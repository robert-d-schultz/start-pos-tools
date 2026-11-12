using System.Text;
using StartPos.Core.Decompile;
using StartPos.Core.Model;
using StartPos.Core.Rpfm;
using StartPos.Core.Tables;

namespace StartPos.Core.Compare;

public sealed class CompareOptions
{
    /// <summary>A startpos.esf, a pack whose startpos.esf is decompiled (<see cref="DecompileLeft"/>), or a start_pos .pack.</summary>
    public required string Left { get; init; }

    /// <summary>The same for the other side.</summary>
    public required string Right { get; init; }

    /// <summary>For a left .pack: decompile its startpos.esf rather than read its start_pos tables.</summary>
    public bool DecompileLeft { get; init; }

    /// <summary>For a right .pack: decompile its startpos.esf rather than read its start_pos tables.</summary>
    public bool DecompileRight { get; init; }

    /// <summary>
    /// The left side's campaign: which startpos of a pack to decompile, or which campaign's rows of
    /// a start_pos pack to compare. A decompiled side's own campaign is used when it is not given;
    /// a start_pos pack takes the other side's.
    /// </summary>
    public string? LeftCampaign { get; init; }

    /// <summary>The same for the right side.</summary>
    public string? RightCampaign { get; init; }

    /// <summary>Leave the campaign column out of matching and comparing, to compare two campaigns' startpos files.</summary>
    public bool IgnoreCampaign { get; init; }

    /// <summary>
    /// A .pack to write the differing rows to: per table, db/&lt;table&gt;_tables/left_&lt;campaign&gt;
    /// holds left's rows that right does not have as they are, and right_&lt;campaign&gt; the reverse.
    /// </summary>
    public string? Output { get; init; }

    /// <summary>
    /// Compare only what a built startpos keeps: start_pos tables are compared as a build would
    /// leave them, and against a decompiled startpos what the build decided by itself does not
    /// count (see <see cref="BuiltForm"/>). Off: every column of every row.
    /// </summary>
    public bool OnlyBuilt { get; init; } = true;

    /// <summary>Read the game's tables so defaults are left out as a decompile would.</summary>
    public bool UseGameTables { get; init; } = true;

    /// <summary>Examples kept per mismatching column.</summary>
    public int MaxExamples { get; init; } = 5;
}

public sealed record CompareResult(
    string? LeftCampaign, string? RightCampaign, List<TableDiffResult> Diffs,
    DecompileReport? LeftReport, DecompileReport? RightReport, StartPosTables Left, StartPosTables Right, string? Output)
{
    public bool Identical => Diffs.All(d => d.Identical);
}

/// <summary>Compares the start_pos tables of two startpos files (decompiled) or packs.</summary>
public static class CompareJob
{
    /// <summary>The column holding the campaign key, in the tables that have one.</summary>
    public const string CampaignColumn = "campaign";

    public static async Task<CompareResult> RunAsync(RpfmSession rpfm, CompareOptions options, Action<string> log, CancellationToken ct = default)
    {
        var left = await LoadAsync(rpfm, options.Left, options.DecompileLeft, options.LeftCampaign, options, log, ct);
        var right = await LoadAsync(rpfm, options.Right, options.DecompileRight, options.RightCampaign, options, log, ct);
        string? leftCampaign = left.Campaign ?? options.LeftCampaign;
        string? rightCampaign = right.Campaign ?? options.RightCampaign;
        leftCampaign ??= rightCampaign;
        rightCampaign ??= leftCampaign;
        if (leftCampaign != null)
            CampaignScope.Apply(left.Tables, leftCampaign);
        if (rightCampaign != null)
            CampaignScope.Apply(right.Tables, rightCampaign);
        log(leftCampaign == rightCampaign ? $"Comparing campaign {leftCampaign ?? "(all)"}" : $"Comparing campaign {leftCampaign} with {rightCampaign}");
        if (leftCampaign != rightCampaign && !options.IgnoreCampaign)
            log($"The campaigns differ, so the {CampaignColumn} column differs in every row that has one; ignore the campaign key to compare the rest.");

        var leftCompared = left.Tables;
        var rightCompared = right.Tables;
        var originals = new Dictionary<DbRow, DbRow>(ReferenceEqualityComparer.Instance);
        if (options.OnlyBuilt)
        {
            foreach (string note in BuiltForm.Notes)
                log(note);
            leftCompared = BuiltForm.Apply(left.Tables, left.Game, originals);
            rightCompared = BuiltForm.Apply(right.Tables, right.Game, originals);
            if (left.Decompiled && !right.Decompiled)
                BuiltForm.Reconcile(leftCompared, rightCompared);
            else if (right.Decompiled && !left.Decompiled)
                BuiltForm.Reconcile(rightCompared, leftCompared);
        }

        string[] ignore = options.IgnoreCampaign ? [CampaignColumn] : [];
        var diffs = TableDiff.Compare(leftCompared, rightCompared, options.MaxExamples, ignore);
        // The differing rows go out as the inputs hold them, not as they were compared.
        foreach (var diff in diffs)
            foreach (var rows in (List<DbRow>[])[diff.LeftDiffering, diff.RightDiffering])
                for (int i = 0; i < rows.Count; i++)
                    rows[i] = originals.GetValueOrDefault(rows[i], rows[i]);
        string? output = null;
        if (options.Output != null)
            output = await WriteDifferencesAsync(rpfm, diffs, options.Output, leftCampaign ?? "", rightCampaign ?? "", log, ct);
        return new CompareResult(leftCampaign, rightCampaign, diffs, left.Report, right.Report, left.Tables, right.Tables, output);
    }

    /// <summary>The fragment the differing rows of one side go to: "left_&lt;campaign&gt;" or "right_&lt;campaign&gt;".</summary>
    public static string Fragment(string side, string campaign) => campaign.Length > 0 ? $"{side}_{campaign}" : side;

    /// <summary>Writes each side's differing rows to a new pack; nothing is written when the sides match.</summary>
    private static async Task<string?> WriteDifferencesAsync(RpfmSession rpfm, List<TableDiffResult> diffs, string output,
        string leftCampaign, string rightCampaign, Action<string> log, CancellationToken ct)
    {
        if (diffs.All(d => d.LeftDiffering.Count == 0 && d.RightDiffering.Count == 0))
        {
            log("No differing rows, so no pack was written.");
            return null;
        }
        string pack = await rpfm.NewPackAsync(ct);
        try
        {
            foreach (var diff in diffs)
            {
                foreach (var (fragment, rows) in new[]
                         {
                             (Fragment("left", leftCampaign), diff.LeftDiffering),
                             (Fragment("right", rightCampaign), diff.RightDiffering),
                         })
                {
                    if (rows.Count == 0)
                        continue;
                    var table = new DbTable(diff.Table);
                    table.Rows.AddRange(rows);
                    log($"Writing {table.Folder}/{fragment} ({rows.Count} rows)");
                    await rpfm.WriteTableAsync(pack, table, fragment, ct);
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await rpfm.SavePackAsAsync(pack, output, ct);
            log($"Saved {output}");
            return output;
        }
        finally
        {
            await TryCloseAsync(rpfm, pack);
        }
    }

    /// <summary>One side: its tables, its campaign (when decompiled), and the game as its pack sees it (when read).</summary>
    private sealed record Loaded(StartPosTables Tables, string? Campaign, DecompileReport? Report, bool Decompiled, GameLookups? Game);

    private static async Task<Loaded> LoadAsync(RpfmSession rpfm, string path, bool decompile, string? campaign, CompareOptions options,
        Action<string> log, CancellationToken ct)
    {
        path = Path.GetFullPath(path);
        bool isEsf = Path.GetExtension(path).Equals(".esf", StringComparison.OrdinalIgnoreCase);
        bool useGame = options.UseGameTables;
        if (!isEsf && !decompile)
        {
            log($"Reading tables of {Path.GetFileName(path)}");
            var pack = await PackTables.LoadAsync(rpfm, path, t => t.StartsWith("start_pos_", StringComparison.Ordinal), ct);
            try
            {
                foreach (var (file, error) in pack.DecodeErrors)
                    log($"  could not decode {file}: {error}");
                // The build's view of the game is the pack's: its own tables and its dependencies' count.
                GameLookups? game = null;
                if (useGame && options.OnlyBuilt)
                {
                    log("Reading game tables");
                    game = await GameLookups.LoadAsync(rpfm, pack.PackKey, log, ct);
                }
                return new Loaded(pack.Effective, null, null, false, game);
            }
            finally
            {
                await TryCloseAsync(rpfm, pack.PackKey);
            }
        }

        // The game's tables are read in the context of the pack being decompiled (its own and
        // its dependencies' tables count); for a loose .esf, vanilla only.
        string packKey = isEsf ? await rpfm.NewPackAsync(ct) : await rpfm.OpenPackAsync(path, ct);
        string work = Path.Combine(Path.GetTempPath(), "startpos_tools", Guid.NewGuid().ToString("N"));
        try
        {
            GameLookups? lookups = null;
            if (useGame)
            {
                log("Reading game tables");
                lookups = await GameLookups.LoadAsync(rpfm, packKey, log, ct);
            }
            if (isEsf)
                return Decompile(path, lookups, log);

            var listing = await StartPosListing.LoadAsync(rpfm, packKey, work, ct);
            foreach (string note in listing.Notes)
                log(note);
            return Decompile(listing.Find(campaign).LocalPath, lookups, log);
        }
        finally
        {
            await TryCloseAsync(rpfm, packKey);
            try
            {
                if (Directory.Exists(work))
                    Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task TryCloseAsync(RpfmSession rpfm, string packKey)
    {
        try
        {
            await rpfm.ClosePackAsync(packKey);
        }
        catch (RpfmException)
        {
            // Closing is housekeeping; the server drops the session's packs on disconnect anyway.
        }
    }

    private static Loaded Decompile(string esfPath, GameLookups? lookups, Action<string> log)
    {
        log($"Decompiling {esfPath}");
        var esf = StartPosEsf.Load(esfPath);
        var (tables, report) = StartPosDecompiler.Decompile(esf, lookups);
        return new Loaded(tables, esf.Campaign, report, true, lookups);
    }

    /// <summary>The comparison as text: a line per table, then mismatching columns and unmatched rows.</summary>
    public static string Format(CompareResult result, int maxRows = 5)
    {
        var text = new StringBuilder();
        if (result.LeftCampaign != result.RightCampaign)
            text.AppendLine($"left: {result.LeftCampaign}, right: {result.RightCampaign}");
        foreach (var diff in result.Diffs)
        {
            text.AppendLine($"{(diff.Identical ? "  " : "* ")}{diff.Table}: {diff.LeftRows} vs {diff.RightRows} rows, {diff.Matched} matched, " +
                            $"{diff.OnlyLeft.Count} only left, {diff.OnlyRight.Count} only right");
            foreach (var (column, (count, examples)) in diff.ColumnMismatches)
            {
                text.AppendLine($"      {column}: {count} differ");
                foreach (string e in examples)
                    text.AppendLine($"          {e}");
            }
            foreach (var row in diff.OnlyLeft.Take(maxRows))
                text.AppendLine($"      < {FormatRow(row)}");
            foreach (var row in diff.OnlyRight.Take(maxRows))
                text.AppendLine($"      > {FormatRow(row)}");
        }
        text.AppendLine(result.Identical ? "The tables match." : "* = the table differs.");
        return text.ToString();
    }

    public static string FormatRow(DbRow row) => string.Join(", ", row.Select(p => $"{p.Key}={DbValue.ToText(p.Value)}"));
}
