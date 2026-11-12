using StartPos.Core.Esf;

namespace StartPos.Core.Model;

/// <summary>
/// A Warhammer III startpos.esf, with names for the parts of the tree the start_pos tables
/// end up in. Everything here was mapped against a startpos built from known tables; the
/// comments say which record and value each piece comes from.
///
/// Layout (CBAB, LZMA-compressed as a whole):
///   CAMPAIGN_STARTPOS
///     SAVE_GAME_HEADER
///     CAMPAIGN_PREOPEN_MAP_INFO        campaign key, map name, FACTION_INFOS (frontend data)
///     CAMPAIGN_ENV
///       CAMPAIGN_SETUP
///       CAMPAIGN_MODEL
///         CAMPAIGN_CALENDAR
///         WORLD                        factions, regions, family tree, ...
///         CAI_INTERFACE                campaign AI: personalities, interaction logs
/// </summary>
public sealed class StartPosEsf
{
    public EsfDocument Document { get; }
    public EsfRecord Root { get; }
    public EsfRecord PreOpenMapInfo { get; }
    public EsfRecord Model { get; }
    public EsfRecord World { get; }

    public StartPosEsf(EsfDocument document)
    {
        Document = document;
        Root = document.Root;
        if (Root.Name != "CAMPAIGN_STARTPOS")
            throw new EsfFormatException($"Not a startpos: the root record is {Root.Name}, not CAMPAIGN_STARTPOS.");
        PreOpenMapInfo = Root.Required("CAMPAIGN_PREOPEN_MAP_INFO");
        Model = Root.Required("CAMPAIGN_ENV").Required("CAMPAIGN_MODEL");
        World = Model.Required("WORLD");
    }

    public static StartPosEsf Load(string path) => new(EsfDocument.Load(path));

    /// <summary>
    /// The campaign key of a startpos: CAMPAIGN_PREOPEN_MAP_INFO #0, else SAVE_GAME_HEADER #9.
    /// Both sit outside the compressed block, so <paramref name="document"/> may be the outer tree only.
    /// </summary>
    public static string ReadCampaign(EsfDocument document)
    {
        var root = document.Root;
        if (root.Record("CAMPAIGN_PREOPEN_MAP_INFO") is { } preOpen && preOpen.Values.Count > 0)
            return preOpen.Str(0);
        if (root.Record("SAVE_GAME_HEADER") is { } header && header.Values.Count > 9)
            return header.Str(9);
        throw new EsfFormatException($"Not a startpos: {root.Name} has no CAMPAIGN_PREOPEN_MAP_INFO.");
    }

    /// <summary>CAMPAIGN_PREOPEN_MAP_INFO #0, e.g. "cr_combi_expanded".</summary>
    public string Campaign => PreOpenMapInfo.Str(0);

    /// <summary>CAMPAIGN_PREOPEN_MAP_INFO #1, the campaign map folder, e.g. "cr_combi_expanded_map_1".</summary>
    public string MapName => PreOpenMapInfo.Str(1);

    /// <summary>WORLD/FACTION_ARRAY: one FACTION per start_pos_factions row (rebels are separate).</summary>
    public IEnumerable<EsfRecord> Factions => World.Required("FACTION_ARRAY").Elements();

    /// <summary>CAMPAIGN_PREOPEN_MAP_INFO/FACTION_INFOS: frontend data per faction, keyed by faction key (#0).</summary>
    public IEnumerable<EsfRecord> FactionInfos => PreOpenMapInfo.Required("FACTION_INFOS").Elements();

    /// <summary>WORLD/REGION_MANAGER/REGIONS_ARRAY: one REGION per land region.</summary>
    public IEnumerable<EsfRecord> Regions => World.Required("REGION_MANAGER").Required("REGIONS_ARRAY").Elements();

    /// <summary>WORLD/FAMILY_TREE: one FAMILY_MEMBER per character the campaign knows, on the map or not.</summary>
    public IEnumerable<EsfRecord> FamilyMembers => World.Required("FAMILY_TREE").Records("FAMILY_MEMBER");

    public EsfRecord? Calendar => Model.Record("CAMPAIGN_CALENDAR");

    /// <summary>WORLD/ENTITY_ASSOCIATION_TAGGING_SYSTEM/ENTITY_TYPE_ASSOCIATIONS.</summary>
    public EsfRecord? EntityAssociations => World.Record("ENTITY_ASSOCIATION_TAGGING_SYSTEM")?.Record("ENTITY_TYPE_ASSOCIATIONS");

    /// <summary>
    /// CHARACTER_NAME_MANAGER/AGENTS: one entry (#0 = agent subtype) per subtype that had its own
    /// agent_subtypes.names_group when the startpos was built. The names group itself is not kept
    /// (each allocator's U32 differs even between subtypes sharing a group); the game reads it from agent_subtypes.
    /// </summary>
    public IEnumerable<EsfRecord> NamedAgentSubtypes =>
        Model.Record("CHARACTER_NAME_MANAGER")?.Record("AGENTS")?.Elements() ?? [];

    /// <summary>CAI_INTERFACE/CAI_WORLD/CAI_WORLD_FACTIONS: per faction, its CAI_FACTION (#6 = faction CQI).</summary>
    public IEnumerable<EsfRecord> CaiFactions =>
        Model.Record("CAI_INTERFACE")?.Record("CAI_WORLD")?.Record("CAI_WORLD_FACTIONS")?.Elements() ?? [];
}
