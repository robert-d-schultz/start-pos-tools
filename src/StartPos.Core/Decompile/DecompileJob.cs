using StartPos.Core.Esf;
using StartPos.Core.Model;
using StartPos.Core.Rpfm;
using StartPos.Core.Tables;

namespace StartPos.Core.Decompile;

public sealed class DecompileOptions
{
    /// <summary>A .pack holding campaigns/&lt;campaign&gt;/startpos.esf, or a loose startpos.esf.</summary>
    public required string Input { get; init; }

    /// <summary>The .pack to write.</summary>
    public required string Output { get; init; }

    /// <summary>Which campaign's startpos to decompile when the pack has several.</summary>
    public string? Campaign { get; init; }

    /// <summary>
    /// The name in the DB files' names (db/&lt;table&gt;_tables/!!!&lt;Name&gt;_new_factions, ...; see
    /// <see cref="PackLayout"/>); default the campaign key.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>Folder for extracted files; a temporary folder by default.</summary>
    public string? WorkFolder { get; init; }
}

public sealed record DecompileResult(string Campaign, StartPosTables Tables, DecompileReport Report, string Output);

/// <summary>Decompiles a startpos (from a pack or a loose file) into a new start_pos pack.</summary>
public static class DecompileJob
{
    public const string StartPosFileName = "startpos.esf";
    public const string VictoryObjectivesPath = "db/victory_objectives.txt";

    public static async Task<DecompileResult> RunAsync(RpfmSession rpfm, DecompileOptions options, Action<string> log, CancellationToken ct = default)
    {
        string work = options.WorkFolder ?? Path.Combine(Path.GetTempPath(), "startpos_tools", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string? sourcePack = null;
            string esfPath;
            var notes = new List<string>();
            if (Path.GetExtension(options.Input).Equals(".pack", StringComparison.OrdinalIgnoreCase))
            {
                sourcePack = await rpfm.OpenPackAsync(options.Input, ct);
                log("Looking for startpos files");
                var listing = await StartPosListing.LoadAsync(rpfm, sourcePack, work, ct);
                notes.AddRange(listing.Notes);
                foreach (string note in listing.Notes)
                    log(note);
                var file = listing.Find(options.Campaign);
                log($"Using {file.PackPath}");
                esfPath = file.LocalPath;
            }
            else
            {
                esfPath = options.Input;
            }

            log($"Reading {Path.GetFileName(esfPath)}");
            var esf = new StartPosEsf(EsfDocument.Load(esfPath));

            string pack = await rpfm.NewPackAsync(ct);
            try
            {
                log("Reading game tables");
                var lookups = await GameLookups.LoadAsync(rpfm, sourcePack ?? pack, log, ct);

                log($"Decompiling campaign {esf.Campaign}");
                var (tables, report) = StartPosDecompiler.Decompile(esf, lookups);
                report.Notes.InsertRange(0, notes);
                foreach (string missing in lookups.MissingDependencies)
                    report.Warn($"Dependency {missing} was not found next to the source pack, so its tables were not compared with the startpos.");
                log($"{tables.All.Count(t => t.Rows.Count > 0)} tables, {tables.RowCount} rows");

                if (sourcePack != null)
                    await CopyCampaignsRowAsync(rpfm, sourcePack, tables, esf.Campaign, ct);
                foreach (var file in PackLayout.Split(tables, options.Name ?? esf.Campaign, lookups))
                {
                    log($"Writing {file.Table.Folder}/{file.Fragment} ({file.Table.Rows.Count} rows)");
                    await rpfm.WriteTableAsync(pack, file.Table, file.Fragment, ct);
                }
                if (sourcePack != null)
                {
                    await CopyDependenciesAsync(rpfm, sourcePack, options.Input, pack, log, ct);
                }
                else
                    report.Warn("The input is a loose file, so the campaign's campaigns row was not copied; " +
                                "a new campaign needs one before RPFM can build its startpos.");
                await AddVictoryObjectivesAsync(rpfm, sourcePack, pack, work, log, ct);

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Output))!);
                await rpfm.SavePackAsAsync(pack, options.Output, ct);
                log($"Saved {options.Output}");
                return new DecompileResult(esf.Campaign, tables, report, options.Output);
            }
            finally
            {
                await TryCloseAsync(rpfm, pack);
                if (sourcePack != null)
                    await TryCloseAsync(rpfm, sourcePack);
            }
        }
        finally
        {
            if (options.WorkFolder == null)
                TryDelete(work);
        }
    }

    /// <summary>
    /// The campaign's row of the campaigns table, from the source pack if it has one, goes into
    /// the decompiled tables (a new campaign needs it for RPFM to build its startpos).
    /// </summary>
    private static async Task CopyCampaignsRowAsync(RpfmSession rpfm, string sourcePack, StartPosTables tables, string campaign,
        CancellationToken ct)
    {
        foreach (string path in (await rpfm.ListFilesAsync(sourcePack, ct)).Where(p => p.StartsWith("db/campaigns_tables/", StringComparison.Ordinal)))
        {
            var (_, rows) = await rpfm.DecodeTableAsync(sourcePack, path, ct: ct);
            tables["campaigns"].Rows.AddRange(rows.Rows.Where(r => r.Str("campaign_name") == campaign));
        }
    }

    /// <summary>
    /// The new pack depends on what the source pack depends on, and on the source pack itself
    /// when that is the mod with the campaign's content (its tables are what the start_pos
    /// rows point at). A source that is itself a start_pos pack is not added: its start_pos
    /// tables would load over the decompiled ones.
    /// </summary>
    private static async Task CopyDependenciesAsync(RpfmSession rpfm, string sourcePack, string sourcePath, string pack,
        Action<string> log, CancellationToken ct)
    {
        var dependencies = await rpfm.GetDependenciesAsync(sourcePack, ct);
        bool isStartPosPack = (await rpfm.ListFilesAsync(sourcePack, ct)).Any(p => p.StartsWith("db/start_pos_", StringComparison.Ordinal));
        string sourceName = Path.GetFileName(sourcePath);
        if (!isStartPosPack && !dependencies.Any(d => d.Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase)))
            dependencies.Add(new PackDependency(sourceName, true));
        if (dependencies.Count == 0)
            return;
        await rpfm.SetDependenciesAsync(pack, dependencies, ct);
        log($"Dependencies: {string.Join(", ", dependencies.Select(d => d.Enabled ? d.Name : $"{d.Name} (unticked)"))}");
    }

    /// <summary>
    /// RPFM's Build Startpos refuses to run without db/victory_objectives.txt. Warhammer III
    /// does not use it (CA's and modders' copies are empty), so the source pack's copy is kept
    /// and an empty one is added otherwise.
    /// </summary>
    private static async Task AddVictoryObjectivesAsync(RpfmSession rpfm, string? sourcePack, string pack, string work,
        Action<string> log, CancellationToken ct)
    {
        string local;
        if (sourcePack != null && (await rpfm.ListFilesAsync(sourcePack, ct)).Contains(VictoryObjectivesPath))
        {
            local = await rpfm.ExtractFileAsync(sourcePack, VictoryObjectivesPath, Path.Combine(work, "extra"), ct);
        }
        else
        {
            local = Path.Combine(work, "extra", "victory_objectives.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            File.WriteAllBytes(local, []);
        }
        await rpfm.AddFileAsync(pack, local, VictoryObjectivesPath, ct);
        log($"Added {VictoryObjectivesPath}");
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

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
