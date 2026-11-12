using StartPos.Core.Esf;
using StartPos.Core.Tables;
using static StartPos.Core.Tables.StartPosTableNames;

namespace StartPos.Core.Decompile;

public sealed partial class StartPosDecompiler
{
    /// <summary>
    /// The field armies of every faction with their general's CQI. ARMY_ARRAY also holds each
    /// settlement's garrison, an army led by an invisible "colonel" the game makes up
    /// (MILITARY_FORCE #12 is the settlement's garrison residence for those, 0 otherwise).
    /// MILITARY_FORCE #0 is the army's CQI, #1 its general's.
    /// </summary>
    private IEnumerable<(EsfRecord Army, long GeneralCqi)> FieldArmies() =>
        _factionsByCqi.Values.OrderBy(f => f.Cqi)
            .SelectMany(f => f.Faction.Record("ARMY_ARRAY")?.Elements() ?? [])
            .Where(army => army.Int(12) == 0)
            .Select(army => (army, army.Int(1)));

    /// <summary>start_pos_land_units, start_pos_horde_details, start_pos_character_to_settlements.</summary>
    private void ExtractArmies()
    {
        var units = Table(LandUnits);
        var hordes = Table(HordeDetails);
        Each(FieldArmies(), a => $"Army {a.Army.Int(0)}", a =>
        {
            var (army, generalCqi) = a;
            if (!_charactersByMapCqi.TryGetValue(generalCqi, out var general))
            {
                _report.Warn($"Army {army.Int(0)} is led by a character with no start_pos ID (CQI {generalCqi}); its units are skipped.");
                return;
            }

            int index = 0;
            foreach (var unit in army.Record("UNIT_CONTAINER")?.Record("UNITS_ARRAY")?.Elements() ?? [])
            {
                if (index++ == 0)
                    continue;   // the general's own unit: start_pos_characters.override_general_unit
                string key = unit.Record("UNIT_RECORD_KEY")?.Str(0) ?? "";
                // UNIT #1/#2: current and maximum soldiers; the table has a percentage.
                long current = unit.Int(1), max = unit.Int(2);
                units.Add(new DbRow
                {
                    ["id"] = Id($"unit|{general.Id}|{index}|{key}"),
                    ["unit_type"] = key,
                    ["general"] = general.Id,
                    ["soldiers"] = max > 0 ? (int)Math.Round(100.0 * current / max) : 100,
                });
            }

            if (army.Record("MILITARY_FORCE_HORDE_DETAILS") is { } horde)
                HordeRow(hordes, general, horde);
        });

        // SETTLEMENT/GARRISON_RESIDENCE #1 is the CQI of the field army inside the settlement;
        // the table links its general.
        var generalsByArmy = FieldArmies().ToDictionary(a => a.Army.Int(0), a => a.GeneralCqi);
        var garrisons = Table(CharacterToSettlements);
        Each(_regions, r => $"Settlement {r.SettlementId}", r =>
        {
            long armyCqi = r.Settlement.Record("GARRISON_RESIDENCE")?.Int(1) ?? 0;
            if (armyCqi == 0)
                return;
            if (generalsByArmy.TryGetValue(armyCqi, out long generalCqi) && _charactersByMapCqi.TryGetValue(generalCqi, out var c))
                garrisons.Add(new DbRow { ["character"] = c.Id, ["settlement"] = SettlementId(r) });
            else
                _report.Warn($"Settlement {r.SettlementId} holds an army (CQI {armyCqi}) whose general has no start_pos ID; skipped.");
        });
    }

    /// <summary>
    /// MILITARY_FORCE_HORDE_DETAILS: MILITARY_FORCE_SLOT_MANAGER/SLOT_BLOCK has slot 0 (primary)
    /// and secondary slots 1-10, each with an optional BUILDING_BASE (#1 = building level);
    /// MILITARY_FORCE_DEVELOPMENT #0 is the starting development points. Hordes the table says
    /// nothing about still get a primary building from the game, so those rows come out too.
    /// </summary>
    private static void HordeRow(DbTable hordes, CharacterEntry general, EsfRecord horde)
    {
        var buildings = new string[11];
        int slotIndex = 0;
        foreach (var slot in horde.Record("MILITARY_FORCE_SLOT_MANAGER")?.Record("SLOT_BLOCK")?.Elements() ?? [])
        {
            if (slotIndex < buildings.Length)
                buildings[slotIndex] = slot.Record("MILITARY_FORCE_BUILDING_MANAGER")?.Record("BUILDING_BASE")?.Str(1) ?? "";
            slotIndex++;
        }
        long devPoints = horde.Record("MILITARY_FORCE_DEVELOPMENT")?.Int(0) ?? 0;
        if (buildings.All(string.IsNullOrEmpty) && devPoints == 0)
            return;

        var row = new DbRow
        {
            ["general"] = general.Id,
            ["primary_building"] = buildings[0] ?? "",
            ["starting_dev_points"] = devPoints,
        };
        for (int i = 1; i <= 10; i++)
            row[$"secondary_building_{i}"] = buildings[i] ?? "";
        hordes.Add(row);
    }
}
