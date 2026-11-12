namespace StartPos.Core.Tables;

/// <summary>A set of DB tables by name: what a start_pos pack holds (start_pos_* and a few helpers).</summary>
public sealed class StartPosTables
{
    private readonly SortedDictionary<string, DbTable> _tables = new(StringComparer.Ordinal);

    public IEnumerable<DbTable> All => _tables.Values;

    /// <summary>The table with this name (without "_tables"), created empty if missing.</summary>
    public DbTable this[string name] => _tables.TryGetValue(name, out var t) ? t : _tables[name] = new DbTable(name);

    public DbTable? Find(string name) => _tables.GetValueOrDefault(name);

    public int RowCount => _tables.Values.Sum(t => t.Rows.Count);
}

/// <summary>The WH3 start_pos table names.</summary>
public static class StartPosTableNames
{
    public const string Calendars = "start_pos_calendars";
    public const string CharacterAncillaries = "start_pos_character_ancillaries";
    public const string CharacterToSettlements = "start_pos_character_to_settlements";
    public const string CharacterTraits = "start_pos_character_traits";
    public const string Characters = "start_pos_characters";
    public const string Diplomacy = "start_pos_diplomacy";
    public const string EntityAssociationFactionRegions = "start_pos_entity_association_faction_regions";
    public const string Factions = "start_pos_factions";
    public const string HordeDetails = "start_pos_horde_details";
    public const string LandUnits = "start_pos_land_units";
    public const string PastEvents = "start_pos_past_events";
    public const string RegionForeignSlots = "start_pos_region_foreign_slots";
    public const string RegionSlotTemplates = "start_pos_region_slot_templates";
    public const string Regions = "start_pos_regions";
    public const string Settlements = "start_pos_settlements";
    public const string StartingGeneralOptions = "start_pos_starting_general_options";
    public const string Technologies = "start_pos_technologies";
    public const string VictoryConditions = "start_pos_victory_conditions";

    // Not start_pos tables, but a build bakes them into the startpos (see StartPosDecompiler.Baked).
    public const string FactionMercenarySets = "faction_to_mercenary_set_junctions";
    public const string AgentCultureDetails = "agent_culture_details";
    public const string AgentSubtypes = "agent_subtypes";
}

/// <summary>
/// Hands out the numeric IDs that the start_pos tables need but a built startpos does not keep
/// (settlement ids, slot template ids, unit ids, ...). IDs are derived from a stable name, so
/// decompiling the same startpos twice gives the same IDs; collisions are resolved by probing.
/// </summary>
public sealed class IdAllocator
{
    private readonly HashSet<long> _used = [];

    /// <summary>Marks an ID that came from the startpos itself as taken.</summary>
    public void Reserve(long id) => _used.Add(id);

    /// <summary>A positive 31-bit ID for <paramref name="name"/>, unique among the IDs handed out or reserved.</summary>
    public long For(string name)
    {
        uint hash = 2166136261;
        foreach (char c in name)
        {
            hash ^= c;
            hash *= 16777619;
        }
        long id = (hash & 0x7FFF_FFFF) | 1;
        while (!_used.Add(id))
            id = id >= 0x7FFF_FFFF ? 1 : id + 1;
        return id;
    }
}
