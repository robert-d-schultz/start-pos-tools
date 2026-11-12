using StartPos.Core.Rpfm;

namespace StartPos.Core.Tables;

/// <summary>
/// DB tables as the game sees them with a pack loaded: the pack's own tables, then those of the
/// packs it depends on, then vanilla. Only ticked entries of a dependency list count, followed
/// through the dependencies' own ticked entries; the packs are looked for next to the pack.
/// (RPFM's own dependency view follows unticked entries too, so it isn't used.) A path shipped by
/// several packs comes from the first: the pack itself, then its dependencies by file name, as
/// the game orders mods. Fragments then apply in file-name order with the first row for a key
/// winning, as the game does.
/// </summary>
public sealed class GameTables
{
    private readonly RpfmSession _rpfm;
    private readonly string _packKey;
    private readonly Dictionary<string, Dictionary<string, List<Source>>> _sources = new(StringComparer.Ordinal);   // table folder → path → sources, first wins
    private readonly Dictionary<string, DbTable> _cache = new(StringComparer.Ordinal);
    private readonly List<string> _opened = [];   // dependency packs this opened, closed by CloseAsync

    /// <summary>Where a DB file is read from: an open pack, or the game's own files.</summary>
    private sealed record Source(string? PackKey)
    {
        public static readonly Source Game = new((string?)null);
    }

    private GameTables(RpfmSession rpfm, string packKey)
    {
        _rpfm = rpfm;
        _packKey = packKey;
    }

    /// <summary>The dependency packs read, by file name, in the order they count.</summary>
    public List<string> Dependencies { get; } = [];

    /// <summary>Ticked dependencies that could not be found next to the pack.</summary>
    public List<string> MissingDependencies { get; } = [];

    /// <summary>
    /// <paramref name="packKey"/> is the pack whose view of the game is wanted (its own tables
    /// and its ticked dependencies count). A new, unsaved pack gives the game's own tables.
    /// Call <see cref="CloseAsync"/> when done, to close the dependency packs.
    /// </summary>
    public static async Task<GameTables> LoadAsync(RpfmSession rpfm, string packKey, CancellationToken ct = default)
    {
        await rpfm.LoadDependenciesAsync(ct);
        var tables = new GameTables(rpfm, packKey);
        tables.Add(await rpfm.ListFilesAsync(packKey, ct), new Source(packKey));

        string? path = await rpfm.PackPathAsync(packKey, ct);
        if (path != null)
        {
            string folder = Path.GetDirectoryName(path)!;
            var open = await rpfm.OpenPacksAsync(ct);
            var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [Path.GetFileName(path)] = packKey };
            var pending = new Queue<string>([packKey]);
            while (pending.Count > 0)
                foreach (var dependency in await rpfm.GetDependenciesAsync(pending.Dequeue(), ct))
                {
                    if (!dependency.Enabled || keys.ContainsKey(dependency.Name))
                        continue;
                    string file = Path.GetFullPath(Path.Combine(folder, dependency.Name));
                    if (!File.Exists(file))
                    {
                        if (!tables.MissingDependencies.Contains(dependency.Name, StringComparer.OrdinalIgnoreCase))
                            tables.MissingDependencies.Add(dependency.Name);
                        continue;
                    }
                    if (!open.TryGetValue(file, out var key))
                    {
                        key = await rpfm.OpenPackAsync(file, ct);
                        tables._opened.Add(key);
                    }
                    keys[dependency.Name] = key;
                    pending.Enqueue(key);
                }
            foreach (var (name, key) in keys.Where(k => k.Value != packKey).OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                tables.Dependencies.Add(name);
                tables.Add(await rpfm.ListFilesAsync(key, ct), new Source(key));
            }
        }
        tables.Add(await rpfm.ListDependencyTablesAsync(RpfmSession.GameFiles, ct), Source.Game);
        return tables;
    }

    /// <summary>Closes the dependency packs <see cref="LoadAsync"/> opened. Tables already read stay readable.</summary>
    public async Task CloseAsync()
    {
        foreach (string key in _opened)
        {
            try
            {
                await _rpfm.ClosePackAsync(key);
            }
            catch (RpfmException)
            {
                // Housekeeping; the server drops the session's packs on disconnect anyway.
            }
        }
        _opened.Clear();
    }

    private void Add(IEnumerable<string> paths, Source source)
    {
        foreach (string path in paths)
        {
            var parts = path.Split('/');
            if (parts.Length != 3 || parts[0] != "db" || !parts[1].EndsWith("_tables", StringComparison.Ordinal))
                continue;
            var bySource = _sources.TryGetValue(parts[1], out var d) ? d : _sources[parts[1]] = new(StringComparer.Ordinal);
            (bySource.TryGetValue(path, out var list) ? list : bySource[path] = []).Add(source);
        }
    }

    /// <summary>A table's rows (empty if no loaded pack has the table).</summary>
    public Task<DbTable> TableAsync(string name, CancellationToken ct = default) => LoadTableAsync(name, vanillaOnly: false, ct);

    /// <summary>A table's rows as the game's own packs ship it, without any mod.</summary>
    public Task<DbTable> VanillaTableAsync(string name, CancellationToken ct = default) => LoadTableAsync(name, vanillaOnly: true, ct);

    private async Task<DbTable> LoadTableAsync(string name, bool vanillaOnly, CancellationToken ct)
    {
        string cacheKey = (vanillaOnly ? "vanilla:" : "") + name;
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;
        var table = new DbTable(name);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, sources) in _sources.GetValueOrDefault(name + "_tables", []).OrderBy(p => p.Key.Split('/')[2], StringComparer.Ordinal))
        {
            var source = vanillaOnly ? sources.FirstOrDefault(s => s == Source.Game) : sources[0];
            if (source == null)
                continue;
            var (definition, rows) = source.PackKey != null
                ? await _rpfm.DecodeTableAsync(source.PackKey, path, ct: ct)
                : await _rpfm.DecodeTableAsync(_packKey, path, RpfmSession.GameFiles, ct);
            var keys = definition.KeyColumns.ToList();
            foreach (var row in rows.Rows)
                if (keys.Count == 0 || seen.Add(string.Join("\u001f", keys.Select(row.Str))))
                    table.Add(row);
        }
        _cache[cacheKey] = table;
        return table;
    }
}

/// <summary>What the decompiler looks up in the game's tables to tell defaults from choices.</summary>
public sealed class GameLookups
{
    /// <summary>agent_subtypes.key → associated_unit_override: the unit a general of that subtype gets.</summary>
    public Dictionary<string, string> DefaultGeneralUnits { get; } = new(StringComparer.Ordinal);

    /// <summary>Faction keys the game itself ships (vanilla factions table).</summary>
    public HashSet<string> VanillaFactions { get; } = new(StringComparer.Ordinal);

    /// <summary>Region keys the game itself ships (vanilla regions table).</summary>
    public HashSet<string> VanillaRegions { get; } = new(StringComparer.Ordinal);

    /// <summary>Agent subtype → how many art sets campaign_character_art_sets offers it.</summary>
    public Dictionary<string, int> ArtSetCounts { get; } = new(StringComparer.Ordinal);

    /// <summary>Trait → its levels (level, threshold points, character_trait_levels key), by level.</summary>
    public Dictionary<string, List<(int Level, int Threshold, string Key)>> TraitLevels { get; } = new(StringComparer.Ordinal);

    /// <summary>The trait level a trait with <paramref name="points"/> is at: the highest whose threshold the points reach.</summary>
    public string? TraitLevel(string trait, long points)
    {
        if (!TraitLevels.TryGetValue(trait, out var levels) || levels.Count == 0)
            return null;
        return levels.LastOrDefault(l => l.Threshold <= points).Key ?? levels[0].Key;
    }

    /// <summary>Skill → ancillaries it grants (character_skill_level_to_ancillaries_junctions), e.g. a lord's starting mount.</summary>
    public Dictionary<string, HashSet<string>> SkillAncillaries { get; } = new(StringComparer.Ordinal);

    /// <summary>Technologies that are in a tech tree (technology_nodes); a build drops start_pos_technologies rows for others.</summary>
    public HashSet<string> Technologies { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The game's tables as <paramref name="packKey"/> sees them (see <see cref="GameTables"/>),
    /// with the packs that counted logged; the dependency packs are closed again afterwards.
    /// </summary>
    public static async Task<GameLookups> LoadAsync(RpfmSession rpfm, string packKey, Action<string> log, CancellationToken ct = default)
    {
        var game = await GameTables.LoadAsync(rpfm, packKey, ct);
        try
        {
            if (game.Dependencies.Count > 0)
                log($"Tables of: {string.Join(", ", game.Dependencies)} (ticked dependencies), and the game's");
            foreach (string missing in game.MissingDependencies)
                log($"Dependency {missing} is not next to the pack; its tables are left out.");
            var lookups = await LoadAsync(game, ct);
            lookups.MissingDependencies.AddRange(game.MissingDependencies);
            return lookups;
        }
        finally
        {
            await game.CloseAsync();
        }
    }

    /// <summary>Ticked dependencies whose tables could not be read (not found next to the pack).</summary>
    public List<string> MissingDependencies { get; } = [];

    public static async Task<GameLookups> LoadAsync(GameTables game, CancellationToken ct = default)
    {
        var lookups = new GameLookups();
        lookups.VanillaFactions.UnionWith((await game.VanillaTableAsync("factions", ct)).Rows.Select(r => r.Str("key")));
        lookups.VanillaRegions.UnionWith((await game.VanillaTableAsync("regions", ct)).Rows.Select(r => r.Str("key")));
        foreach (var row in (await game.TableAsync("agent_subtypes", ct)).Rows)
            lookups.DefaultGeneralUnits[row.Str("key")] = row.Str("associated_unit_override");
        foreach (var group in (await game.TableAsync("campaign_character_art_sets", ct)).Rows.GroupBy(r => r.Str("agent_subtype")))
            lookups.ArtSetCounts[group.Key] = group.Select(r => r.Str("art_set_id")).Distinct().Count();
        foreach (var group in (await game.TableAsync("character_trait_levels", ct)).Rows.GroupBy(r => r.Str("trait")))
            lookups.TraitLevels[group.Key] = group
                .Select(r => (Level: int.Parse(r.Str("level")), Threshold: int.Parse(r.Str("threshold_points")), Key: r.Str("key")))
                .OrderBy(l => l.Level).ToList();
        foreach (var row in (await game.TableAsync("character_skill_level_to_ancillaries_junctions", ct)).Rows)
        {
            string skill = row.Str("skill");
            (lookups.SkillAncillaries.TryGetValue(skill, out var set) ? set : lookups.SkillAncillaries[skill] = new(StringComparer.Ordinal))
                .Add(row.Str("granted_ancillary"));
        }
        lookups.Technologies.UnionWith((await game.TableAsync("technology_nodes", ct)).Rows.Select(r => r.Str("technology_key")));

        // What a build bakes into the startpos from outside the start_pos tables.
        var cultureOf = (await game.TableAsync("cultures_subcultures", ct)).Rows
            .GroupBy(r => r.Str("subculture")).ToDictionary(g => g.Key, g => g.First().Str("culture"), StringComparer.Ordinal);
        foreach (var row in (await game.TableAsync("factions", ct)).Rows)
            lookups.FactionCultures.TryAdd(row.Str("key"), cultureOf.GetValueOrDefault(row.Str("subculture"), ""));
        foreach (var row in (await game.TableAsync("agent_culture_details", ct)).Rows)
        {
            lookups.AgentCultureRows.Add(row);
            if (!NotInRecruitmentPool.Contains(row.Str("agent")))
                Add(lookups.CultureAgents, row.Str("culture"), row.Str("agent"));
        }
        foreach (var row in (await game.TableAsync("agent_subtypes", ct)).Rows)
        {
            lookups.AgentSubtypeRows.TryAdd(row.Str("key"), row);
            if (row.Str("names_group") is { Length: > 0 } namesGroup)
                lookups.SubtypeNamesGroups[row.Str("key")] = namesGroup;
        }
        foreach (var row in (await game.TableAsync("faction_to_mercenary_set_junctions", ct)).Rows)
            Add(lookups.FactionMercenarySets, row.Str("faction"), row.Str("mercenary_set"));
        foreach (var row in (await game.TableAsync("mercenary_pool_to_groups_junctions", ct)).Rows)
        {
            string pool = row.Str("pool");
            (lookups.MercenaryPoolUnits.TryGetValue(pool, out var set) ? set : lookups.MercenaryPoolUnits[pool] = [])
                .Add(new MercenaryUnit(row.Str("group"), row.Str("faction_requirement"),
                    row.Str("subculture_requirement"), row.Str("tech_requirement")));
        }
        return lookups;
    }

    private static void Add(Dictionary<string, HashSet<string>> map, string key, string value) =>
        (map.TryGetValue(key, out var set) ? set : map[key] = new(StringComparer.Ordinal)).Add(value);

    /// <summary>Agent types of agent_culture_details that a faction's CHARACTER_RECRUITMENT_POOL_MANAGER never lists.</summary>
    public static readonly IReadOnlySet<string> NotInRecruitmentPool = new HashSet<string>(["colonel", "minister"], StringComparer.Ordinal);

    /// <summary>Faction key → its culture (factions, cultures_subcultures).</summary>
    public Dictionary<string, string> FactionCultures { get; } = new(StringComparer.Ordinal);

    /// <summary>Culture → the agent types agent_culture_details gives it (without <see cref="NotInRecruitmentPool"/>).</summary>
    public Dictionary<string, HashSet<string>> CultureAgents { get; } = new(StringComparer.Ordinal);

    /// <summary>The agent_culture_details rows (to borrow the columns a startpos doesn't keep from).</summary>
    public List<DbRow> AgentCultureRows { get; } = [];

    /// <summary>agent_subtypes rows by key.</summary>
    public Dictionary<string, DbRow> AgentSubtypeRows { get; } = new(StringComparer.Ordinal);

    /// <summary>Agent subtype → its own names_group (agent_subtypes), for subtypes that have one.</summary>
    public Dictionary<string, string> SubtypeNamesGroups { get; } = new(StringComparer.Ordinal);

    /// <summary>Faction → mercenary sets (faction_to_mercenary_set_junctions; a set is a mercenary_pools key).</summary>
    public Dictionary<string, HashSet<string>> FactionMercenarySets { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Mercenary pool → what it puts in a faction's mercenary pool: a unit per row of
    /// mercenary_pool_to_groups_junctions, requirements and all.
    /// </summary>
    public Dictionary<string, HashSet<MercenaryUnit>> MercenaryPoolUnits { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// A unit of a faction's mercenary pool: the mercenary_unit_groups key, and the faction,
/// subculture and technology it requires (empty = none).
/// </summary>
public sealed record MercenaryUnit(string Group, string Faction, string Subculture, string Tech);
