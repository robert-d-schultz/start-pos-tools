using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace StartPos.Core.Esf;

/// <summary>
/// A decoded ESF file (CAAB or CBAB), read lazily: the header and string tables are read up
/// front, records decode their children on first access.
///
/// This is a port of RPFM's ESF decoder (rpfm_lib/src/files/esf). Like RPFM, when the root's
/// last child is a COMPRESSED_DATA record (startpos files keep the whole tree LZMA1-compressed
/// there), the decompressed ESF replaces the outer one.
/// </summary>
public sealed class EsfDocument
{
    public static readonly byte[] SignatureCaab = [0xCA, 0xAB, 0x00, 0x00];
    public static readonly byte[] SignatureCbab = [0xCB, 0xAB, 0x00, 0x00];

    private const string CompressedDataTag = "COMPRESSED_DATA";
    private const string CompressedDataInfoTag = "COMPRESSED_DATA_INFO";

    private readonly byte[] _data;
    private readonly bool _cbab;
    private string[] _recordNames = [];
    private Dictionary<uint, string> _utf16 = [];
    private Dictionary<uint, string> _ascii = [];

    public string Signature => _cbab ? "CBAB" : "CAAB";
    public uint Unknown1 { get; private set; }
    public uint CreationDate { get; private set; }
    public EsfRecord Root { get; private set; } = null!;

    /// <summary>True when the tree came out of a COMPRESSED_DATA record.</summary>
    public bool WasCompressed { get; private set; }

    /// <summary>Size of the (decompressed) ESF data the tree is read from.</summary>
    public int Length => _data.Length;

    private EsfDocument(byte[] data, bool cbab)
    {
        _data = data;
        _cbab = cbab;
    }

    public static EsfDocument Load(string path, bool decompress = true) => Parse(File.ReadAllBytes(path), decompress);

    /// <param name="decompress">
    /// False keeps the outer tree of a compressed ESF: the records the game reads without
    /// unpacking the rest (for a startpos, the header and the campaign's pre-open info).
    /// </param>
    public static EsfDocument Parse(byte[] data, bool decompress = true)
    {
        if (data.Length < 16)
            throw new EsfFormatException("Not an ESF file: too short.");
        bool cbab;
        if (data.AsSpan(0, 4).SequenceEqual(SignatureCbab))
            cbab = true;
        else if (data.AsSpan(0, 4).SequenceEqual(SignatureCaab))
            cbab = false;
        else
            throw new EsfFormatException($"Unsupported ESF signature {data[0]:X2}{data[1]:X2}{data[2]:X2}{data[3]:X2} (only CAAB and CBAB are supported).");

        var doc = new EsfDocument(data, cbab);
        doc.ReadHeaderAndTables();

        var compressed = doc.Root.Children.LastOrDefault() as EsfRecord;
        if (decompress && compressed?.Name == CompressedDataTag)
        {
            var inner = Parse(Decompress(compressed));
            inner.WasCompressed = true;
            return inner;
        }
        return doc;
    }

    private void ReadHeaderAndTables()
    {
        var r = new Cursor(_data, 4);
        Unknown1 = r.U32();
        CreationDate = r.U32();
        int tablesOffset = checked((int)r.U32());
        int nodesOffset = r.Position;

        r.Position = tablesOffset;
        int recordNameCount = r.U16();
        _recordNames = new string[recordNameCount];
        for (int i = 0; i < recordNameCount; i++)
            _recordNames[i] = r.Utf8(r.U16());

        uint utf16Count = r.U32();
        _utf16 = new Dictionary<uint, string>((int)utf16Count);
        for (uint i = 0; i < utf16Count; i++)
        {
            string s = r.Utf16(_cbab ? r.U32() : r.U16());
            _utf16[r.U32()] = s;
        }

        uint asciiCount = r.U32();
        _ascii = new Dictionary<uint, string>((int)asciiCount);
        for (uint i = 0; i < asciiCount; i++)
        {
            string s = r.Utf8(_cbab ? r.U32() : r.U16());
            _ascii[r.U32()] = s;
        }

        if (r.Position != _data.Length)
            throw new EsfFormatException($"ESF string tables end at 0x{r.Position:x}, file ends at 0x{_data.Length:x}.");

        r.Position = nodesOffset;
        Root = ReadRecordHeader(ref r, isRoot: true);
        if (r.Position != tablesOffset)
            throw new EsfFormatException($"ESF node tree ends at 0x{r.Position:x}, string tables start at 0x{tablesOffset:x}.");
    }

    /// <summary>COMPRESSED_DATA holds [u8 array data, COMPRESSED_DATA_INFO [u32 size, u8 array LZMA properties]].</summary>
    private static byte[] Decompress(EsfRecord compressed)
    {
        var children = compressed.Children;
        if (children.Count < 2 || children[0] is not EsfValue { Value: byte[] packed } ||
            children[1] is not EsfRecord { Name: CompressedDataInfoTag } info ||
            info.Children.Count < 2 || info.Children[0] is not EsfValue sizeNode ||
            info.Children[1] is not EsfValue { Value: byte[] properties })
            throw new EsfFormatException("COMPRESSED_DATA record has an unexpected layout.");

        long size = sizeNode.AsInteger();
        var decoder = new SevenZip.Compression.LZMA.Decoder();
        decoder.SetDecoderProperties(properties);
        using var input = new MemoryStream(packed, writable: false);
        var output = new MemoryStream(checked((int)size));
        decoder.Code(input, output, packed.Length, size, null);
        if (output.Length != size)
            throw new EsfFormatException($"COMPRESSED_DATA decompressed to {output.Length} bytes, expected {size}.");
        return output.GetBuffer().Length == size ? output.GetBuffer() : output.ToArray();
    }

    /// <summary>Reads a record's header and skips over its content, leaving the cursor after it.</summary>
    private EsfRecord ReadRecordHeader(ref Cursor r, bool isRoot)
    {
        int offset = r.Position;
        byte first = r.U8();
        if ((first & 0x80) == 0)
            throw new EsfFormatException($"Expected a record at 0x{offset:x}, found marker 0x{first:x2}.");

        var flags = (EsfRecordFlags)(first & 0xE0);
        int nameIndex;
        byte version;
        if (isRoot || (flags & EsfRecordFlags.HasNonOptimizedInfo) != 0)
        {
            nameIndex = r.U16();
            version = r.U8();
        }
        else
        {
            version = (byte)((first & 0x1E) >> 1);
            nameIndex = ((first & 1) << 8) + r.U8();
        }

        if (nameIndex >= _recordNames.Length)
            throw new EsfFormatException($"Record at 0x{offset:x} names record #{nameIndex}, the table has {_recordNames.Length}.");

        int blockSize = checked((int)r.CauLeb128());
        int blockCount = (flags & EsfRecordFlags.HasNestedBlocks) != 0 ? checked((int)r.CauLeb128()) : 1;
        int contentStart = r.Position;
        int contentEnd = checked(contentStart + blockSize);
        if (contentEnd > _data.Length)
            throw new EsfFormatException($"Record {_recordNames[nameIndex]} at 0x{offset:x} runs past the end of the data.");
        r.Position = contentEnd;
        return new EsfRecord(this, offset, _recordNames[nameIndex], version, flags, contentStart, contentEnd, blockCount);
    }

    internal IReadOnlyList<IReadOnlyList<EsfNode>> ReadBlocks(EsfRecord record, int start, int end, int blockCount)
    {
        var r = new Cursor(_data, start);
        var blocks = new List<IReadOnlyList<EsfNode>>(blockCount);
        for (int b = 0; b < blockCount; b++)
        {
            int blockEnd = end;
            if (record.HasNestedBlocks)
            {
                int size = checked((int)r.CauLeb128());     // counted from after the size itself
                blockEnd = checked(r.Position + size);
            }
            var nodes = new List<EsfNode>();
            while (r.Position < blockEnd)
                nodes.Add(ReadNode(ref r));
            if (r.Position != blockEnd)
                throw new EsfFormatException($"Block {b} of {record.Name} at 0x{record.Offset:x} ends at 0x{r.Position:x}, expected 0x{blockEnd:x}.");
            blocks.Add(nodes);
        }
        if (r.Position != end)
            throw new EsfFormatException($"{record.Name} at 0x{record.Offset:x} ends at 0x{r.Position:x}, expected 0x{end:x}.");
        return blocks;
    }

    private EsfNode ReadNode(ref Cursor r)
    {
        int offset = r.Position;
        byte marker = _data[offset];
        if ((marker & 0x80) != 0)
            return ReadRecordHeader(ref r, isRoot: false);

        r.Position++;
        (EsfValueKind kind, object value) = marker switch
        {
            0x01 => (EsfValueKind.Bool, (object)(r.U8() != 0)),
            0x02 => (EsfValueKind.I8, (sbyte)r.U8()),
            0x03 => (EsfValueKind.I16, r.I16()),
            0x04 => (EsfValueKind.I32, r.I32()),
            0x05 => (EsfValueKind.I64, r.I64()),
            0x06 => (EsfValueKind.U8, r.U8()),
            0x07 => (EsfValueKind.U16, r.U16()),
            0x08 => (EsfValueKind.U32, r.U32()),
            0x09 => (EsfValueKind.U64, r.U64()),
            0x0a => (EsfValueKind.F32, r.F32()),
            0x0b => (EsfValueKind.F64, r.F64()),
            0x0c => (EsfValueKind.Coord2d, new Vector2(r.F32(), r.F32())),
            0x0d => (EsfValueKind.Coord3d, new Vector3(r.F32(), r.F32(), r.F32())),
            0x0e => (EsfValueKind.Utf16, Lookup(_utf16, r.U32(), offset)),
            0x0f => (EsfValueKind.Ascii, Lookup(_ascii, r.U32(), offset)),
            0x10 => (EsfValueKind.Angle, r.I16()),

            0x12 => (EsfValueKind.Bool, true),
            0x13 => (EsfValueKind.Bool, false),
            0x14 => (EsfValueKind.U32, 0u),
            0x15 => (EsfValueKind.U32, 1u),
            0x16 => (EsfValueKind.U32, (uint)r.U8()),
            0x17 => (EsfValueKind.U32, (uint)r.U16()),
            0x18 => (EsfValueKind.U32, r.U24()),
            0x19 => (EsfValueKind.I32, 0),
            0x1a => (EsfValueKind.I32, (int)(sbyte)r.U8()),
            0x1b => (EsfValueKind.I32, (int)r.I16()),
            0x1c => (EsfValueKind.I32, r.I24()),
            0x1d => (EsfValueKind.F32, 0f),

            0x21 => (EsfValueKind.Unknown21, r.U32()),
            0x23 => (EsfValueKind.Unknown23, r.U8()),
            0x24 => (EsfValueKind.Unknown24, r.U16()),
            0x25 => (EsfValueKind.Unknown25, r.U32()),
            0x26 => (EsfValueKind.Unknown26, ReadUnknown26(ref r)),

            0x41 => (EsfValueKind.BoolArray, Array(ref r, 1, (ref Cursor c) => c.U8() != 0)),
            0x42 => (EsfValueKind.I8Array, r.Bytes(checked((int)r.CauLeb128()))),
            0x43 => (EsfValueKind.I16Array, Array(ref r, 2, (ref Cursor c) => c.I16())),
            0x44 => (EsfValueKind.I32Array, Array(ref r, 4, (ref Cursor c) => c.I32())),
            0x45 => (EsfValueKind.I64Array, Array(ref r, 8, (ref Cursor c) => c.I64())),
            0x46 => (EsfValueKind.U8Array, r.Bytes(checked((int)r.CauLeb128()))),
            0x47 => (EsfValueKind.U16Array, Array(ref r, 2, (ref Cursor c) => c.U16())),
            0x48 => (EsfValueKind.U32Array, Array(ref r, 4, (ref Cursor c) => c.U32())),
            0x49 => (EsfValueKind.U64Array, Array(ref r, 8, (ref Cursor c) => c.U64())),
            0x4a => (EsfValueKind.F32Array, Array(ref r, 4, (ref Cursor c) => c.F32())),
            0x4b => (EsfValueKind.F64Array, Array(ref r, 8, (ref Cursor c) => c.F64())),
            0x4c => (EsfValueKind.Coord2dArray, Array(ref r, 8, (ref Cursor c) => new Vector2(c.F32(), c.F32()))),
            0x4d => (EsfValueKind.Coord3dArray, Array(ref r, 12, (ref Cursor c) => new Vector3(c.F32(), c.F32(), c.F32()))),
            0x4e => (EsfValueKind.Utf16Array, StringArray(ref r, _utf16, offset)),
            0x4f => (EsfValueKind.AsciiArray, StringArray(ref r, _ascii, offset)),
            0x50 => (EsfValueKind.AngleArray, Array(ref r, 2, (ref Cursor c) => c.I16())),

            0x56 => (EsfValueKind.U32Array, Array(ref r, 1, (ref Cursor c) => (uint)c.U8())),
            0x57 => (EsfValueKind.U32Array, Array(ref r, 2, (ref Cursor c) => (uint)c.U16())),
            0x58 => (EsfValueKind.U32Array, Array(ref r, 3, (ref Cursor c) => c.U24())),
            0x5a => (EsfValueKind.I32Array, Array(ref r, 1, (ref Cursor c) => (int)(sbyte)c.U8())),
            0x5b => (EsfValueKind.I32Array, Array(ref r, 2, (ref Cursor c) => (int)c.I16())),
            0x5c => (EsfValueKind.I32Array, Array(ref r, 3, (ref Cursor c) => c.I24())),

            _ => throw new EsfFormatException($"Unsupported ESF node type 0x{marker:x2} at 0x{offset:x}."),
        };
        return new EsfValue(offset, marker, kind, value);
    }

    private delegate T ElementReader<T>(ref Cursor c);

    /// <summary>Arrays carry their size in bytes; elements are read until that many bytes are used.</summary>
    private static T[] Array<T>(ref Cursor r, int elementSize, ElementReader<T> read)
    {
        int size = checked((int)r.CauLeb128());
        int end = r.Position + size;
        var items = new T[size / elementSize];
        for (int i = 0; i < items.Length; i++)
            items[i] = read(ref r);
        if (r.Position != end)
            throw new EsfFormatException($"Array at 0x{r.Position:x} is {size} bytes, not a multiple of {elementSize}.");
        return items;
    }

    private static string[] StringArray(ref Cursor r, Dictionary<uint, string> table, int offset)
    {
        var indices = Array(ref r, 4, (ref Cursor c) => c.U32());
        return indices.Select(i => Lookup(table, i, offset)).ToArray();
    }

    private static string Lookup(Dictionary<uint, string> table, uint index, int offset) =>
        table.TryGetValue(index, out var s) ? s : throw new EsfFormatException($"String #{index} at 0x{offset:x} is not in the string table.");

    /// <summary>Three Kingdoms' 0x26 type, decoded the way RPFM does.</summary>
    private byte[] ReadUnknown26(ref Cursor r)
    {
        byte first = r.U8();
        int length = first % 8 == 0 && first != 0 ? first : 7;
        var bytes = new byte[length + 1];
        bytes[0] = first;
        r.Bytes(length).CopyTo(bytes, 1);
        if (r.Position < _data.Length && _data[r.Position] == 0x9C)
            r.Position++;
        return bytes;
    }

    /// <summary>Little-endian reader over the ESF bytes.</summary>
    private struct Cursor(byte[] data, int position)
    {
        private readonly byte[] _data = data;
        public int Position = position;

        private ReadOnlySpan<byte> Take(int n)
        {
            if (Position + n > _data.Length)
                throw new EsfFormatException($"Unexpected end of ESF data at 0x{Position:x}.");
            var span = _data.AsSpan(Position, n);
            Position += n;
            return span;
        }

        public byte U8() => Take(1)[0];
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public short I16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
        // 24-bit values are big-endian, unlike everything else. RPFM reads them little-endian,
        // which turns e.g. a region's population of 100000 (01 86 A0) into 10520065.
        public uint U24() { var s = Take(3); return (uint)(s[0] << 16 | s[1] << 8 | s[2]); }
        public int I24() { var s = Take(3); return (s[0] << 16 | s[1] << 8 | s[2]) << 8 >> 8; }
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
        public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
        public byte[] Bytes(int n) => Take(n).ToArray();
        public string Utf8(uint n) => Encoding.UTF8.GetString(Take(checked((int)n)));
        public string Utf16(uint chars) => Encoding.Unicode.GetString(Take(checked((int)chars * 2)));

        /// <summary>CA's big-endian LEB128: 7 bits per byte, most significant group first, high bit = more.</summary>
        public uint CauLeb128()
        {
            uint value = 0;
            byte b = U8();
            while ((b & 0x80) != 0)
            {
                value = (value << 7) | (uint)(b & 0x7f);
                b = U8();
            }
            return (value << 7) | (uint)(b & 0x7f);
        }
    }
}
