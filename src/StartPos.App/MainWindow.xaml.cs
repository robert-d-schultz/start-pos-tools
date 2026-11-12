using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StartPos.Core;
using StartPos.Core.Compare;
using StartPos.Core.Decompile;
using StartPos.Core.Esf;
using StartPos.Core.Model;
using StartPos.Core.Rpfm;
using StartPos.Core.Tables;

namespace StartPos.App;

public partial class MainWindow : Window
{
    private const string InputFilter = "Startpos files and packs|*.esf;*.pack|All files|*.*";

    private readonly ToolSettings _settings = ToolSettings.Load();
    private RpfmSession? _rpfm;
    private string? _rpfmFolder;
    private CancellationTokenSource? _cancellation;
    private string? _lastOutput;
    private string? _lastCompareOutput;

    /// <summary>The input each campaign drop-down was last filled from.</summary>
    private readonly Dictionary<ComboBox, string> _listedInputs = [];

    /// <summary>Compare's campaign drop-downs → the drop-down saying what their pack is read for (startpos.esf or tables).</summary>
    private readonly Dictionary<ComboBox, ComboBox> _sources = [];

    /// <summary>Set while a source drop-down is changed from code, so that is not taken as the user's choice.</summary>
    private bool _settingSource;

    public MainWindow()
    {
        InitializeComponent();
        RpfmFolderBox.Text = _settings.RpfmFolder ?? "";
        InputBox.Text = _settings.DecompileInput ?? "";
        CampaignCombo.Text = _settings.DecompileCampaign ?? "";
        OutputBox.Text = _settings.DecompileOutput ?? "";
        NameBox.Text = _settings.DecompileName ?? "";
        LeftBox.Text = _settings.CompareLeft ?? "";
        LeftCampaignCombo.Text = _settings.CompareLeftCampaign ?? "";
        RightBox.Text = _settings.CompareRight ?? "";
        RightCampaignCombo.Text = _settings.CompareRightCampaign ?? "";
        CompareOutputBox.Text = _settings.CompareOutput ?? "";
        IgnoreCampaignBox.IsChecked = _settings.CompareIgnoreCampaign;
        OnlyBuiltBox.IsChecked = _settings.CompareOnlyBuilt;
        _sources[LeftCampaignCombo] = LeftSourceCombo;
        _sources[RightCampaignCombo] = RightSourceCombo;
        SetSource(LeftSourceCombo, _settings.CompareLeftTables, !IsEsf(LeftBox.Text));
        SetSource(RightSourceCombo, _settings.CompareRightTables, !IsEsf(RightBox.Text));
    }

    // ------------------------------------------------------------------ decompile

    private async void RunDecompile(object sender, RoutedEventArgs e)
    {
        string input = InputBox.Text.Trim().Trim('"');
        if (!File.Exists(input))
        {
            MessageBox.Show(this, "Pick the input: a .pack with campaigns/<campaign>/startpos.esf, or a startpos.esf.", Title);
            return;
        }
        string? campaign = OrNull(CampaignCombo.Text);
        string output = OrNull(OutputBox.Text) ?? DefaultOutput(input, campaign);
        OutputBox.Text = output;
        string? name = OrNull(NameBox.Text);
        SaveSettings();

        ResultGrid.Visibility = Visibility.Collapsed;
        DecompileResult? result = null;
        await RunToolAsync("Decompiling", async (rpfm, ct) =>
        {
            result = await DecompileJob.RunAsync(rpfm, new DecompileOptions
            {
                Input = input,
                Output = output,
                Campaign = campaign,
                Name = name,
            }, Log, ct);
        });
        if (result == null)
            return;

        _lastOutput = result.Output;
        OpenOutputButton.IsEnabled = true;
        ResultGrid.ItemsSource = result.Tables.All.Where(t => t.Rows.Count > 0)
            .Select(t => new { Table = t.Name, Rows = t.Rows.Count }).ToList();
        ResultGrid.Visibility = Visibility.Visible;
        LogReport(result.Report);
        SetStatus($"Decompiled {result.Campaign}: {result.Tables.RowCount} rows in {result.Tables.All.Count(t => t.Rows.Count > 0)} tables" +
                  (result.Report.Warnings.Count > 0 ? $", {result.Report.Warnings.Count} warning(s) (see the log)" : ""));
    }

    private async void ListCampaigns(object sender, RoutedEventArgs e) => await FillCampaignsAsync(InputBox, CampaignCombo);

    private async void OnInputChanged(object sender, RoutedEventArgs e) => await FillCampaignsIfChangedAsync(InputBox, CampaignCombo);

    private void OnCampaignChanged(object sender, SelectionChangedEventArgs e)
    {
        // A different campaign usually means a different output; drop an output that was the default.
        if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is string old &&
            OutputBox.Text == DefaultOutput(InputBox.Text.Trim(), old))
            OutputBox.Text = "";
    }

    private static string DefaultOutput(string input, string? campaign)
    {
        string name = Path.GetFileNameWithoutExtension(input);
        if (IsEsf(input))
            name = campaign ?? Path.GetFileName(Path.GetDirectoryName(input)) ?? name;    // campaigns/<campaign>/startpos.esf
        else if (campaign != null)
            name += "_" + campaign;
        return Path.Combine(Path.GetDirectoryName(input) ?? "", name + "_decompiled.pack");
    }

    private void OpenOutput(object sender, RoutedEventArgs e) => ShowInExplorer(_lastOutput);

    // ------------------------------------------------------------------ compare

    private async void RunCompare(object sender, RoutedEventArgs e)
    {
        string left = LeftBox.Text.Trim().Trim('"'), right = RightBox.Text.Trim().Trim('"');
        if (!File.Exists(left) || !File.Exists(right))
        {
            MessageBox.Show(this, "Pick the two sides to compare: a startpos.esf, a pack with campaigns/<campaign>/startpos.esf, or a pack of start_pos tables.", Title);
            return;
        }
        string? leftCampaign = OrNull(LeftCampaignCombo.Text), rightCampaign = OrNull(RightCampaignCombo.Text);
        // The default is not put in the box: it follows the inputs, so it is worked out on every run.
        string output = OrNull(CompareOutputBox.Text) ?? DefaultCompareOutput(left, leftCampaign, right, rightCampaign);
        bool ignoreCampaign = IgnoreCampaignBox.IsChecked == true;
        bool onlyBuilt = OnlyBuiltBox.IsChecked == true;
        bool leftTables = ReadsTables(LeftSourceCombo), rightTables = ReadsTables(RightSourceCombo);
        SaveSettings();

        CompareGrid.Visibility = Visibility.Collapsed;
        CompareResult? result = null;
        await RunToolAsync("Comparing", async (rpfm, ct) =>
        {
            result = await CompareJob.RunAsync(rpfm, new CompareOptions
            {
                Left = left,
                Right = right,
                DecompileLeft = !leftTables,
                DecompileRight = !rightTables,
                LeftCampaign = leftCampaign,
                RightCampaign = rightCampaign,
                IgnoreCampaign = ignoreCampaign,
                OnlyBuilt = onlyBuilt,
                Output = output,
            }, Log, ct);
        });
        if (result == null)
            return;

        Log(CompareJob.Format(result));
        _lastCompareOutput = result.Output;
        OpenCompareOutputButton.IsEnabled = result.Output != null;
        CompareGrid.ItemsSource = result.Diffs.Where(d => d.LeftRows + d.RightRows > 0).Select(d => new
        {
            d.Table,
            d.LeftRows,
            d.RightRows,
            OnlyLeft = d.OnlyLeft.Count,
            OnlyRight = d.OnlyRight.Count,
            Columns = string.Join(", ", d.ColumnMismatches.Select(c => $"{c.Key} ({c.Value.Count})")),
        }).ToList();
        CompareGrid.Visibility = Visibility.Visible;
        int differing = result.Diffs.Count(d => !d.Identical);
        SetStatus(result.Identical ? "The two sides match." :
            $"{differing} table(s) differ; the differing rows are in {Path.GetFileName(result.Output)}.");
    }

    private async void ListLeftCampaigns(object sender, RoutedEventArgs e) => await FillCampaignsAsync(LeftBox, LeftCampaignCombo);

    private async void ListRightCampaigns(object sender, RoutedEventArgs e) => await FillCampaignsAsync(RightBox, RightCampaignCombo);

    private async void OnLeftChanged(object sender, RoutedEventArgs e) => await FillCampaignsIfChangedAsync(LeftBox, LeftCampaignCombo);

    private async void OnRightChanged(object sender, RoutedEventArgs e) => await FillCampaignsIfChangedAsync(RightBox, RightCampaignCombo);

    /// <summary>Next to the left input, named after the two campaigns (or files).</summary>
    private static string DefaultCompareOutput(string left, string? leftCampaign, string right, string? rightCampaign)
    {
        string a = leftCampaign ?? Path.GetFileNameWithoutExtension(left), b = rightCampaign ?? Path.GetFileNameWithoutExtension(right);
        string name = a.Equals(b, StringComparison.OrdinalIgnoreCase) ? $"{a}_compare" : $"{a}_vs_{b}";
        return Path.Combine(Path.GetDirectoryName(left) ?? "", name + ".pack");
    }

    private void OpenCompareOutput(object sender, RoutedEventArgs e) => ShowInExplorer(_lastCompareOutput);

    // ------------------------------------------------------------------ campaigns

    private async Task FillCampaignsIfChangedAsync(TextBox input, ComboBox combo)
    {
        string path = input.Text.Trim().Trim('"');
        if (path != _listedInputs.GetValueOrDefault(combo) && (IsEsf(path) || IsPack(path)))
            await FillCampaignsAsync(input, combo);
    }

    /// <summary>
    /// Fills a campaign drop-down with the campaigns an input has a startpos for. A .esf is
    /// read directly (its campaign sits outside the compressed part); a pack's startpos files
    /// are listed through RPFM. Each startpos.esf is identified by the campaign recorded in it,
    /// so a copy in another campaign's folder (IEE's wh3_main_combi one, there for multiplayer)
    /// is not listed as a campaign of its own. On the Compare tab a pack can be read for its
    /// start_pos tables instead: the source drop-down is set from what the pack holds (left as
    /// it is when it holds both), and the campaigns are then those the tables are for.
    /// </summary>
    private async Task FillCampaignsAsync(TextBox input, ComboBox combo)
    {
        string path = input.Text.Trim().Trim('"');
        if (!File.Exists(path) || !(IsEsf(path) || IsPack(path)))
            return;
        string current = combo.Text;
        var source = _sources.GetValueOrDefault(combo);
        bool tables = false;
        List<string>? campaigns;
        if (IsEsf(path))
        {
            if (source != null)
                SetSource(source, tables: false, enabled: false);
            campaigns = await ReadEsfCampaignAsync(path);
        }
        else
        {
            var listed = await ListPackCampaignsAsync(path, source != null, source != null && ReadsTables(source));
            if (listed == null)
                return;
            (campaigns, tables) = listed.Value;
            if (source != null)
                SetSource(source, tables, enabled: true);
        }
        if (campaigns == null)
            return;
        _listedInputs[combo] = path;
        combo.ItemsSource = campaigns;
        combo.Text = campaigns.FirstOrDefault(c => c.Equals(current, StringComparison.OrdinalIgnoreCase)) ?? campaigns.FirstOrDefault() ?? "";
        string name = Path.GetFileName(path);
        SetStatus(tables
            ? campaigns.Count switch
            {
                0 => $"{name}'s start_pos tables name no campaign.",
                1 => $"{name} has start_pos tables for {campaigns[0]}.",
                _ => $"{name} has start_pos tables for {campaigns.Count} campaigns.",
            }
            : campaigns.Count switch
            {
                0 => $"{name} has no campaigns/<campaign>/startpos.esf.",
                1 => $"{name} is a startpos for {campaigns[0]}.",
                _ => $"{name} has startpos files for {campaigns.Count} campaigns.",
            });
    }

    /// <summary>The user picked what a pack is read for: its campaigns are listed again.</summary>
    private async void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingSource || !IsLoaded)
            return;
        var (combo, input) = sender == LeftSourceCombo ? (LeftCampaignCombo, LeftBox) : (RightCampaignCombo, RightBox);
        if (!IsPack(input.Text.Trim().Trim('"')))
            return;
        _listedInputs.Remove(combo);
        await FillCampaignsAsync(input, combo);
    }

    private static bool ReadsTables(ComboBox source) => source.SelectedIndex == 1;

    private void SetSource(ComboBox source, bool tables, bool enabled)
    {
        _settingSource = true;
        source.SelectedIndex = tables ? 1 : 0;
        source.IsEnabled = enabled;
        _settingSource = false;
    }

    private async Task<List<string>?> ReadEsfCampaignAsync(string path)
    {
        try
        {
            string campaign = await Task.Run(() => StartPosEsf.ReadCampaign(EsfDocument.Load(path, decompress: false)));
            Log($"{path}: campaign {campaign}");
            return [campaign];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EsfFormatException)
        {
            Log($"ERROR: {path}: {ex.Message}");
            SetStatus($"Could not read {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The campaigns of a pack: those it has a startpos.esf for, or with <paramref name="mayReadTables"/>,
    /// those its start_pos tables are for when it has tables and no startpos.esf (or both, and
    /// <paramref name="preferTables"/>). Tables says which it was.
    /// </summary>
    private async Task<(List<string> Campaigns, bool Tables)?> ListPackCampaignsAsync(string path, bool mayReadTables, bool preferTables)
    {
        List<string> campaigns = [];
        bool tables = false;
        bool done = await RunToolAsync("Reading the pack", async (rpfm, ct) =>
        {
            string key = await rpfm.OpenPackAsync(path, ct);
            string work = Path.Combine(Path.GetTempPath(), "startpos_tools", Guid.NewGuid().ToString("N"));
            try
            {
                if (mayReadTables)
                {
                    var files = await rpfm.ListFilesAsync(key, ct);
                    bool hasTables = PackTables.HasStartPosTables(files);
                    bool hasEsf = files.Any(f => f.StartsWith("campaigns/", StringComparison.Ordinal) &&
                                                 f.EndsWith("/" + DecompileJob.StartPosFileName, StringComparison.Ordinal));
                    if (hasTables && (!hasEsf || preferTables))
                    {
                        tables = true;
                        campaigns = await PackTables.CampaignsAsync(rpfm, key, files, ct);
                        Log($"{path}: start_pos tables for {(campaigns.Count > 0 ? string.Join(", ", campaigns) : "no campaign")}" +
                            (hasEsf ? " (it also has a startpos.esf)" : ""));
                        return;
                    }
                }
                var listing = await StartPosListing.LoadAsync(rpfm, key, work, ct);
                foreach (var file in listing.Files)
                    Log($"{file.PackPath}: campaign {file.Campaign}");
                foreach (string note in listing.Notes)
                    Log(note);
                campaigns = listing.Campaigns.Select(f => f.Campaign).ToList();
            }
            finally
            {
                try
                {
                    await rpfm.ClosePackAsync(key);
                }
                catch (RpfmException)
                {
                }
                try
                {
                    if (Directory.Exists(work))
                        Directory.Delete(work, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        });
        return done ? (campaigns, tables) : null;
    }

    private static bool IsEsf(string path) => path.EndsWith(".esf", StringComparison.OrdinalIgnoreCase);

    private static bool IsPack(string path) => path.EndsWith(".pack", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ running

    /// <summary>
    /// Runs a tool off the UI thread with the shared RPFM session, the busy indicator and
    /// cancellation. A lost connection drops the session so the next run starts a new one.
    /// False when it did not run to the end (another tool was running, it failed, or was cancelled).
    /// </summary>
    private async Task<bool> RunToolAsync(string what, Func<RpfmSession, CancellationToken, Task> work)
    {
        if (_cancellation != null)
            return false;
        _cancellation = new CancellationTokenSource();
        var ct = _cancellation.Token;
        SetBusy(true, what + "...");
        var started = Stopwatch.StartNew();
        string rpfmFolder = RpfmFolderBox.Text.Trim().Trim('"');
        try
        {
            await Task.Run(async () =>
            {
                var rpfm = await SessionAsync(rpfmFolder, ct);
                await work(rpfm, ct);
            }, ct);
            Log($"{what} done in {started.Elapsed.TotalSeconds:0.0} s");
            SetStatus($"{what} done.");
            return true;
        }
        catch (OperationCanceledException)
        {
            Log($"{what} cancelled.");
            SetStatus("Cancelled.");
            DropSession();   // the server may still be busy with the cancelled request
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
            SetStatus($"{what} failed: {ex.Message}");
            if (ex is WebSocketException or TimeoutException or IOException)
                DropSession();
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            SetBusy(false, null);
        }
        return false;
    }

    private async Task<RpfmSession> SessionAsync(string rpfmFolder, CancellationToken ct)
    {
        if (_rpfm != null && _rpfmFolder == rpfmFolder)
            return _rpfm;
        DropSession();
        _rpfm = await RpfmSession.ConnectAsync(rpfmFolder.Length > 0 ? rpfmFolder : ToolSettings.ResolveRpfmFolder(null), Log, ct);
        _rpfmFolder = rpfmFolder;
        return _rpfm;
    }

    private void DropSession()
    {
        _rpfm?.Dispose();
        _rpfm = null;
    }

    private void Cancel(object sender, RoutedEventArgs e) => _cancellation?.Cancel();

    private void SetBusy(bool busy, string? status)
    {
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
        CancelButton.IsEnabled = busy;
        DecompileButton.IsEnabled = !busy;
        CompareButton.IsEnabled = !busy;
        ListCampaignsButton.IsEnabled = !busy;
        ListLeftButton.IsEnabled = !busy;
        ListRightButton.IsEnabled = !busy;
        if (status != null)
            SetStatus(status);
    }

    private void SetStatus(string text) => StatusText.Text = text;

    /// <summary>Appends a line to the log; callable from any thread.</summary>
    private void Log(string line) => Dispatcher.BeginInvoke(() =>
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    });

    private void LogReport(DecompileReport report)
    {
        foreach (string note in report.Notes)
            Log($"note: {note}");
        foreach (string warning in report.Warnings)
            Log($"warning: {warning}");
    }

    private void ClearLog(object sender, RoutedEventArgs e) => LogBox.Clear();

    private void CopyLog(object sender, RoutedEventArgs e) => Clipboard.SetText(LogBox.Text);

    private static void ShowInExplorer(string? file)
    {
        if (file != null && File.Exists(file))
            Process.Start("explorer.exe", $"/select,\"{file}\"");
    }

    // ------------------------------------------------------------------ browsing

    private void BrowseRpfmFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "RPFM folder (with rpfm_server.exe)" };
        if (Directory.Exists(RpfmFolderBox.Text))
            dialog.InitialDirectory = RpfmFolderBox.Text;
        if (dialog.ShowDialog(this) == true)
        {
            RpfmFolderBox.Text = dialog.FolderName;
            if (!File.Exists(Path.Combine(dialog.FolderName, "rpfm_server.exe")))
                MessageBox.Show(this, "That folder has no rpfm_server.exe. Pick the RPFM 5.3 folder.", Title);
        }
    }

    private async void BrowseInput(object sender, RoutedEventArgs e)
    {
        if (PickFile(InputBox, InputFilter) is null)
            return;
        OutputBox.Text = "";
        await PickedInputAsync(InputBox, CampaignCombo);
    }

    private void BrowseOutput(object sender, RoutedEventArgs e) => PickOutput(OutputBox, InputBox);

    private async void BrowseLeft(object sender, RoutedEventArgs e)
    {
        if (PickFile(LeftBox, InputFilter) is not null)
            await PickedInputAsync(LeftBox, LeftCampaignCombo);
    }

    private async void BrowseRight(object sender, RoutedEventArgs e)
    {
        if (PickFile(RightBox, InputFilter) is not null)
            await PickedInputAsync(RightBox, RightCampaignCombo);
    }

    private void BrowseCompareOutput(object sender, RoutedEventArgs e) => PickOutput(CompareOutputBox, LeftBox);

    /// <summary>A new input: its campaigns replace the old input's.</summary>
    private async Task PickedInputAsync(TextBox input, ComboBox combo)
    {
        combo.ItemsSource = null;
        combo.Text = "";
        _listedInputs.Remove(combo);
        await FillCampaignsAsync(input, combo);
    }

    private string? PickFile(TextBox box, string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter };
        string? folder = Path.GetDirectoryName(box.Text);
        if (Directory.Exists(folder))
            dialog.InitialDirectory = folder;
        if (dialog.ShowDialog(this) != true)
            return null;
        box.Text = dialog.FileName;
        return dialog.FileName;
    }

    private void PickOutput(TextBox box, TextBox nextTo)
    {
        var dialog = new SaveFileDialog { Filter = "Pack|*.pack", FileName = Path.GetFileName(box.Text) };
        string? folder = Path.GetDirectoryName(OrNull(box.Text) ?? nextTo.Text);
        if (Directory.Exists(folder))
            dialog.InitialDirectory = folder;
        if (dialog.ShowDialog(this) == true)
            box.Text = dialog.FileName;
    }

    // ------------------------------------------------------------------ settings

    private void SaveSettings()
    {
        _settings.RpfmFolder = OrNull(RpfmFolderBox.Text);
        _settings.DecompileInput = OrNull(InputBox.Text);
        _settings.DecompileCampaign = OrNull(CampaignCombo.Text);
        _settings.DecompileOutput = OrNull(OutputBox.Text);
        _settings.DecompileName = OrNull(NameBox.Text);
        _settings.CompareLeft = OrNull(LeftBox.Text);
        _settings.CompareLeftCampaign = OrNull(LeftCampaignCombo.Text);
        _settings.CompareRight = OrNull(RightBox.Text);
        _settings.CompareRightCampaign = OrNull(RightCampaignCombo.Text);
        _settings.CompareOutput = OrNull(CompareOutputBox.Text);
        _settings.CompareIgnoreCampaign = IgnoreCampaignBox.IsChecked == true;
        _settings.CompareOnlyBuilt = OnlyBuiltBox.IsChecked == true;
        _settings.CompareLeftTables = ReadsTables(LeftSourceCombo);
        _settings.CompareRightTables = ReadsTables(RightSourceCombo);
        try
        {
            _settings.Save();
        }
        catch (IOException ex)
        {
            Log($"Could not save the settings: {ex.Message}");
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _cancellation?.Cancel();
        SaveSettings();
        DropSession();
    }

    private static string? OrNull(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim().Trim('"');
}
