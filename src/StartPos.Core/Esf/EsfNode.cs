using System.Numerics;

namespace StartPos.Core.Esf;

/// <summary>
/// A node of an ESF tree: either a <see cref="EsfRecord"/> (a named container) or an
/// <see cref="EsfValue"/> (a primitive or a primitive array).
/// </summary>
public abstract class EsfNode
{
    /// <summary>Offset of the node's type marker in the (decompressed) ESF data.</summary>
    public int Offset { get; }

    protected EsfNode(int offset) => Offset = offset;
}

/// <summary>The kinds of primitive ESF values, after folding the optimized encodings together.</summary>
public enum EsfValueKind
{
    Bool, I8, I16, I32, I64, U8, U16, U32, U64, F32, F64, Coord2d, Coord3d, Utf16, Ascii, Angle,
    Unknown21, Unknown23, Unknown24, Unknown25, Unknown26,
    BoolArray, I8Array, I16Array, I32Array, I64Array, U8Array, U16Array, U32Array, U64Array, F32Array, F64Array,
    Coord2dArray, Coord3dArray, Utf16Array, AsciiArray, AngleArray,
}

/// <summary>
/// A primitive ESF node. <see cref="Value"/> holds the decoded value: bool, sbyte, short, int,
/// long, byte, ushort, uint, ulong, float, double, <see cref="Vector2"/>, <see cref="Vector3"/>,
/// string, or an array of one of those (byte[] for U8 and I8 arrays and Unknown26).
/// </summary>
public sealed class EsfValue : EsfNode
{
    public EsfValueKind Kind { get; }

    /// <summary>The raw type marker, which tells the optimized encodings apart (e.g. 0x16 is a u32 stored in one byte).</summary>
    public byte Marker { get; }

    public object Value { get; }

    internal EsfValue(int offset, byte marker, EsfValueKind kind, object value) : base(offset)
    {
        Marker = marker;
        Kind = kind;
        Value = value;
    }

    public bool IsArray => Kind >= EsfValueKind.BoolArray;

    /// <summary>The value as a string: strings as they are, numbers in invariant culture.</summary>
    public string AsString() => Value switch
    {
        string s => s,
        bool b => b ? "true" : "false",
        float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        Vector2 v => $"({Fmt(v.X)}, {Fmt(v.Y)})",
        Vector3 v => $"({Fmt(v.X)}, {Fmt(v.Y)}, {Fmt(v.Z)})",
        IFormattable n => n.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => Value.ToString() ?? "",
    };

    /// <summary>Integer values of any width, as a long. Throws for anything else.</summary>
    public long AsInteger() => Value switch
    {
        sbyte v => v, short v => v, int v => v, long v => v,
        byte v => v, ushort v => v, uint v => v, ulong v => checked((long)v),
        bool v => v ? 1 : 0,
        _ => throw new InvalidOperationException($"ESF value at 0x{Offset:x} is {Kind}, not an integer."),
    };

    public double AsDouble() => Value switch
    {
        float v => v, double v => v,
        _ => AsInteger(),
    };

    public bool AsBool() => Value switch
    {
        bool v => v,
        _ => AsInteger() != 0,
    };

    public override string ToString() => $"{Kind} {(IsArray ? $"[{((Array)Value).Length}]" : AsString())}";

    private static string Fmt(float f) => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// A record: a named, versioned container of child nodes. Children come in blocks. A record
/// without nested blocks has exactly one block; a record with nested blocks (an "array" record,
/// such as a list of factions) has one block per element.
///
/// Children are decoded on first access, so walking to one part of a large ESF does not decode
/// the rest of it.
/// </summary>
public sealed class EsfRecord : EsfNode
{
    private readonly EsfDocument _document;
    private readonly int _contentStart;
    private readonly int _contentEnd;
    private readonly int _blockCount;
    private IReadOnlyList<IReadOnlyList<EsfNode>>? _blocks;

    public string Name { get; }
    public byte Version { get; }
    public EsfRecordFlags Flags { get; }
    public bool HasNestedBlocks => (Flags & EsfRecordFlags.HasNestedBlocks) != 0;

    /// <summary>Number of blocks, known without decoding them.</summary>
    public int BlockCount => _blockCount;

    /// <summary>Size in bytes of the encoded children.</summary>
    public int ContentSize => _contentEnd - _contentStart;

    internal EsfRecord(EsfDocument document, int offset, string name, byte version, EsfRecordFlags flags,
        int contentStart, int contentEnd, int blockCount) : base(offset)
    {
        _document = document;
        Name = name;
        Version = version;
        Flags = flags;
        _contentStart = contentStart;
        _contentEnd = contentEnd;
        _blockCount = blockCount;
    }

    public IReadOnlyList<IReadOnlyList<EsfNode>> Blocks => _blocks ??= _document.ReadBlocks(this, _contentStart, _contentEnd, _blockCount);

    /// <summary>The children of a record without nested blocks (the first block otherwise).</summary>
    public IReadOnlyList<EsfNode> Children => Blocks.Count > 0 ? Blocks[0] : [];

    /// <summary>The primitive children of the first block, in order.</summary>
    public IReadOnlyList<EsfValue> Values => Children.OfType<EsfValue>().ToList();

    /// <summary>The record children of the first block, in order.</summary>
    public IEnumerable<EsfRecord> Records() => Children.OfType<EsfRecord>();

    /// <summary>Record children of the first block with the given name.</summary>
    public IEnumerable<EsfRecord> Records(string name) => Records().Where(r => r.Name == name);

    /// <summary>The first record child of the first block with the given name, or null.</summary>
    public EsfRecord? Record(string name) => Records().FirstOrDefault(r => r.Name == name);

    /// <summary>The first record child with the given name; throws if there is none.</summary>
    public EsfRecord Required(string name) =>
        Record(name) ?? throw new EsfFormatException($"{Name} at 0x{Offset:x} has no {name} record.");

    /// <summary>
    /// Follows a '/'-separated path of record names. A plain step takes the first child record
    /// with that name, "NAME:n" the n-th one, and "NAME[n]" the n-th element of array record NAME.
    /// </summary>
    public EsfRecord? Path(string path)
    {
        EsfRecord? current = this;
        foreach (string step in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Step(current, step);
            if (current == null)
                return null;
        }
        return current;
    }

    /// <summary>
    /// Like <see cref="Path"/>, with wildcards: "NAME[*]" steps into every element of array
    /// record NAME and "NAME:*" into every child record named NAME.
    /// </summary>
    public IEnumerable<EsfRecord> Select(string path)
    {
        IEnumerable<EsfRecord> current = [this];
        foreach (string step in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (step.EndsWith("[*]", StringComparison.Ordinal))
                current = current.SelectMany(r => r.Record(step[..^3])?.Elements() ?? []);
            else if (step.EndsWith(":*", StringComparison.Ordinal))
                current = current.SelectMany(r => r.Records(step[..^2]));
            else
                current = current.Select(r => Step(r, step)).OfType<EsfRecord>();
        }
        return current;
    }

    private static EsfRecord? Step(EsfRecord record, string step)
    {
        int bracket = step.IndexOf('[');
        if (bracket > 0 && step.EndsWith(']') && int.TryParse(step[(bracket + 1)..^1], out int element))
            return record.Record(step[..bracket])?.Elements().ElementAtOrDefault(element);
        int colon = step.IndexOf(':');
        if (colon > 0 && int.TryParse(step[(colon + 1)..], out int nth))
            return record.Records(step[..colon]).ElementAtOrDefault(nth);
        return record.Record(step);
    }

    /// <summary>
    /// For an array record (nested blocks), the elements. A block holding exactly one record
    /// is that record; any other block is returned as a view: a record named like this one
    /// whose single block is that block.
    /// </summary>
    public IEnumerable<EsfRecord> Elements() =>
        Blocks.Select(b => b is [EsfRecord only] ? only : new EsfRecord(this, b));

    /// <summary>A view of one block of <paramref name="array"/> as a record.</summary>
    private EsfRecord(EsfRecord array, IReadOnlyList<EsfNode> block) : base(block.Count > 0 ? block[0].Offset : array.Offset)
    {
        _document = array._document;
        Name = array.Name;
        Version = array.Version;
        Flags = array.Flags & ~EsfRecordFlags.HasNestedBlocks;
        _blockCount = 1;
        _blocks = [block];
    }

    public string Str(int index) => Value(index).AsString();
    public long Int(int index) => Value(index).AsInteger();
    public bool Bool(int index) => Value(index).AsBool();

    /// <summary>
    /// The primitive child <paramref name="back"/> places before the first child record named
    /// <paramref name="anchor"/> (1 = the value right before it). For records whose value
    /// layout shifts between instances but keeps fixed neighbours.
    /// </summary>
    public EsfValue? ValueBefore(string anchor, int back)
    {
        var values = new List<EsfValue>();
        foreach (var node in Children)
        {
            if (node is EsfRecord r && r.Name == anchor)
                return back >= 1 && back <= values.Count ? values[^back] : null;
            if (node is EsfValue v)
                values.Add(v);
        }
        return null;
    }

    /// <summary>The primitive child <paramref name="forward"/> places after the first child record named <paramref name="anchor"/>.</summary>
    public EsfValue? ValueAfter(string anchor, int forward)
    {
        bool found = false;
        int seen = 0;
        foreach (var node in Children)
        {
            if (!found)
            {
                found = node is EsfRecord r && r.Name == anchor;
                continue;
            }
            if (node is EsfValue v && ++seen == forward)
                return v;
        }
        return null;
    }

    /// <summary>The i-th primitive child of the first block.</summary>
    public EsfValue Value(int index)
    {
        int seen = 0;
        foreach (var node in Children)
            if (node is EsfValue v && seen++ == index)
                return v;
        throw new EsfFormatException($"{Name} at 0x{Offset:x} has no value #{index}.");
    }

    public override string ToString() => $"{Name} v{Version} ({_blockCount} block{(_blockCount == 1 ? "" : "s")})";
}

[Flags]
public enum EsfRecordFlags : byte
{
    None = 0,
    IsRecord = 0b1000_0000,
    HasNestedBlocks = 0b0100_0000,
    HasNonOptimizedInfo = 0b0010_0000,
}

public sealed class EsfFormatException(string message) : Exception(message);
