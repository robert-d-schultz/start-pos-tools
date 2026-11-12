using StartPos.Core.Tables;
using static StartPos.Core.Tables.StartPosTableNames;

namespace StartPos.Core.Decompile;

/// <summary>
/// How decompiled rows are split into DB files (db/&lt;table&gt;_tables/&lt;file&gt;):
///   !!!&lt;name&gt;_campaign          calendars, the campaigns row, agent_culture_details and
///                                agent_subtypes rows the startpos needs and the tables lack
///   !!!&lt;name&gt;_vanilla_factions  factions the game ships, and what hangs off them: characters,
///                                units, traits, ancillaries, general options, hordes, garrisons,
///                                technologies, victory conditions, diplomacy, past events,
///                                mercenary sets the startpos has and the tables lack
///   !!!&lt;name&gt;_new_factions      the same for factions the game does not ship (diplomacy and past
///                                events go here when either side is new)
///   !!!&lt;name&gt;_vanilla_regions   regions the game ships: regions, settlements, slot templates,
///                                foreign slots, entity associations
///   !!!&lt;name&gt;_new_regions       the same for new regions (foreign slots and entity associations
///                                go here when the region or the faction is new)
/// Without the game's tables nothing is known to be vanilla, and the files are just
/// "_factions" and "_regions". The "!!!" sorts the files ahead of vanilla's "data__", so these
/// rows win where keys collide.
/// </summary>
public static class PackLayout
{
    public sealed record TableFile(string Fragment, DbTable Table);

    public static List<TableFile> Split(StartPosTables tables, string name, GameLookups? game)
    {
        string Fragment(string part) => $"!!!{name}_{part}";
        string FactionPart(bool isNew) => game == null ? "factions" : isNew ? "new_factions" : "vanilla_factions";
        string RegionPart(bool isNew) => game == null ? "regions" : isNew ? "new_regions" : "vanilla_regions";

        // What decides a row's file: faction keys and region keys, reached through the IDs that link the tables.
        var factionKeyById = Map(tables.Find(Factions), "ID", "faction");
        var factionIdByCharacter = Map(tables.Find(Characters), "ID", "faction");
        var regionKeyById = Map(tables.Find(Regions), "id", "region");
        bool NewFaction(string key) => game != null && !game.VanillaFactions.Contains(key);
        bool NewFactionId(string id) => NewFaction(factionKeyById.GetValueOrDefault(id, ""));
        bool NewCharacter(string id) => NewFactionId(factionIdByCharacter.GetValueOrDefault(id, ""));
        bool NewRegion(string key) => game != null && !game.VanillaRegions.Contains(key);

        Func<DbRow, string>? Rule(string table) => table switch
        {
            Calendars or "campaigns" or AgentCultureDetails or AgentSubtypes => _ => Fragment("campaign"),
            Factions => r => Fragment(FactionPart(NewFaction(r.Str("faction")))),
            Technologies or FactionMercenarySets => r => Fragment(FactionPart(NewFaction(r.Str("faction")))),
            Characters => r => Fragment(FactionPart(NewFactionId(r.Str("faction")))),
            VictoryConditions => r => Fragment(FactionPart(NewFactionId(r.Str("start_pos_faction")))),
            Diplomacy => r => Fragment(FactionPart(NewFactionId(r.Str("faction1")) || NewFactionId(r.Str("faction2")))),
            PastEvents => r => Fragment(FactionPart(NewFactionId(r.Str("source")) || NewFactionId(r.Str("target")))),
            LandUnits or HordeDetails or StartingGeneralOptions => r => Fragment(FactionPart(NewCharacter(r.Str("general")))),
            CharacterTraits or CharacterAncillaries => r => Fragment(FactionPart(NewCharacter(r.Str("character_id")))),
            CharacterToSettlements => r => Fragment(FactionPart(NewCharacter(r.Str("character")))),
            Regions or RegionSlotTemplates => r => Fragment(RegionPart(NewRegion(r.Str("region")))),
            Settlements => r => Fragment(RegionPart(NewRegion(regionKeyById.GetValueOrDefault(r.Str("region"), "")))),
            RegionForeignSlots or EntityAssociationFactionRegions =>
                r => Fragment(RegionPart(NewRegion(r.Str("region")) || NewFaction(r.Str("faction")))),
            _ => null,
        };

        var files = new List<TableFile>();
        foreach (var table in tables.All.Where(t => t.Rows.Count > 0))
        {
            var rule = Rule(table.Name) ?? (_ => Fragment("other"));
            foreach (var group in table.Rows.GroupBy(rule).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var part = new DbTable(table.Name);
                part.Rows.AddRange(group);
                files.Add(new TableFile(group.Key, part));
            }
        }
        return files;
    }

    private static Dictionary<string, string> Map(DbTable? table, string from, string to)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in table?.Rows ?? [])
            map.TryAdd(row.Str(from), row.Str(to));
        return map;
    }
}
