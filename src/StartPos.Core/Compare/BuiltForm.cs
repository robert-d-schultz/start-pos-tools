using StartPos.Core.Tables;
using static StartPos.Core.Tables.StartPosTableNames;

namespace StartPos.Core.Compare;

/// <summary>
/// Start_pos tables reduced to what a built startpos keeps of them, so that tables compare with a
/// decompiled startpos (or two start_pos packs compare on what their builds would differ in)
/// without the noise of what the build drops or decides by itself. These are the rules the
/// decompiler's notes describe, applied to the tables' side.
/// </summary>
public static class BuiltForm
{
    /// <summary>
    /// Columns a built startpos does not hold (or the decompiler cannot read back), per table.
    /// The decompiler writes them with a fixed value.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> NotKept = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [Characters] = ["loyalty", "model", "death_type", "turns_died_before_start"],
        [Factions] = ["prestige_army", "prestige_navy", "prestige_economy", "prestige_enlightenment", "long_description"],
        [Regions] = ["base_gdp", "town_wealth", "rebel_faction_name", "settler_rebellions_enabled", "capture_prestige",
                     "long_description", "development_points", "base_fertility"],
        [Settlements] = ["startpos_slave_points"],
    };

    /// <summary>What <see cref="Apply"/> and <see cref="Reconcile"/> leave out, as sentences for the log.</summary>
    public static readonly string[] Notes =
    [
        "Columns a built startpos does not keep are not compared: " +
            string.Join("; ", NotKept.Select(p => $"{p.Key}: {string.Join(", ", p.Value)}")) + ".",
        "Tables are compared as a build would leave them: neutral diplomacy rows with no treaties, units and horde " +
            "details of generals in the recruitment pool, empty horde rows, duplicate rows and technologies that are in " +
            "no tech tree are dropped; a general's default unit, the only art set of a subtype, and a display building " +
            "that is the primary building count as not set; each victory region counts for the long and the short victory.",
        "Against a decompiled startpos, what the build decides by itself is not a difference: the position of a " +
            "character the tables leave at 0,0, the art set of one they give none, names the game rejected, and the " +
            "primary building the game gives a horde the tables say nothing about.",
    ];

    private const string LongVictory = "wh_main_victory_type_long", ShortVictory = "wh_main_victory_type_short";

    /// <summary>
    /// A copy of <paramref name="tables"/> as a build would leave it. Every copied row is recorded
    /// in <paramref name="originals"/> (copy → row it came from), so differing rows can be reported
    /// as the tables hold them. <paramref name="game"/> is the game as the tables' pack sees it;
    /// without it the rules that need the game's tables are skipped.
    /// </summary>
    public static StartPosTables Apply(StartPosTables tables, GameLookups? game, Dictionary<DbRow, DbRow> originals)
    {
        var result = new StartPosTables();
        foreach (var table in tables.All)
        {
            var copy = result[table.Name];
            string[] notKept = NotKept.GetValueOrDefault(table.Name, []);
            foreach (var row in table.Rows)
            {
                var c = new DbRow(row);
                foreach (string column in notKept)
                    c.Remove(column);
                originals[c] = originals.GetValueOrDefault(row, row);
                copy.Add(c);
            }
        }

        // A general in the recruitment pool is not on the map: no position, no army.
        var characters = result[Characters].Rows;
        var pool = characters.Where(r => r.Str("is_in_generals_pool") == "true").Select(r => r.Str("ID")).ToHashSet(StringComparer.Ordinal);
        foreach (var c in characters)
        {
            if (pool.Contains(c.Str("ID")))
            {
                c["startx"] = 0f;
                c["starty"] = 0f;
            }
            // Only a unit other than the subtype's default, and an art set the build could not
            // have picked by itself, are anything the startpos records.
            string subtype = c.Str("subtype");
            if (game != null && game.DefaultGeneralUnits.GetValueOrDefault(subtype) == c.Str("override_general_unit"))
                c["override_general_unit"] = "";
            if (game != null && game.ArtSetCounts.GetValueOrDefault(subtype) <= 1)
                c["portrait_id"] = "";
        }
        result[LandUnits].Rows.RemoveAll(r => pool.Contains(r.Str("general")));
        result[HordeDetails].Rows.RemoveAll(r => pool.Contains(r.Str("general")) || IsEmptyHorde(r));

        // Neutral with no treaties is what factions without a row have.
        result[Diplomacy].Rows.RemoveAll(r => r.Str("stance") == "neutral" && r.Str("grants_military_access") != "true" &&
                                              r.Str("grants_trade_agreement") != "true" && r.Str("non_aggression_pact") != "true");

        if (game is { Technologies.Count: > 0 })
            result[Technologies].Rows.RemoveAll(r => !game.Technologies.Contains(r.Str("technology")));

        foreach (var s in result[Settlements].Rows)
            if (s.Str("primary_display_building") == s.Str("primary_building"))
                s["primary_display_building"] = "";

        foreach (string table in (string[])[CharacterTraits, CharacterAncillaries, Technologies])
            Deduplicate(result[table], StartPosMatchSpecs.All[table].Match);

        // The startpos keeps a faction's victory regions, not the victory type.
        var victory = result[VictoryConditions];
        var byRegion = victory.Rows.GroupBy(r => (Faction: r.Str("start_pos_faction"), Region: r.Str("region"))).ToList();
        victory.Rows.Clear();
        foreach (var group in byRegion)
        {
            foreach (string type in (string[])[LongVictory, ShortVictory])
            {
                var row = group.FirstOrDefault(r => r.Str("victory_type") == type) ?? new DbRow(group.First()) { ["victory_type"] = type };
                victory.Add(row);
            }
        }
        return result;
    }

    /// <summary>
    /// Makes <paramref name="tables"/> (as <see cref="Apply"/> left them) agree with a decompiled
    /// startpos, <paramref name="built"/>, wherever the build decided by itself what the tables
    /// left open, or dropped what the tables said.
    /// </summary>
    public static void Reconcile(StartPosTables built, StartPosTables tables)
    {
        var builtCharacters = (built.Find(Characters)?.Rows ?? []).GroupBy(r => r.Str("ID")).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var c in tables.Find(Characters)?.Rows ?? [])
        {
            if (!builtCharacters.TryGetValue(c.Str("ID"), out var b))
                continue;
            // A name the game rejected (from another culture's names group, say) is not in the startpos.
            foreach (string column in (string[])["Name", "Surname"])
                if (b.Str(column).Length == 0)
                    c[column] = b.GetValueOrDefault(column);
            // No art set: the build picks one at random.
            if (c.Str("portrait_id").Length == 0)
                c["portrait_id"] = b.GetValueOrDefault("portrait_id");
            // 0,0 on the map: the build places the character (in its settlement's garrison, say).
            if (c.Str("startx") == "0" && c.Str("starty") == "0")
            {
                c["startx"] = b.GetValueOrDefault("startx");
                c["starty"] = b.GetValueOrDefault("starty");
            }
        }

        // A horde the tables say nothing about still gets a primary building from the game.
        var hordes = (tables.Find(HordeDetails)?.Rows ?? []).Select(r => r.Str("general")).ToHashSet(StringComparer.Ordinal);
        built.Find(HordeDetails)?.Rows.RemoveAll(r => !hordes.Contains(r.Str("general")) && r.Str("starting_dev_points") == "0" &&
                                                     Enumerable.Range(1, 10).All(i => r.Str($"secondary_building_{i}").Length == 0));
    }

    private static bool IsEmptyHorde(DbRow r) =>
        r.Str("primary_building").Length == 0 && r.Str("starting_dev_points") == "0" &&
        Enumerable.Range(1, 10).All(i => r.Str($"secondary_building_{i}").Length == 0);

    private static void Deduplicate(DbTable table, string[] key)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        table.Rows.RemoveAll(r => !seen.Add(string.Join("\u001f", key.Select(r.Str))));
    }
}
