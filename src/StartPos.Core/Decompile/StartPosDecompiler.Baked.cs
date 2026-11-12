using StartPos.Core.Tables;
using static StartPos.Core.Tables.StartPosTableNames;

namespace StartPos.Core.Decompile;

/// <summary>
/// Tables outside start_pos_* that a build reads and bakes into the startpos: changing them
/// afterwards does nothing to a startpos already built.
///   faction_to_mercenary_set_junctions  each faction's mercenary pool is filled from its sets
///   agent_culture_details               the agent types a faction can recruit
///   agent_subtypes.names_group          which subtypes name their characters from their own group
/// The startpos keeps what these produced, not the rows, so they are compared with the game's
/// tables (as the source pack sees them): rows the build had that the tables lack are written,
/// with what the startpos doesn't keep borrowed or left empty, and the reverse is a warning.
/// </summary>
public sealed partial class StartPosDecompiler
{
    /// <summary>
    /// faction_to_mercenary_set_junctions. FACTION/FACTION_MERCENARY_POOL/MERCENARY_POOL/
    /// MERCENARY_POOL_UNIT_ARRAY has a unit per mercenary_pool_to_groups_junctions row of each of
    /// the faction's sets, whether or not the faction meets the row's requirements: #12 the unit
    /// group, #8 faction, #9 subculture and #10 technology requirement (#1 is the pool's
    /// recruitment_source, empty in older builds, so not compared). The set keys are not kept, so a set is recognised by its rows: a pool all of
    /// whose rows are in the faction's pool. Rows are written for the fewest such pools that give
    /// what the tables' own sets don't; a pool with no groups leaves no trace.
    /// </summary>
    private void ExtractMercenarySets()
    {
        if (_game == null)
            return;
        var table = Table(FactionMercenarySets);
        // Sets of a faction in the tables whose units the startpos's pool doesn't all have: set → factions.
        var notBuilt = new Dictionary<string, List<string>>(StringComparer.Ordinal);   // none of its units: the set was added since
        var grown = new Dictionary<string, List<string>>(StringComparer.Ordinal);      // some: units were added to the set since
        var unknown = new List<string>();                                               // factions with units no pool in the tables gives
        bool any = false;

        Each(_factionsByCqi.Values.OrderBy(f => f.Cqi), f => $"Mercenary pool of {f.Key}", f =>
        {
            var units = f.Faction.Record("FACTION_MERCENARY_POOL")?.Record("MERCENARY_POOL")?.Record("MERCENARY_POOL_UNIT_ARRAY");
            if (units == null)
                return;
            any = true;
            var built = units.Elements().Select(u => new MercenaryUnit(u.Str(12), u.Str(8), u.Str(9), u.Str(10))).ToHashSet();

            var sets = _game.FactionMercenarySets.GetValueOrDefault(f.Key) ?? [];
            var fromTables = new HashSet<MercenaryUnit>();
            foreach (string set in sets.Order(StringComparer.Ordinal))
            {
                var given = _game.MercenaryPoolUnits.GetValueOrDefault(set) ?? [];
                fromTables.UnionWith(given);
                if (given.Count > 0 && !given.IsSubsetOf(built))
                {
                    var map = given.Overlaps(built) ? grown : notBuilt;
                    (map.TryGetValue(set, out var list) ? list : map[set] = []).Add(f.Key);
                }
            }

            var unexplained = built.Where(u => !fromTables.Contains(u)).ToHashSet();
            if (unexplained.Count == 0)
                return;
            var candidates = _game.MercenaryPoolUnits.Where(p => p.Value.Count > 0 && p.Value.IsSubsetOf(built) && p.Value.Overlaps(unexplained))
                .OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
            // The fewest pools that give those units: repeatedly the one giving most of what is left.
            while (candidates.Count > 0 && unexplained.Count > 0)
            {
                var best = candidates.MaxBy(c => c.Value.Count(unexplained.Contains));
                if (!best.Value.Overlaps(unexplained))
                    break;
                unexplained.ExceptWith(best.Value);
                table.Add(new DbRow { ["faction"] = f.Key, ["mercenary_set"] = best.Key });
            }
            if (unexplained.Count > 0)
                unknown.Add($"{f.Key} ({unexplained.Count}, e.g. {unexplained.First().Group})");
        });

        if (table.Rows.Count > 0)
            _report.Warn($"{FactionMercenarySets}: the startpos's mercenary pools need {table.Rows.Count} set(s) the tables don't give " +
                         $"({string.Join(", ", table.Rows.Take(5).Select(r => $"{r.Str("faction")}: {r.Str("mercenary_set")}"))}" +
                         $"{(table.Rows.Count > 5 ? ", ..." : "")}); written to the pack.");
        foreach (var (set, factions) in notBuilt.OrderBy(p => p.Key, StringComparer.Ordinal))
            _report.Warn($"{FactionMercenarySets}: {set} is a set of {Describe(factions)} in the tables but not in the startpos " +
                         "(added after it was built?); a rebuild adds it.");
        foreach (var (set, factions) in grown.OrderBy(p => p.Key, StringComparer.Ordinal))
            _report.Warn($"{FactionMercenarySets}: {set} (a set of {Describe(factions)}) has units in mercenary_pool_to_groups_junctions " +
                         "that the startpos's pools don't (added to the set after it was built?); a rebuild adds them.");
        if (unknown.Count > 0)
            _report.Warn($"{FactionMercenarySets}: mercenary pool units that no set in the tables gives (a pool changed or removed " +
                         $"since the build?): {Describe(unknown)}.");
        if (any)
            _report.Notes.Add($"{FactionMercenarySets} is baked into the startpos as each faction's mercenary pool; sets are recognised " +
                              "by their unit groups, and only sets the tables lack are written. A set with no unit groups can't be seen.");
    }

    /// <summary>
    /// agent_culture_details: FACTION/CHARACTER_RECRUITMENT_POOL_MANAGER/CHARACTER_RECRUITMENT_POOL_BLOCK
    /// has an entry (#0 = agent type) per agent type of the faction's culture, other than colonel
    /// and minister, and always a general. Only the agent type is kept: a row the tables lack is written with the other
    /// columns (associated unit, icons, ...) borrowed from another culture's row for that agent
    /// type, and a generated key.
    /// </summary>
    private void ExtractAgentCultures()
    {
        if (_game == null)
            return;
        var built = new Dictionary<string, (HashSet<string> Agents, List<string> Factions)>(StringComparer.Ordinal);
        Each(_factionsByCqi.Values.OrderBy(f => f.Cqi), f => $"Agent types of {f.Key}", f =>
        {
            var pools = f.Faction.Record("CHARACTER_RECRUITMENT_POOL_MANAGER")?.Record("CHARACTER_RECRUITMENT_POOL_BLOCK");
            if (pools == null || _game.FactionCultures.GetValueOrDefault(f.Key) is not { Length: > 0 } culture)
                return;
            var entry = built.TryGetValue(culture, out var e) ? e : built[culture] = (new(StringComparer.Ordinal), []);
            entry.Agents.UnionWith(pools.Elements().Select(p => p.Str(0)));
            entry.Factions.Add(f.Key);
        });

        var table = Table(AgentCultureDetails);
        foreach (var (culture, (agents, factions)) in built.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var tables = _game.CultureAgents.GetValueOrDefault(culture) ?? [];
            // Every faction gets a general pool, with or without a row (the mixer_tre_tregara
            // factions, whose culture has no agent_culture_details rows, have one), so it says nothing.
            foreach (string agent in agents.Except(tables).Where(a => a != "general").Order(StringComparer.Ordinal))
            {
                var borrowed = _game.AgentCultureRows.Where(r => r.Str("agent") == agent)
                    .OrderBy(r => r.Str("culture"), StringComparer.Ordinal).FirstOrDefault();
                var row = borrowed != null ? new DbRow(borrowed) : new DbRow { ["agent"] = agent, ["recruitable_in_ui"] = true };
                row["culture"] = culture;
                row["key"] = Id($"agent_culture|{culture}|{agent}");
                table.Add(row);
            }
            var added = tables.Except(agents).Order(StringComparer.Ordinal).ToList();
            if (added.Count > 0)
                _report.Warn($"{AgentCultureDetails}: the tables give {culture} agent type(s) {string.Join(", ", added)} that the startpos " +
                             $"doesn't ({Describe(factions)} can't recruit them until it is rebuilt).");
        }
        if (table.Rows.Count > 0)
            _report.Warn($"{AgentCultureDetails}: the startpos gives {table.Rows.Count} agent type(s) to cultures the tables don't " +
                         $"({string.Join(", ", table.Rows.Take(5).Select(r => $"{r.Str("culture")}: {r.Str("agent")}"))}" +
                         $"{(table.Rows.Count > 5 ? ", ..." : "")}); written to the pack with the other columns borrowed from another culture.");
        if (built.Count > 0)
            _report.Notes.Add($"{AgentCultureDetails} is baked into the startpos as each faction's agent types; rows the tables lack are " +
                              "written with their associated unit, icons and the rest borrowed from another culture's row.");
    }

    /// <summary>
    /// agent_subtypes.names_group: CHARACTER_NAME_MANAGER/AGENTS has a name allocator (keyed by the
    /// subtype only) per subtype that had a names group of its own when the startpos was built; a
    /// subtype not in it is named from its faction's group. No group key is kept anywhere: the
    /// game reads the group from agent_subtypes. So a build only needs the subtype to have *a*
    /// group, and which one is written doesn't matter: a subtype in the list whose group the tables
    /// lack gets the tables' row (or a row with only its key) with a placeholder group.
    /// </summary>
    /// <summary>The names_group written where any will do: a vanilla group, so it always exists. Blank would give no allocator.</summary>
    private const string PlaceholderNamesGroup = "names_emp_empire";

    private void ExtractSubtypeNamesGroups()
    {
        if (_game == null)
            return;
        var built = _esf.NamedAgentSubtypes.Select(a => a.Str(0)).ToHashSet(StringComparer.Ordinal);
        if (built.Count == 0)
            return;

        var missing = built.Where(s => !_game.SubtypeNamesGroups.ContainsKey(s)).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            var table = Table(AgentSubtypes);
            foreach (string subtype in missing)
            {
                var row = _game.AgentSubtypeRows.TryGetValue(subtype, out var existing) ? new DbRow(existing) : new DbRow { ["key"] = subtype };
                row["names_group"] = PlaceholderNamesGroup;
                table.Add(row);
            }
            _report.Warn($"{AgentSubtypes}: {Describe(missing, "subtype")} had their own names_group when the startpos was built and the " +
                         $"tables give them none; written to the pack with the placeholder group {PlaceholderNamesGroup} (the startpos doesn't " +
                         "keep which group, and a build only needs one).");
        }
        foreach (var group in _game.SubtypeNamesGroups.Where(p => !built.Contains(p.Key))
                     .GroupBy(p => p.Value, p => p.Key).OrderBy(g => g.Key, StringComparer.Ordinal))
            _report.Warn($"{AgentSubtypes}: names_group {group.Key} is set on {Describe(group.Order(StringComparer.Ordinal).ToList(), "subtype")} " +
                         "in the tables but not in the startpos (set after it was built?); until a rebuild their characters are named " +
                         "from the faction's group and may show blank names.");
        _report.Notes.Add($"{AgentSubtypes}.names_group is baked into the startpos as the list of subtypes that have a group of their " +
                          "own, not the group; a subtype on it that the tables give no group is written with a placeholder group.");
    }

    private static string Describe(List<string> items, string what = "faction") =>
        items.Count <= 3 ? string.Join(", ", items) : $"{string.Join(", ", items.Take(3))} and {items.Count - 3} more {what}(s)";
}
