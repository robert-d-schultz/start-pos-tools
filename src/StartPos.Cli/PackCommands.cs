using StartPos.Core;
using StartPos.Core.Compare;
using StartPos.Core.Decompile;
using StartPos.Core.Rpfm;
using StartPos.Core.Tables;

namespace StartPos.Cli;

/// <summary>Commands that read or write packs through the RPFM server.</summary>
internal static class PackCommands
{
    public static async Task<int> DecompileCommand(string[] args)
    {
        var opts = Options.Parse(args, ["out", "campaign", "name", "rpfm"]);
        if (opts.Positional.Count != 1)
            return Program.Fail("decompile takes one input: a .pack with campaigns/<campaign>/startpos.esf, or a startpos.esf.");
        string input = Path.GetFullPath(opts.Positional[0]);
        if (!File.Exists(input))
            return Program.Fail($"No such file: {input}");
        string output = Path.GetFullPath(opts.Get("out") ?? Path.Combine(Path.GetDirectoryName(input)!,
            Path.GetFileNameWithoutExtension(input) + "_decompiled.pack"));

        using var rpfm = await ConnectAsync(opts);
        var result = await DecompileJob.RunAsync(rpfm, new DecompileOptions
        {
            Input = input,
            Output = output,
            Campaign = opts.Get("campaign"),
            Name = opts.Get("name"),
        }, Console.WriteLine);

        foreach (var table in result.Tables.All.Where(t => t.Rows.Count > 0))
            Console.WriteLine($"  {table.Name,-48} {table.Rows.Count,6} rows");
        PrintReport(result.Report);
        return 0;
    }

    /// <summary>
    /// Compares the start_pos tables of two inputs. A startpos.esf (or a pack's startpos.esf,
    /// with --decompile) is decompiled first, so a decompile can be checked against the tables
    /// it was built from.
    /// </summary>
    public static async Task<int> DiffCommand(string[] args)
    {
        var opts = Options.Parse(args, ["campaign", "left-campaign", "right-campaign", "out", "rpfm", "examples", "rows", "json"],
            ["decompile", "decompile-right", "ignore-campaign", "everything", "no-game"]);
        if (opts.Positional.Count != 2)
            return Program.Fail("diff takes two inputs (.pack or startpos.esf).");

        using var rpfm = await ConnectAsync(opts);
        var result = await CompareJob.RunAsync(rpfm, new CompareOptions
        {
            Left = opts.Positional[0],
            Right = opts.Positional[1],
            DecompileLeft = opts.Has("decompile"),
            DecompileRight = opts.Has("decompile-right"),
            LeftCampaign = opts.Get("left-campaign") ?? opts.Get("campaign"),
            RightCampaign = opts.Get("right-campaign") ?? opts.Get("campaign"),
            IgnoreCampaign = opts.Has("ignore-campaign"),
            Output = opts.Get("out") is { } output ? Path.GetFullPath(output) : null,
            OnlyBuilt = !opts.Has("everything"),
            UseGameTables = !opts.Has("no-game"),
            MaxExamples = opts.GetInt("examples") ?? 5,
        }, Console.WriteLine);
        Console.Write(CompareJob.Format(result, opts.GetInt("rows") ?? 5));
        if (result.LeftReport != null)
            PrintReport(result.LeftReport);
        if (result.RightReport != null)
            PrintReport(result.RightReport);
        if (opts.Get("json") is { } json)
            WriteJson(result.Left, json);
        return result.Identical ? 0 : 1;
    }

    /// <summary>
    /// Prints a DB table as the game sees it (with --pack: that pack's own tables, its
    /// dependencies, then vanilla; --vanilla: the game's own packs only), as tab-separated rows.
    /// </summary>
    public static async Task<int> TableCommand(string[] args)
    {
        var opts = Options.Parse(args, ["pack", "rpfm", "out", "where"], ["vanilla"]);
        if (opts.Positional.Count != 1)
            return Program.Fail("table takes a table name (without _tables), e.g. agent_culture_details.");
        string name = opts.Positional[0].EndsWith("_tables", StringComparison.Ordinal) ? opts.Positional[0][..^7] : opts.Positional[0];

        using var rpfm = await ConnectAsync(opts);
        string pack = opts.Get("pack") is { } path ? await rpfm.OpenPackAsync(path) : await rpfm.NewPackAsync();
        var game = await GameTables.LoadAsync(rpfm, pack);
        DbTable table;
        try
        {
            if (game.Dependencies.Count > 0)
                Console.Error.WriteLine($"Tables of: {string.Join(", ", game.Dependencies)} (ticked dependencies), and the game's");
            foreach (string missing in game.MissingDependencies)
                Console.Error.WriteLine($"Dependency {missing} is not next to the pack; its tables are left out.");
            table = opts.Has("vanilla") ? await game.VanillaTableAsync(name) : await game.TableAsync(name);
        }
        finally
        {
            await game.CloseAsync();
        }

        // --where column=value keeps the rows whose column has that value.
        IEnumerable<DbRow> rows = table.Rows;
        if (opts.Get("where") is { } where)
        {
            int eq = where.IndexOf('=');
            if (eq <= 0)
                return Program.Fail("--where takes column=value.");
            string column = where[..eq], value = where[(eq + 1)..];
            rows = rows.Where(r => r.Str(column) == value);
        }

        var columns = table.Rows.SelectMany(r => r.Keys).Distinct().ToList();
        using var file = opts.Get("out") is { } outPath ? new StreamWriter(outPath) : null;
        var output = file ?? Console.Out;
        output.WriteLine(string.Join('	', columns));
        foreach (var row in rows)
            output.WriteLine(string.Join('	', columns.Select(row.Str)));
        return 0;
    }

    public static async Task<RpfmSession> ConnectAsync(Options opts)
    {
        string? folder = ToolSettings.ResolveRpfmFolder(opts.Get("rpfm"));
        return await RpfmSession.ConnectAsync(folder, Console.WriteLine);
    }

    private static void PrintReport(DecompileReport report)
    {
        foreach (string note in report.Notes)
            Console.WriteLine($"note: {note}");
        foreach (var group in report.Warnings.GroupBy(w => w.Split(' ')[0]).Take(50))
            foreach (string warning in group.Take(10))
                Console.WriteLine($"warning: {warning}");
        if (report.Warnings.Count > 0)
            Console.WriteLine($"{report.Warnings.Count} warning(s)");
    }

    /// <summary>Writes tables as {"table": [{column: value}, ...]} for scripting.</summary>
    public static void WriteJson(StartPosTables tables, string path)
    {
        var root = new System.Text.Json.Nodes.JsonObject();
        foreach (var table in tables.All)
            root[table.Name] = new System.Text.Json.Nodes.JsonArray(table.Rows.Select(r =>
                (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject(
                    r.Select(p => KeyValuePair.Create(p.Key, (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(DbValue.ToText(p.Value)))))).ToArray());
        File.WriteAllText(path, root.ToJsonString());
    }

}
