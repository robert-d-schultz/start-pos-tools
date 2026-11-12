using StartPos.Core.Rpfm;

namespace StartPos.Core.Tables;

/// <summary>One DB file of a pack: db/&lt;table&gt;_tables/&lt;Name&gt;.</summary>
public sealed record TableFragment(string Name, TableDefinition Definition, DbTable Rows);

/// <summary>
/// The DB tables of a pack, read through RPFM. Fragments of a table are applied in file-name
/// order and the first row for a key wins, as the game does, so "!!!!corrections" overrides
/// "!!!vanilla"; <see cref="Effective"/> holds the rows that win.
/// </summary>
public sealed class PackTables
{
    public string PackKey { get; }
    public IReadOnlyList<string> Files { get; }
    public Dictionary<string, List<TableFragment>> Fragments { get; } = new(StringComparer.Ordinal);
    public StartPosTables Effective { get; } = new();

    /// <summary>Rows that lost to an earlier row with the same key: (table, loser, winner).</summary>
    public List<(string Table, DbRow Loser, DbRow Winner)> Overridden { get; } = [];

    /// <summary>DB files RPFM could not decode: (path, error).</summary>
    public List<(string Path, string Error)> DecodeErrors { get; } = [];

    private PackTables(string packKey, IReadOnlyList<string> files)
    {
        PackKey = packKey;
        Files = files;
    }

    /// <summary>Opens <paramref name="packPath"/> in RPFM and reads its DB tables (all, or those <paramref name="include"/> accepts).</summary>
    public static async Task<PackTables> LoadAsync(RpfmSession rpfm, string packPath, Func<string, bool>? include = null,
        CancellationToken ct = default)
    {
        string key = await rpfm.OpenPackAsync(packPath, ct);
        var files = await rpfm.ListFilesAsync(key, ct);
        var result = new PackTables(key, files);

        var byTable = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string path in files)
        {
            var parts = path.Split('/');
            if (parts.Length != 3 || parts[0] != "db" || !parts[1].EndsWith("_tables", StringComparison.Ordinal))
                continue;
            string table = parts[1][..^"_tables".Length];
            if (include?.Invoke(table) == false)
                continue;
            (byTable.TryGetValue(table, out var list) ? list : byTable[table] = []).Add(path);
        }

        foreach (var (table, paths) in byTable)
        {
            var fragments = result.Fragments[table] = [];
            foreach (string path in paths.OrderBy(p => p.Split('/')[2], StringComparer.Ordinal))
            {
                try
                {
                    var (definition, rows) = await rpfm.DecodeTableAsync(key, path, ct: ct);
                    fragments.Add(new TableFragment(path.Split('/')[2], definition, rows));
                }
                catch (RpfmException ex)
                {
                    result.DecodeErrors.Add((path, ex.Message));
                }
            }
            result.Merge(table, fragments);
        }
        return result;
    }

    /// <summary>Whether a pack's files (as listed by RPFM) include start_pos tables.</summary>
    public static bool HasStartPosTables(IEnumerable<string> files) =>
        files.Any(f => f.StartsWith("db/start_pos_", StringComparison.Ordinal));

    /// <summary>
    /// The campaigns a pack's start_pos tables are for: the campaign column of its
    /// start_pos_calendars, or of its start_pos_factions when it has no calendars.
    /// </summary>
    public static async Task<List<string>> CampaignsAsync(RpfmSession rpfm, string packKey, IReadOnlyList<string> files,
        CancellationToken ct = default)
    {
        foreach (string table in (string[])[StartPosTableNames.Calendars, StartPosTableNames.Factions])
        {
            var campaigns = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string path in files.Where(f => f.StartsWith($"db/{table}_tables/", StringComparison.Ordinal)))
            {
                var (_, rows) = await rpfm.DecodeTableAsync(packKey, path, ct: ct);
                campaigns.UnionWith(rows.Rows.Select(r => r.Str("campaign")).Where(c => c.Length > 0));
            }
            if (campaigns.Count > 0)
                return campaigns.ToList();
        }
        return [];
    }

    private void Merge(string table, List<TableFragment> fragments)
    {
        var effective = Effective[table];
        var keys = fragments.FirstOrDefault()?.Definition.KeyColumns.ToList() ?? [];
        var seen = new Dictionary<string, DbRow>(StringComparer.Ordinal);
        foreach (var fragment in fragments)
        {
            foreach (var row in fragment.Rows.Rows)
            {
                if (keys.Count > 0)
                {
                    string k = string.Join("\u001f", keys.Select(row.Str));
                    if (seen.TryGetValue(k, out var winner))
                    {
                        Overridden.Add((table, row, winner));
                        continue;
                    }
                    seen[k] = row;
                }
                effective.Add(row);
            }
        }
    }
}
