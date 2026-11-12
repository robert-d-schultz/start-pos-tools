using StartPos.Core.Compare;
using StartPos.Core.Decompile;
using StartPos.Core.Tables;
using Xunit;
using static StartPos.Core.Tables.StartPosTableNames;

namespace StartPos.Tests;

public class IdAllocatorTests
{
    [Fact]
    public void SameNameGivesSameIdAcrossRuns()
    {
        Assert.Equal(new IdAllocator().For("settlement|x"), new IdAllocator().For("settlement|x"));
    }

    [Fact]
    public void IdsArePositive31BitAndAvoidReservedOnes()
    {
        var first = new IdAllocator();
        long id = first.For("unit|1");

        var second = new IdAllocator();
        second.Reserve(id);
        long other = second.For("unit|1");

        Assert.NotEqual(id, other);
        Assert.InRange(other, 1, int.MaxValue);
    }
}

public class TableDiffTests
{
    private static StartPosTables Tables(params (string Table, DbRow Row)[] rows)
    {
        var tables = new StartPosTables();
        foreach (var (table, row) in rows)
            tables[table].Add(row);
        return tables;
    }

    [Fact]
    public void MatchesRowsIgnoringGeneratedIds()
    {
        var left = Tables((CharacterTraits, new DbRow { ["id"] = "1", ["character_id"] = "7", ["trait_level"] = "brave" }));
        var right = Tables((CharacterTraits, new DbRow { ["id"] = "99", ["character_id"] = "7", ["trait_level"] = "brave" }));

        Assert.True(TableDiff.Compare(left, right).Single().Identical);
    }

    [Fact]
    public void ComparesLinksThroughWhatTheyPointAt()
    {
        // Settlement ids differ between the two sides; the settlement they name is the same.
        var left = Tables(
            (Settlements, new DbRow { ["settlement_id"] = "settlement:a", ["id"] = "1" }),
            (CharacterToSettlements, new DbRow { ["character"] = "5", ["settlement"] = "1" }));
        var right = Tables(
            (Settlements, new DbRow { ["settlement_id"] = "settlement:a", ["id"] = "2" }),
            (CharacterToSettlements, new DbRow { ["character"] = "5", ["settlement"] = "2" }));

        Assert.All(TableDiff.Compare(left, right), d => Assert.True(d.Identical));
    }

    [Fact]
    public void ReportsDifferingColumns()
    {
        var left = Tables((Factions, new DbRow { ["faction"] = "f", ["treasury"] = 5000 }));
        var right = Tables((Factions, new DbRow { ["faction"] = "f", ["treasury"] = "2000" }));

        var diff = TableDiff.Compare(left, right).Single();
        Assert.Equal(1, diff.ColumnMismatches["treasury"].Count);
    }

    [Fact]
    public void IgnoredCampaignKeyMatchesTwoCampaigns()
    {
        var left = Tables(
            (Calendars, new DbRow { ["campaign"] = "cr_combi_expanded", ["year"] = "2500" }),
            (Factions, new DbRow { ["campaign"] = "cr_combi_expanded", ["faction"] = "f" }));
        var right = Tables(
            (Calendars, new DbRow { ["campaign"] = "wh3_main_combi", ["year"] = "2500" }),
            (Factions, new DbRow { ["campaign"] = "wh3_main_combi", ["faction"] = "f" }));

        Assert.Contains(TableDiff.Compare(left, right), d => !d.Identical);
        Assert.All(TableDiff.Compare(left, right, ignoreColumns: ["campaign"]), d => Assert.True(d.Identical));
    }

    [Fact]
    public void DifferingRowsAreKeptAsTheTableHoldsThem()
    {
        var left = Tables(
            (Settlements, new DbRow { ["settlement_id"] = "settlement:a", ["id"] = "1" }),
            (CharacterToSettlements, new DbRow { ["character"] = "5", ["settlement"] = "1", ["primary"] = true }),
            (Factions, new DbRow { ["faction"] = "only_left" }));
        var right = Tables(
            (Settlements, new DbRow { ["settlement_id"] = "settlement:a", ["id"] = "2" }),
            (CharacterToSettlements, new DbRow { ["character"] = "5", ["settlement"] = "2", ["primary"] = false }));

        var diffs = TableDiff.Compare(left, right).ToDictionary(d => d.Table);

        // Matched but differing: each side's row, with the settlement id it has (not the translated key).
        Assert.Equal("1", Assert.Single(diffs[CharacterToSettlements].LeftDiffering).Str("settlement"));
        Assert.Equal("2", Assert.Single(diffs[CharacterToSettlements].RightDiffering).Str("settlement"));
        Assert.Equal("only_left", Assert.Single(diffs[Factions].LeftDiffering).Str("faction"));
        Assert.Empty(diffs[Factions].RightDiffering);
        Assert.Empty(diffs[Settlements].LeftDiffering);     // only the arbitrary id differs
    }
}

public class BuiltFormTests
{
    private static StartPosTables Tables(params (string Table, DbRow Row)[] rows)
    {
        var tables = new StartPosTables();
        foreach (var (table, row) in rows)
            tables[table].Add(row);
        return tables;
    }

    private static DbRow Character(string id, bool pool = false, float x = 0, float y = 0, string portrait = "", string name = "n") => new()
    {
        ["ID"] = id, ["subtype"] = "lord", ["is_in_generals_pool"] = pool, ["startx"] = x, ["starty"] = y,
        ["portrait_id"] = portrait, ["override_general_unit"] = "", ["Name"] = name, ["loyalty"] = 0,
    };

    [Fact]
    public void DropsWhatTheBuildDrops()
    {
        var game = new GameLookups();
        game.DefaultGeneralUnits["lord"] = "lord_unit";
        game.ArtSetCounts["lord"] = 1;
        game.Technologies.Add("tech_in_tree");
        var pooled = Character("1", pool: true, x: 5, y: 5);
        var onMap = Character("2", x: 3, y: 4, portrait: "art_1");
        onMap["override_general_unit"] = "lord_unit";
        onMap["loyalty"] = 10;
        var tables = Tables(
            (Characters, pooled), (Characters, onMap),
            (LandUnits, new DbRow { ["general"] = "1", ["unit_type"] = "u" }),
            (LandUnits, new DbRow { ["general"] = "2", ["unit_type"] = "u" }),
            (Diplomacy, new DbRow { ["faction1"] = "a", ["faction2"] = "b", ["stance"] = "neutral", ["non_aggression_pact"] = false }),
            (Diplomacy, new DbRow { ["faction1"] = "a", ["faction2"] = "c", ["stance"] = "war", ["non_aggression_pact"] = false }),
            (Technologies, new DbRow { ["faction"] = "a", ["technology"] = "tech_in_tree" }),
            (Technologies, new DbRow { ["faction"] = "a", ["technology"] = "tech_in_tree" }),
            (Technologies, new DbRow { ["faction"] = "a", ["technology"] = "old_tech" }),
            (VictoryConditions, new DbRow { ["start_pos_faction"] = "a", ["region"] = "r", ["victory_type"] = "wh_main_victory_type_long" }));
        var originals = new Dictionary<DbRow, DbRow>(ReferenceEqualityComparer.Instance);

        var built = BuiltForm.Apply(tables, game, originals);

        var characters = built[Characters].Rows.ToDictionary(r => r.Str("ID"));
        Assert.Equal("0", characters["1"].Str("startx"));          // the pool has no position
        Assert.Equal("", characters["2"].Str("override_general_unit"));
        Assert.Equal("", characters["2"].Str("portrait_id"));        // the only art set
        Assert.False(characters["2"].ContainsKey("loyalty"));
        Assert.Equal("2", Assert.Single(built[LandUnits].Rows).Str("general"));
        Assert.Equal("war", Assert.Single(built[Diplomacy].Rows).Str("stance"));
        Assert.Equal("tech_in_tree", Assert.Single(built[Technologies].Rows).Str("technology"));
        Assert.Equal(2, built[VictoryConditions].Rows.Count);
        // The input is untouched, and every copy leads back to its row.
        Assert.Equal(10, onMap["loyalty"]);
        Assert.Same(onMap, originals[characters["2"]]);
    }

    [Fact]
    public void WhatTheBuildPicksIsNoDifference()
    {
        var builtSide = Tables(
            (Characters, Character("1", x: 300, y: 200, portrait: "art_3", name: "")),
            (HordeDetails, new DbRow { ["general"] = "1", ["primary_building"] = "camp", ["starting_dev_points"] = 0L }));
        var tablesSide = Tables((Characters, Character("1", name: "Grom")));
        var originals = new Dictionary<DbRow, DbRow>(ReferenceEqualityComparer.Instance);
        var left = BuiltForm.Apply(builtSide, null, originals);
        var right = BuiltForm.Apply(tablesSide, null, originals);

        BuiltForm.Reconcile(left, right);

        Assert.All(TableDiff.Compare(left, right), d => Assert.True(d.Identical));
        // The input still has what the tables say.
        Assert.Equal("Grom", tablesSide[Characters].Rows[0].Str("Name"));
    }

    [Fact]
    public void ARealDifferenceStays()
    {
        var builtSide = Tables((Characters, Character("1", x: 300, y: 200, portrait: "art_3")));
        var tablesSide = Tables((Characters, Character("1", x: 10, y: 20, portrait: "art_1")));
        var originals = new Dictionary<DbRow, DbRow>(ReferenceEqualityComparer.Instance);
        var left = BuiltForm.Apply(builtSide, null, originals);
        var right = BuiltForm.Apply(tablesSide, null, originals);

        BuiltForm.Reconcile(left, right);

        var diff = TableDiff.Compare(left, right).Single(d => d.Table == Characters);
        Assert.Equal(["portrait_id", "startx", "starty"], diff.ColumnMismatches.Keys);
    }
}

public class PackLayoutTests
{
    [Fact]
    public void SplitsVanillaAndNewFactionsAndRegions()
    {
        var tables = new StartPosTables();
        tables[Factions].Add(new DbRow { ["ID"] = "1", ["faction"] = "wh_main_emp_empire" });
        tables[Factions].Add(new DbRow { ["ID"] = "2", ["faction"] = "mod_new_faction" });
        tables[Characters].Add(new DbRow { ["ID"] = "10", ["faction"] = "2" });
        tables[LandUnits].Add(new DbRow { ["id"] = "100", ["general"] = "10", ["unit_type"] = "u" });
        tables[Regions].Add(new DbRow { ["id"] = "50", ["region"] = "mod_region" });
        tables[Settlements].Add(new DbRow { ["settlement_id"] = "s", ["region"] = "50" });
        tables[Diplomacy].Add(new DbRow { ["faction1"] = "1", ["faction2"] = "2" });
        tables[Calendars].Add(new DbRow { ["campaign"] = "c" });

        var game = new GameLookups();
        game.VanillaFactions.Add("wh_main_emp_empire");

        var files = PackLayout.Split(tables, "c", game).ToDictionary(f => (f.Table.Name, f.Fragment), f => f.Table.Rows.Count);

        Assert.Equal(1, files[(Factions, "!!!c_vanilla_factions")]);
        Assert.Equal(1, files[(Factions, "!!!c_new_factions")]);
        Assert.Equal(1, files[(LandUnits, "!!!c_new_factions")]);          // through its general's faction
        Assert.Equal(1, files[(Settlements, "!!!c_new_regions")]);         // through its region
        Assert.Equal(1, files[(Diplomacy, "!!!c_new_factions")]);          // one side is new
        Assert.Equal(1, files[(Calendars, "!!!c_campaign")]);
    }
}

public class StartPosListingTests
{
    private static StartPosFile File(string folder, string campaign, string hash) =>
        new($"campaigns/{folder}/startpos.esf", folder, campaign, $"x/{folder}.esf", hash);

    [Fact]
    public void CopiesInOtherFoldersAreNotSeparateCampaigns()
    {
        // IEE ships its cr_combi_expanded startpos in wh3_main_combi too, for multiplayer.
        var listing = new StartPosListing([File("wh3_main_combi", "cr_combi_expanded", "A"), File("cr_combi_expanded", "cr_combi_expanded", "A")]);

        var only = Assert.Single(listing.Campaigns);
        Assert.Equal("cr_combi_expanded", only.Folder);
        Assert.Contains("copy", Assert.Single(listing.Notes));
        Assert.Same(only, listing.Find(null));
        Assert.Same(only, listing.Find("cr_combi_expanded"));
    }

    [Fact]
    public void FolderNameStillFindsAFile()
    {
        var listing = new StartPosListing([File("wh3_main_combi", "cr_combi_expanded", "A"), File("cr_combi_expanded", "cr_combi_expanded", "A")]);

        Assert.Equal("cr_combi_expanded", listing.Find("wh3_main_combi").Campaign);
    }

    [Fact]
    public void SeveralCampaignsNeedAChoice()
    {
        var listing = new StartPosListing([File("a", "a", "A"), File("b", "b", "B")]);

        Assert.Throws<InvalidOperationException>(() => listing.Find(null));
        Assert.Equal("b", listing.Find("b").Campaign);
    }
}
