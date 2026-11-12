using StartPos.Core.Esf;
using StartPos.Core.Tables;
using static StartPos.Core.Tables.StartPosTableNames;

namespace StartPos.Core.Decompile;

public sealed partial class StartPosDecompiler
{
    private readonly Dictionary<long, long> _settlementIds = [];

    /// <summary>The start_pos_settlements id of a region's settlement (the startpos does not keep it).</summary>
    private long SettlementId(RegionEntry r) =>
        _settlementIds.TryGetValue(r.Cqi, out long id) ? id : _settlementIds[r.Cqi] = _ids.For("settlement|" + r.SettlementId);

    /// <summary>
    /// start_pos_regions, start_pos_settlements, start_pos_region_slot_templates,
    /// start_pos_region_foreign_slots.
    /// </summary>
    private void ExtractRegions()
    {
        // FACTION #7 is the CQI of the faction's capital region (0 if it has none; #6 is a home
        // region, which landless factions have too).
        var capitals = new HashSet<long>(_factionsByCqi.Values.Select(f => f.Faction.Int(7)).Where(c => c != 0));

        var regions = Table(Regions);
        var settlements = Table(Settlements);
        var templates = Table(RegionSlotTemplates);
        var foreignSlots = Table(RegionForeignSlots);
        Each(_regions, r => $"Region {r.Key}", r =>
        {
            var region = r.Region;
            var population = region.Record("POPULATION");
            var factors = population?.Record("REGION_FACTORS");
            var slots = region.Record("REGION_SLOT_MANAGER")?.Record("REGION_SLOT_ARRAY")?.Elements().ToList() ?? [];
            long owner = region.Int(4);

            regions.Add(new DbRow
            {
                ["id"] = r.Id,
                ["region"] = r.Key,
                ["campaign"] = Campaign,
                // REGION #4 is the owner's CQI; rebels (CQI 1, not a start_pos faction) means no owner.
                ["owning_faction"] = FactionId(owner),
                ["faction_capital"] = capitals.Contains(r.Cqi),
                // POPULATION #0 base population; REGION_FACTORS #1 population, #3 base max population
                // (POPULATION #1 is the max the game settled on: 1000000 where the table has 0).
                ["base_population"] = population?.Int(0) ?? 0,
                ["base_max_population"] = factors?.Int(3) ?? 0,
                ["population"] = factors?.Int(1) ?? 0,
                ["base_gdp"] = 0,
                ["town_wealth"] = 0,
                ["rebel_faction"] = region.Str(5),
                ["cultural_originator"] = region.Str(6),
                ["rebel_faction_name"] = "",
                ["settler_rebellions_enabled"] = false,
                ["capture_prestige"] = 0,
                ["long_description"] = "",
                ["development_points"] = 0,
                ["base_fertility"] = 0,
                ["slot_cap"] = slots.Count,
            });

            // REGION_SLOT #2 is the slot template, #3 the slot type (primary/port/secondary);
            // REGION_BUILDING_MANAGER/BUILDING_BASE #1 the building in it.
            string primary = "", port = "";
            var secondary = new List<string>();
            var seenTemplates = new HashSet<(string, string)>();
            foreach (var slot in slots)
            {
                string template = slot.Str(2), type = slot.Str(3);
                if (template.Length > 0 && seenTemplates.Add((template, type)))
                    templates.Add(new DbRow
                    {
                        ["campaign"] = Campaign,
                        ["id"] = Id($"slot_template|{r.Key}|{type}|{template}"),
                        ["region"] = r.Key,
                        ["slot_template"] = template,
                        ["slot_type"] = type,
                    });

                string building = slot.Record("REGION_BUILDING_MANAGER")?.Record("BUILDING_BASE")?.Str(1) ?? "";
                if (building.Length == 0)
                    continue;
                switch (type)
                {
                    case "primary": primary = building; break;
                    case "port": port = building; break;
                    default: secondary.Add(building); break;
                }
            }
            if (secondary.Count > 5)
                _report.Warn($"{r.SettlementId} has {secondary.Count} secondary buildings; start_pos_settlements holds 5, the rest are dropped.");

            // SETTLEMENT #1 wealth, #3 settlement type, #6 the building shown on the map (the
            // primary building unless primary_display_building overrides it).
            var s = r.Settlement;
            string display = s.Str(6);
            var settlement = new DbRow
            {
                ["settlement_id"] = r.SettlementId,
                ["region"] = r.Id,
                ["wealth"] = s.Int(1),
                ["id"] = SettlementId(r),
                ["primary_building"] = primary,
                ["port_building"] = port,
                ["startpos_slave_points"] = 0,
                ["primary_display_building"] = display != primary ? display : "",
                ["settlement_type"] = s.Str(3),
            };
            for (int i = 0; i < 5; i++)
                settlement[$"building{i + 1}"] = i < secondary.Count ? secondary[i] : "";
            settlements.Add(settlement);

            // FOREIGN_SLOT_MANAGER: per foreign slot set, #0 the owning faction's CQI and #1 the slot set.
            foreach (var foreign in region.Record("FOREIGN_SLOT_MANAGER")?.Elements() ?? [])
            {
                if (!_factionsByCqi.TryGetValue(foreign.Int(0), out var f))
                    continue;
                foreignSlots.Add(new DbRow
                {
                    ["campaign"] = Campaign,
                    ["faction"] = f.Key,
                    ["region"] = r.Key,
                    ["slot_set"] = foreign.Str(1),
                });
            }
        });
    }

    /// <summary>
    /// start_pos_diplomacy. Every faction has an OLD_DIPLOMACY_RELATIONSHIP with every other:
    /// #0 the other faction's CQI, #2 the stance between them, #13 the stance the start_pos row
    /// set (only on the side of the row's faction1), #14 flags, where the INITIAL_* flags are the
    /// row's treaties. Relationships without a row are neutral with no treaties, so rows that
    /// say just that are not recovered.
    /// </summary>
    private void ExtractDiplomacy()
    {
        var table = Table(Diplomacy);
        Each(_factionsByCqi.Values.OrderBy(f => f.Cqi), f => $"Diplomacy of {f.Key}", f =>
        {
            foreach (var rel in f.Faction.Record("OLD_DIPLOMACY_MANAGER")?.Record("OLD_DIPLOMACY_RELATIONSHIP")?.Elements() ?? [])
            {
                string stance = rel.Str(13);
                var flags = rel.Value(14).Value as string[] ?? [];
                bool access = flags.Contains("INITIAL_SOFT_MILITARY_ACCESS");
                bool trade = flags.Contains("INITIAL_TRADE_AGREEMENT");
                bool nap = flags.Contains("INITIAL_NON_AGGRESSION_PACT");
                if (stance == "neutral" && !access && !trade && !nap)
                    continue;
                if (!_factionsByCqi.TryGetValue(rel.Int(0), out var other))
                    continue;
                table.Add(new DbRow
                {
                    ["key"] = Id($"diplomacy|{f.Id}|{other.Id}"),
                    ["faction1"] = f.Id,
                    ["faction2"] = other.Id,
                    ["stance"] = stance,
                    ["grants_military_access"] = access,
                    ["grants_trade_agreement"] = trade,
                    ["non_aggression_pact"] = nap,
                });
            }
        });
    }

    /// <summary>start_pos_technologies: FACTION_TECHNOLOGY_MANAGER/RESEARCHED_TECHS, #0 the technology.</summary>
    private void ExtractTechnologies()
    {
        var table = Table(Technologies);
        foreach (var f in _factionsByCqi.Values.OrderBy(f => f.Cqi))
            foreach (var tech in f.Faction.Record("FACTION_TECHNOLOGY_MANAGER")?.Record("RESEARCHED_TECHS")?.Elements() ?? [])
                table.Add(new DbRow { ["faction"] = f.Key, ["technology"] = tech.Str(0) });
    }

    /// <summary>
    /// start_pos_victory_conditions: FACTION #5 lists the regions. The victory type is not kept;
    /// each region is written for both the long and the short victory, as CA's data has it.
    /// </summary>
    private void ExtractVictoryConditions()
    {
        var table = Table(VictoryConditions);
        foreach (var f in _factionsByCqi.Values.OrderBy(f => f.Cqi))
        {
            if (f.Faction.Value(5).Value is not string[] regions)
                continue;
            foreach (string region in regions)
                foreach (string type in (string[])["wh_main_victory_type_long", "wh_main_victory_type_short"])
                    table.Add(new DbRow { ["start_pos_faction"] = f.Id, ["region"] = region, ["victory_type"] = type });
        }
    }

    private static readonly string[] Seasons = ["season_spring", "season_summer", "season_autumn", "season_winter"];

    /// <summary>
    /// start_pos_calendars: CAMPAIGN_CALENDAR #0 turns per year, its DATE #0 the year and #1 the
    /// season (index into the seasons).
    /// </summary>
    private void ExtractCalendar()
    {
        if (_esf.Calendar is not { } calendar)
            return;
        var date = calendar.Record("DATE");
        long season = date?.Int(1) ?? 0;
        Table(Calendars).Add(new DbRow
        {
            ["campaign"] = Campaign,
            ["start_year"] = date?.Int(0) ?? 1,
            ["turns_per_year"] = calendar.Int(0),
            ["start_season"] = season >= 0 && season < Seasons.Length ? Seasons[season] : Seasons[0],
            ["start_week_of_year"] = date?.Int(2) ?? 0,
        });
    }

    /// <summary>
    /// start_pos_entity_association_faction_regions: ENTITY_ASSOCIATION_TAGGING_SYSTEM/
    /// ENTITY_TYPE_ASSOCIATIONS/ENTITY_ASSOCIATION, each #0 the tag, #1 the faction CQI (in
    /// the low 32 bits) and #2 the region CQI.
    /// </summary>
    private void ExtractEntityAssociations()
    {
        var table = Table(EntityAssociationFactionRegions);
        foreach (var type in _esf.EntityAssociations?.Elements() ?? [])
        {
            foreach (var association in type.Record("ENTITY_ASSOCIATION")?.Elements() ?? [])
            {
                long factionCqi = association.Int(1) & 0xFFFF_FFFF;
                long regionCqi = association.Int(2);
                if (!_factionsByCqi.TryGetValue(factionCqi, out var f) || !_regionsByCqi.TryGetValue(regionCqi, out var r))
                {
                    _report.Warn($"Entity association '{association.Str(0)}' links faction CQI {factionCqi} and region CQI {regionCqi}, which are not both known; skipped.");
                    continue;
                }
                table.Add(new DbRow { ["campaign"] = Campaign, ["faction"] = f.Key, ["region"] = r.Key, ["tag"] = association.Str(0) });
            }
        }
    }
}
