using static StartPos.Core.Tables.StartPosTableNames;

namespace StartPos.Core.Tables;

/// <summary>
/// Narrows start_pos tables to what one campaign's build uses. Tables with a campaign column
/// are filtered on it; the rest follow the links from there: factions → characters → units,
/// traits, ancillaries, general options, horde details, garrisons; factions → diplomacy,
/// technologies, victory conditions, past events; regions → settlements.
/// </summary>
public static class CampaignScope
{
    public static void Apply(StartPosTables tables, string campaign)
    {
        foreach (var table in tables.All)
            if (table.Rows.Count > 0 && table.Rows[0].ContainsKey("campaign"))
                table.Rows.RemoveAll(r => r.Str("campaign") != campaign);
        if (tables.Find(Calendars) is { } calendars)
            calendars.Rows.RemoveAll(r => r.Str("campaign") != campaign);

        var factions = tables[Factions].Rows;
        var factionIds = Set(factions, "ID");
        var factionKeys = Set(factions, "faction");
        Keep(tables, Characters, r => factionIds.Contains(r.Str("faction")));
        var characterIds = Set(tables[Characters].Rows, "ID");

        Keep(tables, LandUnits, r => characterIds.Contains(r.Str("general")));
        Keep(tables, HordeDetails, r => characterIds.Contains(r.Str("general")));
        Keep(tables, StartingGeneralOptions, r => characterIds.Contains(r.Str("general")));
        Keep(tables, CharacterTraits, r => characterIds.Contains(r.Str("character_id")));
        Keep(tables, CharacterAncillaries, r => characterIds.Contains(r.Str("character_id")));
        Keep(tables, CharacterToSettlements, r => characterIds.Contains(r.Str("character")));

        Keep(tables, Diplomacy, r => factionIds.Contains(r.Str("faction1")) && factionIds.Contains(r.Str("faction2")));
        Keep(tables, PastEvents, r => factionIds.Contains(r.Str("source")) && factionIds.Contains(r.Str("target")));
        Keep(tables, VictoryConditions, r => factionIds.Contains(r.Str("start_pos_faction")));
        Keep(tables, Technologies, r => factionKeys.Contains(r.Str("faction")));

        var regionIds = Set(tables[Regions].Rows, "id");
        Keep(tables, Settlements, r => regionIds.Contains(r.Str("region")));
    }

    private static HashSet<string> Set(List<DbRow> rows, string column) => rows.Select(r => r.Str(column)).ToHashSet(StringComparer.Ordinal);

    private static void Keep(StartPosTables tables, string table, Predicate<DbRow> keep) =>
        tables.Find(table)?.Rows.RemoveAll(r => !keep(r));
}
