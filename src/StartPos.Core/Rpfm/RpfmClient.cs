using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace StartPos.Core.Rpfm;

/// <summary>
/// Minimal client for the RPFM server (rpfm_server.exe, RPFM 5.3 or newer): JSON-RPC 2.0 over a
/// WebSocket at ws://127.0.0.1:45127/ws. Starts the server from the RPFM install folder when none
/// is running; the server exits by itself once its last session disconnects.
/// Methods: https://github.com/Frodo45127/rpfm/blob/master/docs/server/methods.md
/// </summary>
public sealed class RpfmClient : IDisposable
{
    public const int DefaultPort = 45127;

    private readonly ClientWebSocket _ws = new();
    private readonly int _port;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _receiveCancel = new();
    private readonly TaskCompletionSource<long> _sessionConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _nextId;

    public long SessionId { get; private set; }

    private RpfmClient(int port)
    {
        _port = port;
        // Pack listings and table rows arrive as single large text messages.
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
    }

    /// <summary>Connects to a running server, or starts one from <paramref name="rpfmFolder"/> first.</summary>
    public static async Task<RpfmClient> ConnectAsync(string? rpfmFolder, Action<string> log, int port = DefaultPort, CancellationToken ct = default)
    {
        bool started = false;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Could not connect to an RPFM server on port {port} within 60s.");

            if (!await IsListeningAsync(port))
            {
                if (!started)
                {
                    string server = FindServerExecutable(rpfmFolder);
                    log($"Starting RPFM server: {server}");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = server,
                        WorkingDirectory = Path.GetDirectoryName(server)!,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    started = true;
                }
                await Task.Delay(250, ct);
                continue;
            }

            // A server whose last session just left is shutting down and drops new connections:
            // only one that greets with session.connected is kept, else start over.
            var client = new RpfmClient(port);
            try
            {
                await client._ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), ct);
                _ = Task.Run(client.ReceiveLoop);
                client.SessionId = await client._sessionConnected.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                log($"Connected to RPFM server (session {client.SessionId})");
                return client;
            }
            catch (Exception ex) when (ex is WebSocketException or TimeoutException or IOException)
            {
                client.Abort();
                started = false;
                await Task.Delay(500, ct);
            }
        }
    }

    private static string FindServerExecutable(string? rpfmFolder)
    {
        if (!string.IsNullOrWhiteSpace(rpfmFolder))
        {
            string candidate = File.Exists(rpfmFolder) ? rpfmFolder : Path.Combine(rpfmFolder, "rpfm_server.exe");
            if (File.Exists(candidate))
                return candidate;
            throw new FileNotFoundException($"rpfm_server.exe not found in {rpfmFolder} (RPFM 5.3 or newer is required).");
        }
        throw new InvalidOperationException("No RPFM server is running and no RPFM folder was configured to start one from " +
                                            "(set it in the GUI, pass --rpfm, or set the RPFM_FOLDER environment variable).");
    }

    private static async Task<bool> IsListeningAsync(int port)
    {
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(1));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ReceiveLoop()
    {
        var buffer = new byte[1 << 20];
        using var message = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, _receiveCancel.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new WebSocketException("RPFM server closed the connection.");
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var root = JsonNode.Parse(message.GetBuffer().AsSpan(0, (int)message.Length))!;

                // Notifications (no id): session.connected, job.updated, ...
                if (root["id"] is not JsonValue idNode)
                {
                    if (root["method"]?.GetValue<string>() == "session.connected")
                        _sessionConnected.TrySetResult(root["params"]!["session_id"]!.GetValue<long>());
                    continue;
                }

                if (_pending.TryRemove(idNode.GetValue<int>(), out var tcs))
                {
                    if (root["error"] is JsonObject error)
                        tcs.TrySetException(RpfmException.From(error));
                    else
                        tcs.TrySetResult(root["result"]);
                }
            }
        }
        catch (Exception ex)
        {
            _sessionConnected.TrySetException(ex);
            foreach (var key in _pending.Keys)
                if (_pending.TryRemove(key, out var tcs))
                    tcs.TrySetException(ex);
        }
    }

    /// <summary>
    /// Calls a method and returns its result. Methods that run as jobs answer with <c>{"job": n}</c>;
    /// those are waited for, and the job's own result is returned.
    /// </summary>
    public async Task<JsonNode?> CallAsync(string method, JsonObject? parameters = null, CancellationToken ct = default)
    {
        var result = await RequestAsync(method, parameters, ct);
        if (result is not JsonObject { Count: 1 } started || started["job"] is not JsonValue jobNode)
            return result;

        long job = jobNode.GetValue<long>();
        while (true)
        {
            var status = (await RequestAsync("job.wait", new JsonObject { ["job"] = job, ["timeout_secs"] = 600 }, ct))!;
            switch (status["state"]?.GetValue<string>())
            {
                case "finished":
                    return status["result"];
                case "failed":
                    throw status["error"] is JsonObject error ? RpfmException.From(error) : new RpfmException($"{method}: job failed.");
                case "cancelled":
                    throw new RpfmException($"{method}: job cancelled.");
            }
        }
    }

    private async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        int id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters != null)
            request["params"] = parameters;
        byte[] bytes = Encoding.UTF8.GetBytes(request.ToJsonString());
        await _sendLock.WaitAsync(ct);
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }

        using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
        try
        {
            return await tcs.Task;
        }
        catch (RpfmException ex)
        {
            throw new RpfmException($"{method}: {ex.Message}", ex.Kind);
        }
    }

    private void Abort()
    {
        _receiveCancel.Cancel();
        _ws.Dispose();
        _sendLock.Dispose();
    }

    public void Dispose()
    {
        bool last = false;
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}/"), Timeout = TimeSpan.FromSeconds(2) };
                try
                {
                    last = JsonNode.Parse(http.GetStringAsync("sessions").Result) is JsonArray { Count: <= 1 };
                }
                catch
                {
                    // Unknown: don't wait for the server below.
                }
                // Lets the server drop the session now instead of after its 5-minute grace period.
                RequestAsync("session.disconnect", null, CancellationToken.None).Wait(TimeSpan.FromSeconds(2));
                _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch
        {
            // Best effort; the server times the session out on its own (it may also just close the socket).
        }
        Abort();

        // The last session leaving shuts the server down. Wait for that, or the next client
        // connects to the dying server and gets dropped mid-request.
        if (last)
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}/"), Timeout = TimeSpan.FromSeconds(1) };
            for (int i = 0; i < 40; i++)
            {
                try
                {
                    http.GetStringAsync("version").Wait();
                }
                catch
                {
                    break;
                }
                Thread.Sleep(250);
            }
        }
    }
}

/// <summary>An RPFM server error. <see cref="Kind"/> is its <c>data.kind</c>, like <c>file_not_found</c>.</summary>
public sealed class RpfmException(string message, string? kind = null) : Exception(message)
{
    public string? Kind { get; } = kind;

    internal static RpfmException From(JsonObject error) =>
        new(error["message"]?.GetValue<string>() ?? error.ToJsonString(), error["data"]?["kind"]?.GetValue<string>());
}
