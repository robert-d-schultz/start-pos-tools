using StartPos.Core.Tables;
using static StartPos.Core.Tables.StartPosTableNames;

namespace StartPos.Core.Decompile;

public sealed partial class StartPosDecompiler
{
    /// <summary>
    /// The game's codes for cai_personality_diplomatic_events, as CAI_LOG_EVENT stores them.
    /// The game numbers them itself (not in table order); these were matched against
    /// start_pos_past_events tables of startpos files built from them. 89 and 95 only ever
    /// appeared together, so which is cultural and which religious is a guess.
    /// </summary>
    private static readonly Dictionary<long, string> PastEventCodes = new()
    {
        [0] = "WAR",
        [1] = "WAR_PAST",
        [4] = "NON_AGGRESSION_PACT_BROKEN",
        [5] = "NON_AGGRESSION_PACT_PAST",
        [15] = "TRADE_AGREEMENT_PAST",
        [25] = "STATE_GIFT_SMALL",
        [26] = "STATE_GIFT_MEDIUM",
        [27] = "STATE_GIFT_LARGE",
        [33] = "BATTLE_ATTACK",
        [35] = "BATTLE_SUPPORT",
        [37] = "TROOPS_ENSLAVED",
        [38] = "TROOPS_EXECUTED",
        [42] = "CITY_SACKED",
        [43] = "CITY_RAZED",
        [47] = "HOSTILE_AGENT_ACTION",
        [48] = "TRESPASSING",
        [49] = "RAIDING",
        [53] = "REBELLED_AGAINST",
        [55] = "DEMANDED_TRIBUTE",
        [56] = "ULTIMATUM_ISSUED",
        [61] = "PAST_EVENT_NEGATIVE_MINOR",
        [62] = "PAST_EVENT_NEGATIVE_MODERATE",
        [63] = "PAST_EVENT_NEGATIVE_MAJOR",
        [64] = "PAST_EVENT_POSITIVE_MINOR",
        [65] = "PAST_EVENT_POSITIVE_MODERATE",
        [66] = "PAST_EVENT_POSITIVE_MAJOR",
        [72] = "DILEMMA_BONUS_SMALL",
        [84] = "CULTURAL_DIFFERENCES",
        [89] = "CULTURAL_SIMILARITIES_FACTION",
        [95] = "RELIGIOUS_SIMILARITIES_FACTION",
        [96] = "EXPANSIONISM",
    };

    /// <summary>
    /// Events the build logs by itself on turn 0 from the starting stances and treaties (a
    /// war, say); they come from start_pos_diplomacy, not start_pos_past_events.
    /// </summary>
    private static readonly HashSet<long> GeneratedEventCodes = [0, 3, 9, 12, 16, 78, 80];

    /// <summary>
    /// start_pos_past_events, from the campaign AI's interaction logs:
    /// CAI_INTERFACE/CAI_BDI_POOL/CAI_BDI_POOL_BELIEFS holds a CAI_INTERACTION_LOG per pair of
    /// factions (#0 and #1 their CAI ids). Its first #2 CAI_LOG_EVENTs are events of #0 towards
    /// #1, the next #3 events of #1 towards #0. A CAI_LOG_EVENT is #0 the event code and #1 the
    /// turn it happened on (-turns ago).
    /// </summary>
    private void ExtractPastEvents()
    {
        // CAI_WORLD_FACTIONS: each element's CAI_BDI_COMPONENT #0 is the faction's CAI id, its
        // CAI_FACTION #6 the faction CQI.
        var factionsByCaiId = new Dictionary<long, FactionEntry>();
        foreach (var element in _esf.CaiFactions)
            if (element.Record("CAI_BDI_COMPONENT")?.Int(0) is long caiId && element.Record("CAI_FACTION")?.Int(6) is long cqi &&
                _factionsByCqi.TryGetValue(cqi, out var f))
                factionsByCaiId[caiId] = f;

        var beliefs = _esf.Model.Record("CAI_INTERFACE")?.Record("CAI_BDI_POOL")?.Record("CAI_BDI_POOL_BELIEFS")?.Elements() ?? [];
        var table = Table(PastEvents);
        var unknownCodes = new SortedDictionary<long, int>();
        var seen = new HashSet<(string, string, string, long)>();
        Each(beliefs.Select(b => b.Record("CAI_INTERACTION_LOG")).OfType<Esf.EsfRecord>(), log => $"CAI interaction log {log.Int(0)}/{log.Int(1)}", log =>
        {
            if (!factionsByCaiId.TryGetValue(log.Int(0), out var a) || !factionsByCaiId.TryGetValue(log.Int(1), out var b))
                return;
            long firstGroup = log.Int(2);
            int index = 0;
            foreach (var e in log.Records("CAI_LOG_EVENT"))
            {
                var (source, target) = index++ < firstGroup ? (a, b) : (b, a);
                long code = e.Int(0), turnsAgo = -e.Int(1);
                if (turnsAgo == 0 && GeneratedEventCodes.Contains(code))
                    continue;
                if (!PastEventCodes.TryGetValue(code, out var name))
                {
                    unknownCodes[code] = unknownCodes.GetValueOrDefault(code) + 1;
                    continue;
                }
                if (!seen.Add((name, source.Id, target.Id, turnsAgo)))
                    continue;
                table.Add(new DbRow { ["event"] = name, ["source"] = source.Id, ["target"] = target.Id, ["turns_ago"] = turnsAgo });
            }
        });
        if (unknownCodes.Count > 0)
            _report.Warn("Past events with event codes not mapped to a cai_personality_diplomatic_events key were skipped: " +
                         string.Join(", ", unknownCodes.Select(p => $"code {p.Key} ×{p.Value}")) + ".");
    }
}
