using System.Text.Json.Nodes;
using StartPos.Core.Tables;

namespace StartPos.Core.Rpfm;

/// <summary>
/// The pack operations the start_pos tools need, on top of the RPFM server: opening and listing
/// packs, extracting files, reading tables, and building new packs out of tables and files.
/// Pack keys are what RPFM returns (a UUID per open pack).
/// </summary>
public sealed class RpfmSession : IDisposable
{
    public const string Warhammer3 = "warhammer_3";

    /// <summary>File sources of the dependencies (<see cref="DecodeTableAsync"/>'s <c>source</c>).</summary>
    public const string GameFiles = "game_files", ParentFiles = "parent_files";

    /// <summary>"No limit" for the paginated methods.</summary>
    private const int All = 1 << 30;

    private readonly RpfmClient _client;
    private readonly Action<string> _log;
    private readonly Dictionary<string, TableDefinition> _gameDefinitions = new(StringComparer.Ordinal);

    private RpfmSession(RpfmClient client, Action<string> log)
    {
        _client = client;
        _log = log;
    }

    public RpfmClient Client => _client;

    /// <summary>Connects to (or starts) the RPFM server and selects Warhammer III, which loads its schema.</summary>
    public static async Task<RpfmSession> ConnectAsync(string? rpfmFolder, Action<string> log, CancellationToken ct = default)
    {
        var client = await RpfmClient.ConnectAsync(rpfmFolder, log, ct: ct);
        try
        {
            await client.CallAsync("session.set_game", new JsonObject { ["game"] = Warhammer3, ["rebuild_dependencies"] = false }, ct);
            return new RpfmSession(client, log);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static JsonObject Pack(string packKey) => new() { ["pack"] = packKey };

    private static JsonObject FileRef(string packKey, string path, string? source = null) =>
        new() { ["source"] = source == null ? Pack(packKey) : JsonValue.Create(source), ["path"] = path };

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    // ------------------------------------------------------------------ packs

    /// <summary>Opens a pack from disk and returns its key; a pack the session already has open is reused.</summary>
    public async Task<string> OpenPackAsync(string path, CancellationToken ct = default)
    {
        path = Path.GetFullPath(path);
        var status = await _client.CallAsync("session.status", null, ct);
        foreach (var pack in status!["packs"]!.AsArray())
        {
            string? file = pack?["path"]?.GetValue<string>();
            if (file != null && string.Equals(Path.GetFullPath(file), path, StringComparison.OrdinalIgnoreCase))
                return pack!["key"]!.GetValue<string>();
        }
        var opened = await _client.CallAsync("pack.open", new JsonObject { ["paths"] = Strings([path]) }, ct);
        return opened!["key"]!.GetValue<string>();
    }

    public async Task<string> NewPackAsync(CancellationToken ct = default) =>
        (await _client.CallAsync("pack.new", null, ct))!["key"]!.GetValue<string>();

    public async Task ClosePackAsync(string packKey, CancellationToken ct = default) =>
        await _client.CallAsync("pack.close", Pack(packKey), ct);

    public async Task SavePackAsAsync(string packKey, string path, CancellationToken ct = default)
    {
        var request = Pack(packKey);
        request["path"] = Path.GetFullPath(path);
        await _client.CallAsync("pack.save", request, ct);
    }

    /// <summary>Saves an open pack over the file it was opened from.</summary>
    public async Task SavePackAsync(string packKey, CancellationToken ct = default) =>
        await _client.CallAsync("pack.save", Pack(packKey), ct);

    /// <summary>Removes files from an open pack.</summary>
    public async Task DeleteFilesAsync(string packKey, IEnumerable<string> packPaths, CancellationToken ct = default)
    {
        var request = Pack(packKey);
        request["paths"] = Strings(packPaths);
        await _client.CallAsync("files.delete", request, ct);
    }

    /// <summary>In-pack paths of every file in a pack ('/'-separated).</summary>
    public Task<List<string>> ListFilesAsync(string packKey, CancellationToken ct = default) =>
        ListSourceAsync(Pack(packKey), "", ct);

    private async Task<List<string>> ListSourceAsync(JsonNode source, string pathPrefix, CancellationToken ct)
    {
        var list = await _client.CallAsync("files.list", new JsonObject { ["source"] = source, ["path_prefix"] = pathPrefix, ["limit"] = All }, ct);
        return list!["files"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Extracts one file of a pack under <paramref name="destination"/> and returns its local path.</summary>
    public async Task<string> ExtractFileAsync(string packKey, string packPath, string destination, CancellationToken ct = default)
    {
        // The server resolves relative paths against its own folder.
        destination = Path.GetFullPath(destination);
        await _client.CallAsync("files.extract", new JsonObject
        {
            ["source"] = Pack(packKey),
            ["paths"] = Strings([packPath]),
            ["destination"] = destination,
        }, ct);
        string local = Path.Combine(destination, packPath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(local) ? local : throw new RpfmException($"RPFM did not extract {packPath}.");
    }

    /// <summary>
    /// A pack's dependency list: pack file names, each with its checkbox. Only a ticked entry
    /// counts as a dependency (for RPFM's diagnostics, and for which tables the tools read).
    /// </summary>
    public async Task<List<PackDependency>> GetDependenciesAsync(string packKey, CancellationToken ct = default) =>
        (await _client.CallAsync("pack.info", Pack(packKey), ct))!["dependencies"]!.AsArray()
            .Select(d => new PackDependency(d!["name"]!.GetValue<string>(), d["enabled"]?.GetValue<bool>() ?? true)).ToList();

    /// <summary>Sets a pack's dependency list, checkboxes as given.</summary>
    public async Task SetDependenciesAsync(string packKey, IEnumerable<PackDependency> dependencies, CancellationToken ct = default)
    {
        var request = Pack(packKey);
        request["dependencies"] = new JsonArray(dependencies.Select(d => (JsonNode)new JsonObject { ["enabled"] = d.Enabled, ["name"] = d.Name }).ToArray());
        await _client.CallAsync("pack.update", request, ct);
    }

    /// <summary>The open packs of the session: file path (empty for a new, unsaved pack) → pack key.</summary>
    public async Task<Dictionary<string, string>> OpenPacksAsync(CancellationToken ct = default)
    {
        var status = await _client.CallAsync("session.status", null, ct);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in status!["packs"]!.AsArray())
            if (pack?["path"]?.GetValue<string>() is { Length: > 0 } file)
                result.TryAdd(Path.GetFullPath(file), pack["key"]!.GetValue<string>());
        return result;
    }

    /// <summary>The file an open pack was opened from, or null for a new pack.</summary>
    public async Task<string?> PackPathAsync(string packKey, CancellationToken ct = default) =>
        (await OpenPacksAsync(ct)).FirstOrDefault(p => p.Value == packKey).Key;

    /// <summary>Adds a file from disk to a pack at <paramref name="packPath"/>.</summary>
    public async Task AddFileAsync(string packKey, string localPath, string packPath, CancellationToken ct = default)
    {
        var request = Pack(packKey);
        request["paths"] = Strings([Path.GetFullPath(localPath)]);
        request["pack_paths"] = Strings([packPath]);
        var response = await _client.CallAsync("files.add_from_disk", request, ct);
        if (response?["error"] is JsonValue error && error.GetValue<string>() is { Length: > 0 } msg)
            throw new RpfmException($"Adding {packPath}: {msg}");
        if (response?["not_added"] is JsonArray { Count: > 0 })
            throw new RpfmException($"RPFM did not add {packPath}.");
    }

    // ----------------------------------------------------------------- tables

    /// <summary>
    /// Reads a DB file: of the open pack <paramref name="packKey"/>, or of <paramref name="source"/>
    /// (<see cref="GameFiles"/> or <see cref="ParentFiles"/>) when set.
    /// </summary>
    public async Task<(TableDefinition Definition, DbTable Table)> DecodeTableAsync(string packKey, string packPath,
        string? source = null, CancellationToken ct = default)
    {
        var file = FileRef(packKey, packPath, source);
        var info = (await _client.CallAsync("table.info", new JsonObject { ["file"] = file.DeepClone() }, ct))!;
        string folder = info["table_name"]!.GetValue<string>();
        var definition = ToDefinition(folder, info);

        var data = (await _client.CallAsync("table.rows", new JsonObject { ["file"] = file, ["limit"] = All }, ct))!;
        var columns = data["columns"]!.AsArray().Select(c => definition.Field(c!.GetValue<string>())).ToList();
        var result = new DbTable(folder.EndsWith("_tables", StringComparison.Ordinal) ? folder[..^"_tables".Length] : folder);
        foreach (var rowNode in data["rows"]!.AsArray())
        {
            var values = rowNode!["values"]!.AsArray();
            var row = new DbRow();
            for (int c = 0; c < values.Count && c < columns.Count; c++)
                if (columns[c] is { } field)
                    row[field.Name] = DbValue.FromRpfmValue(values[c], field.Type);
            result.Rows.Add(row);
        }
        return (definition, result);
    }

    /// <summary>
    /// Loads RPFM's dependencies (vanilla files, Assembly Kit tables, and the parent packs of the
    /// open packs). Needed to know the table versions the installed game uses and to read vanilla
    /// tables. Reloaded on every call, so packs opened since are covered.
    /// </summary>
    public async Task LoadDependenciesAsync(CancellationToken ct = default)
    {
        _log("Loading RPFM's dependencies...");
        var status = (await _client.CallAsync("dependencies.rebuild", new JsonObject(), ct))!;
        var dependencies = status["dependencies"]!;
        if (dependencies["vanilla_loaded"]?.GetValue<bool>() != true)
            throw new RpfmException("RPFM loaded no Warhammer III game files" +
                                    (dependencies["error"]?.GetValue<string>() is { Length: > 0 } error ? $" ({error})" : "") +
                                    ". In rpfm_ui.exe, set the Warhammer 3 game folder in PackFile > Settings, " +
                                    "then run Game Selected > Generate Dependencies Cache.");
    }

    /// <summary>In-pack paths of the DB files of a dependency source (<see cref="GameFiles"/> or <see cref="ParentFiles"/>).</summary>
    public Task<List<string>> ListDependencyTablesAsync(string source, CancellationToken ct = default) =>
        ListSourceAsync(JsonValue.Create(source), "db/", ct);

    /// <summary>
    /// The definition of a table at the version the installed game uses. The game's packs ship only
    /// some start_pos tables (the rest are Assembly Kit-only); for the others RPFM gives the schema's
    /// newest definition.
    /// </summary>
    public async Task<TableDefinition> GameDefinitionAsync(string tableFolder, CancellationToken ct = default)
    {
        if (_gameDefinitions.TryGetValue(tableFolder, out var cached))
            return cached;
        var raw = await _client.CallAsync("schema.definition", new JsonObject { ["table_name"] = tableFolder }, ct);
        var definition = ToDefinition(tableFolder, raw!);
        _gameDefinitions[tableFolder] = definition;
        return definition;
    }

    /// <summary>Creates <c>db/&lt;table&gt;_tables/&lt;fragment&gt;</c> in a pack and fills it with the table's rows.</summary>
    public async Task WriteTableAsync(string packKey, DbTable table, string fragment, CancellationToken ct = default)
    {
        var definition = await GameDefinitionAsync(table.Folder, ct);
        string path = $"db/{table.Folder}/{fragment}";
        var request = Pack(packKey);
        request["path"] = path;
        request["kind"] = new JsonObject { ["type"] = "db", ["table_name"] = table.Folder, ["version"] = definition.Version };
        await _client.CallAsync("files.create", request, ct);
        await ReplaceTableRowsAsync(packKey, path, table.Rows, ct);
    }

    /// <summary>
    /// Replaces all rows of an existing DB file of an open pack. The file keeps its own table
    /// version; columns a row does not have are written as the column type's empty value.
    /// </summary>
    public async Task ReplaceTableRowsAsync(string packKey, string packPath, IEnumerable<DbRow> rows, CancellationToken ct = default)
    {
        var info = (await _client.CallAsync("table.info", new JsonObject { ["file"] = FileRef(packKey, packPath) }, ct))!;
        var definition = ToDefinition(info["table_name"]!.GetValue<string>(), info);
        int existing = info["row_count"]!.GetValue<int>();

        var edits = new JsonArray();
        if (existing > 0)
            edits.Add(new JsonObject { ["op"] = "delete", ["indexes"] = new JsonArray(Enumerable.Range(0, existing).Select(i => (JsonNode?)i).ToArray()) });
        foreach (var row in rows)
        {
            // Every column is set: ones left out would get the schema's default value, not an empty one.
            var values = new JsonObject();
            foreach (var field in definition.Fields)
                values[field.Name] = DbValue.ToRpfmValue(row.GetValueOrDefault(field.Name), field.Type);
            edits.Add(new JsonObject { ["op"] = "insert", ["values"] = values });
        }

        var request = Pack(packKey);
        request["path"] = packPath;
        request["edits"] = edits;
        await _client.CallAsync("table.edit", request, ct);
    }

    /// <summary>A definition from <c>table.info</c> or <c>schema.definition</c>; its columns are in row order.</summary>
    private static TableDefinition ToDefinition(string tableFolder, JsonNode info) =>
        new(tableFolder, info["version"]!.GetValue<int>(), info["columns"]!.AsArray().Select(f => new FieldDef(
            f!["name"]!.GetValue<string>(),
            f["field_type"] is JsonValue t ? t.GetValue<string>() : ((JsonObject)f["field_type"]!).First().Key,
            f["is_key"]!.GetValue<bool>())).ToList());

    public void Dispose() => _client.Dispose();
}

/// <summary>An entry of a pack's dependency list: a pack file name and its checkbox.</summary>
public sealed record PackDependency(string Name, bool Enabled);
