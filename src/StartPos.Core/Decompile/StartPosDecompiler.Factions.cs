using StartPos.Core.Esf;
using StartPos.Core.Tables;
using static StartPos.Core.Tables.StartPosTableNames;

namespace StartPos.Core.Decompile;

public sealed partial class StartPosDecompiler
{
    /// <summary>start_pos_factions.</summary>
    private void ExtractFactions()
    {
        // CAI_FACTION #6 is the faction CQI; its CAI_FACTION_PERSONALITY #1 the starting
        // personality and #2 the personality group.
        var personalities = new Dictionary<long, EsfRecord>();
        foreach (var element in _esf.CaiFactions)
            if (element.Record("CAI_FACTION") is { } cai && cai.Record("CAI_FACTION_PERSONALITY") is { } personality)
                personalities[cai.Int(6)] = personality;

        var table = Table(Factions);
        Each(_factionsByCqi.Values.OrderBy(f => f.Cqi), f => $"Faction {f.Key}", f =>
        {
            var faction = f.Faction;
            var personality = personalities.GetValueOrDefault(f.Cqi);
            table.Add(new DbRow
            {
                ["ID"] = f.Id,
                ["faction"] = f.Key,
                ["campaign"] = Campaign,
                // FACTION_INFOS #1 is_major, #2 playable, #3 starting_order (FACTION #2/#3 repeat the first two).
                ["playable"] = f.Info?.Bool(2) ?? faction.Bool(3),
                ["treasury"] = faction.Record("FACTION_ECONOMICS")?.Int(0) ?? 0,
                ["starting_order"] = f.Info?.Int(3) ?? 9999,
                ["is_major"] = f.Info?.Bool(1) ?? faction.Bool(2),
                // The values right before FACTION_FLAG_AND_COLOURS are ai_manager, (unused), cdir config;
                // where they sit among the other values differs between factions.
                ["ai_manager"] = faction.ValueBefore("FACTION_FLAG_AND_COLOURS", 3)?.AsString() ?? "default",
                ["prestige_army"] = 0,
                ["prestige_navy"] = 0,
                ["prestige_economy"] = 0,
                ["prestige_enlightenment"] = 0,
                ["cai_starting_personality"] = personality?.Str(1) ?? "",
                ["long_description"] = "",
                ["cdir_military_generator_config"] = faction.ValueBefore("FACTION_FLAG_AND_COLOURS", 1)?.AsString() ?? "",
                ["cai_personality_group"] = personality?.Str(2) ?? "",
                ["faction_potential"] = faction.Record("FACTION_POTENTIAL")?.Str(1) ?? "",
            });
        });
    }

    /// <summary>
    /// start_pos_starting_general_options: FACTION_INFOS/STARTING_GENERAL_OPTION_BLOCK, each
    /// STARTING_GENERAL_OPTION being #0 general ID, #1 precedence, #2 frontend faction leader,
    /// #4 exclude other options.
    /// </summary>
    private void ExtractGeneralOptions()
    {
        var table = Table(StartingGeneralOptions);
        Each(_factionsByCqi.Values.OrderBy(f => f.Cqi), f => $"General options of {f.Key}", f =>
        {
            foreach (var option in f.Info?.Record("STARTING_GENERAL_OPTION_BLOCK")?.Elements() ?? [])
            {
                string general = option.Str(0);
                string leader = option.Str(2);
                table.Add(new DbRow
                {
                    ["general"] = general,
                    ["id"] = Id($"general_option|{general}|{leader}"),
                    ["precedence"] = option.Int(1),
                    ["frontend_faction_leader"] = leader,
                    ["exclude_other_options"] = option.Bool(4),
                });
            }
        });
    }
}
