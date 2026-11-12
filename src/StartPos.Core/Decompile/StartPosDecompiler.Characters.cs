using StartPos.Core.Esf;
using StartPos.Core.Tables;
using static StartPos.Core.Tables.StartPosTableNames;

namespace StartPos.Core.Decompile;

public sealed partial class StartPosDecompiler
{
    private const string NamesPrefix = "names_name_";

    /// <summary>start_pos_characters, start_pos_character_traits, start_pos_character_ancillaries.</summary>
    private void ExtractCharacters()
    {
        // GOVERNMENT/CHARACTER_POSTS: CHARACTER_POST #1 is the post, #2 the CQI of the holder (0 = vacant).
        var posts = new Dictionary<long, string>();
        foreach (var f in _factionsByCqi.Values)
            foreach (var post in f.Faction.Record("GOVERNMENT")?.Record("CHARACTER_POSTS")?.Blocks.SelectMany(b => b).OfType<EsfRecord>() ?? [])
                if (post.Name == "CHARACTER_POST" && post.Int(2) != 0)
                    posts.TryAdd(post.Int(2), post.Str(1));

        // An army's general's own unit is the first unit of the army.
        var generalUnits = new Dictionary<long, string>();
        foreach (var (army, generalCqi) in FieldArmies())
            if (army.Record("UNIT_CONTAINER")?.Record("UNITS_ARRAY")?.Elements().FirstOrDefault() is { } unit)
                generalUnits.TryAdd(generalCqi, unit.Record("UNIT_RECORD_KEY")?.Str(0) ?? "");

        var characters = Table(Characters);
        Each(_characters, c => $"Character {c.Id}", c =>
        {
            var d = c.Details;
            var names = Names(d.Record("CHARACTER_NAME"));
            // CHARACTER_DETAILS #3 is a list of flags: male, is_immortal, is_named_character.
            var flags = d.Value(3).Value as string[] ?? [];
            var locomotable = c.MapRecord?.Record("LOCOMOTABLE");
            string subtype = d.Str(8);

            // CHARACTER_DETAILS #20 is the unit of a general who has no army; otherwise it is
            // the army's first unit. Either way only a unit other than the subtype's default
            // (agent_subtypes.associated_unit_override) needs to be in the table.
            string overrideUnit = d.Str(20);
            if (overrideUnit.Length == 0 && generalUnits.TryGetValue(c.MapCqi, out var unit))
                overrideUnit = unit;
            if (_game?.DefaultGeneralUnits.GetValueOrDefault(subtype) == overrideUnit)
                overrideUnit = "";

            // The art set the build picked. When the subtype has only one the build picks it
            // anyway; otherwise it was picked at random (or set), and is kept so a rebuild looks the same.
            string portrait = d.Record("CHARACTER_DETAILS_ART_SET_INFO")?.Str(5) ?? "";
            if (_game != null && _game.ArtSetCounts.GetValueOrDefault(subtype) <= 1)
                portrait = "";

            characters.Add(new DbRow
            {
                ["ID"] = c.Id,
                ["faction"] = c.Faction?.Id ?? "",
                ["Name"] = names.GetValueOrDefault("forename", ""),
                ["Surname"] = names.GetValueOrDefault("family_name", ""),
                ["Type"] = d.Str(7),
                // LOCOMOTABLE #1/#2 are the hex-grid position the table's startx/starty are in.
                ["startx"] = (float)(locomotable?.Int(1) ?? 0),
                ["starty"] = (float)(locomotable?.Int(2) ?? 0),
                ["ministerial_position"] = posts.GetValueOrDefault(c.MapCqi, ""),
                ["portrait_id"] = portrait,
                ["model"] = "",
                ["immortal"] = flags.Contains("is_immortal"),
                ["override_general_unit"] = overrideUnit,
                // CHARACTER_DETAILS #2 is the CQI of the character on the map; 0 = in the recruitment pool.
                ["is_in_generals_pool"] = c.MapCqi == 0,
                ["is_male"] = flags.Contains("male"),
                ["loyalty"] = 0,
                ["clan_name"] = names.GetValueOrDefault("clan_name", ""),
                ["other_name"] = names.GetValueOrDefault("other", ""),
                ["death_type"] = "",
                ["turns_died_before_start"] = 0,
                ["subtype"] = subtype,
                ["unique"] = flags.Contains("is_named_character"),
            });

            Traits(c);
            Ancillaries(c);
        });
    }

    /// <summary>
    /// CHARACTER_DETAILS/TRAITS/TRAIT: per trait #0 the trait and #1 its points; v1 records add
    /// #2, the trait level. For v0 records (older game builds) the level is looked up in
    /// character_trait_levels: the highest level whose threshold the points reach.
    /// </summary>
    private void Traits(CharacterEntry c)
    {
        var traitArray = c.Details.Record("TRAITS")?.Record("TRAIT");
        foreach (var trait in traitArray?.Elements() ?? [])
        {
            string level = trait.Children.OfType<EsfValue>().Count() >= 3
                ? trait.Str(2)
                : _game?.TraitLevel(trait.Str(0), trait.Int(1)) ?? trait.Str(0);
            Table(CharacterTraits).Add(new DbRow
            {
                ["id"] = Id($"trait|{c.Id}|{level}"),
                ["character_id"] = c.Id,
                ["trait_level"] = level,
            });
        }
    }

    /// <summary>
    /// CHARACTER_DETAILS/ANCILLARY_MANAGER/ANCILLARY #1. Ancillaries a skill of the character
    /// grants (a lord's default mount, say) come from the skill, not from the table, and are
    /// left out. CAMPAIGN_SKILLS #0 is the innate skill and CAMPAIGN_SKILLS_BLOCK lists the
    /// skills taken (#0 skill, #1 level).
    /// </summary>
    private void Ancillaries(CharacterEntry c)
    {
        var granted = new HashSet<string>(StringComparer.Ordinal);
        if (_game != null && c.Details.Record("CAMPAIGN_SKILLS") is { } skills)
            foreach (string skill in (skills.Record("CAMPAIGN_SKILLS_BLOCK")?.Elements().Select(e => e.Str(0)) ?? []).Append(skills.Str(0)))
                if (_game.SkillAncillaries.TryGetValue(skill, out var fromSkill))
                    granted.UnionWith(fromSkill);

        foreach (var ancillary in c.Details.Record("ANCILLARY_MANAGER")?.Records("ANCILLARY") ?? [])
        {
            string key = ancillary.Str(1);
            if (granted.Contains(key))
                continue;
            Table(CharacterAncillaries).Add(new DbRow
            {
                ["id"] = Id($"ancillary|{c.Id}|{key}"),
                ["character_id"] = c.Id,
                ["ancillary"] = key,
            });
        }
    }

    /// <summary>
    /// CHARACTER_NAME/CHARACTER_NAME_ELEMENT: per element a CAMPAIGN_LOCALISATION ("names_name_&lt;id&gt;"
    /// or empty) and the element's kind (clan_name, forename, family_name, other). Returns kind → names id.
    /// </summary>
    private static Dictionary<string, string> Names(EsfRecord? characterName)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in characterName?.Record("CHARACTER_NAME_ELEMENT")?.Elements() ?? [])
        {
            string loc = element.Record("CAMPAIGN_LOCALISATION")?.Str(0) ?? "";
            if (loc.StartsWith(NamesPrefix, StringComparison.Ordinal))
                result.TryAdd(element.Str(0), loc[NamesPrefix.Length..]);
        }
        return result;
    }
}
