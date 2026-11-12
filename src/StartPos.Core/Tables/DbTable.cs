using System.Globalization;
using System.Text.Json.Nodes;

namespace StartPos.Core.Tables;

/// <summary>A DB column as RPFM processes it: name, RPFM field type (e.g. "StringU8", "OptionalI32") and key flag.</summary>
public sealed record FieldDef(string Name, string Type, bool IsKey);

/// <summary>
/// A table definition from RPFM's schema. <see cref="Fields"/> is in RPFM's processed order
/// (colours merged, bitwise columns split), which is the order rows are exchanged in.
/// </summary>
public sealed class TableDefinition(string tableName, int version, IReadOnlyList<FieldDef> fields)
{
    /// <summary>Folder name, e.g. "start_pos_regions_tables".</summary>
    public string TableName { get; } = tableName;
    public int Version { get; } = version;
    public IReadOnlyList<FieldDef> Fields { get; } = fields;

    public IEnumerable<string> KeyColumns => Fields.Where(f => f.IsKey).Select(f => f.Name);

    public FieldDef? Field(string name) => Fields.FirstOrDefault(f => f.Name == name);
}

/// <summary>
/// One row: column name to value. Values are plain .NET values (string, bool, int, long, short,
/// float, double); <see cref="DbValue.Convert"/> turns them into a column's type when written.
/// </summary>
public sealed class DbRow : Dictionary<string, object?>
{
    public DbRow() : base(StringComparer.Ordinal) { }

    public DbRow(IDictionary<string, object?> values) : base(values, StringComparer.Ordinal) { }

    public string Str(string column) => DbValue.ToText(this.GetValueOrDefault(column));
}

/// <summary>Rows of one DB table (all fragments of it, or one fragment).</summary>
public sealed class DbTable(string name)
{
    /// <summary>Table name without the "_tables" suffix, e.g. "start_pos_regions".</summary>
    public string Name { get; } = name;

    public string Folder => Name + "_tables";

    public List<DbRow> Rows { get; } = [];

    public DbRow Add(DbRow row)
    {
        Rows.Add(row);
        return row;
    }

    public override string ToString() => $"{Name} ({Rows.Count} rows)";
}

public static class DbValue
{
    /// <summary>Invariant text form of a value, as RPFM shows it ("true"/"false" for booleans).</summary>
    public static string ToText(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable n => n.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>Converts a value to what a column of RPFM type <paramref name="fieldType"/> holds.</summary>
    public static object Convert(object? value, string fieldType)
    {
        string text = ToText(value);
        switch (fieldType)
        {
            case "Boolean":
                return value is bool b ? b : text is "true" or "1" or "True";
            case "F32":
                return value is float f ? f : text.Length == 0 ? 0f : float.Parse(text, CultureInfo.InvariantCulture);
            case "F64":
                return value is double d ? d : text.Length == 0 ? 0d : double.Parse(text, CultureInfo.InvariantCulture);
            case "I16":
            case "OptionalI16":
                return text.Length == 0 ? (short)0 : short.Parse(text, CultureInfo.InvariantCulture);
            case "I32":
            case "OptionalI32":
                return text.Length == 0 ? 0 : int.Parse(text, CultureInfo.InvariantCulture);
            case "I64":
            case "OptionalI64":
                return text.Length == 0 ? 0L : long.Parse(text, CultureInfo.InvariantCulture);
            default:
                return text;
        }
    }

    /// <summary>The JSON RPFM takes for a value of a column (a boolean, number or string).</summary>
    public static JsonNode ToRpfmValue(object? value, string fieldType) => Convert(value, fieldType) switch
    {
        bool b => JsonValue.Create(b),
        float f => JsonValue.Create(f),
        double d => JsonValue.Create(d),
        short s => JsonValue.Create(s),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        string s => JsonValue.Create(s),
        var v => throw new InvalidOperationException($"Cannot write {v.GetType().Name} as {fieldType}."),
    };

    /// <summary>A row value as RPFM sends it (961.3 in an F32 column → 961.3f).</summary>
    public static object? FromRpfmValue(JsonNode? node, string fieldType)
    {
        if (node is not JsonValue value)
            return node?.ToJsonString();
        if (value.TryGetValue<string>(out var s))
            return s;
        return fieldType switch
        {
            "Boolean" => value.GetValue<bool>(),
            "F32" => value.GetValue<float>(),
            "F64" => value.GetValue<double>(),
            "I16" or "OptionalI16" => value.GetValue<short>(),
            "I32" or "OptionalI32" => value.GetValue<int>(),
            "I64" or "OptionalI64" => value.GetValue<long>(),
            _ => value.ToJsonString(),
        };
    }
}
