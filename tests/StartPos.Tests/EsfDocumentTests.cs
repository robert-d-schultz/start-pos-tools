using System.Text;
using StartPos.Core.Esf;
using Xunit;

namespace StartPos.Tests;

public class EsfDocumentTests
{
    /// <summary>
    /// A CBAB file: ROOT { U32_24BIT 01 86 A0, ASCII "hello", ARR [ {U32_ONE}, {U32_BYTE 5} ] }.
    /// </summary>
    private static byte[] SampleEsf()
    {
        var arrayBlocks = new List<byte>();
        arrayBlocks.AddRange([1, 0x15]);             // block 0: size 1, U32_ONE
        arrayBlocks.AddRange([2, 0x16, 5]);          // block 1: size 2, U32_BYTE 5
        var array = new List<byte> { 0xC0, 0x01 };   // record, nested blocks, optimized header: version 0, name #1
        array.Add((byte)arrayBlocks.Count);          // block size, counted from after the block count
        array.Add(2);                                 // block count
        array.AddRange(arrayBlocks);

        var rootChildren = new List<byte> { 0x18, 0x01, 0x86, 0xA0, 0x0F, 0, 0, 0, 0 };
        rootChildren.AddRange(array);
        var root = new List<byte> { 0x80, 0, 0, 1, (byte)rootChildren.Count };   // root: name #0, version 1
        root.AddRange(rootChildren);

        var tables = new List<byte>();
        tables.AddRange(BitConverter.GetBytes((ushort)2));
        foreach (string name in new[] { "ROOT", "ARR" })
        {
            tables.AddRange(BitConverter.GetBytes((ushort)name.Length));
            tables.AddRange(Encoding.UTF8.GetBytes(name));
        }
        tables.AddRange(BitConverter.GetBytes(0u));                // no UTF-16 strings
        tables.AddRange(BitConverter.GetBytes(1u));                // one ASCII string
        tables.AddRange(BitConverter.GetBytes(5u));
        tables.AddRange(Encoding.UTF8.GetBytes("hello"));
        tables.AddRange(BitConverter.GetBytes(0u));                // its index

        var file = new List<byte>(EsfDocument.SignatureCbab);
        file.AddRange(BitConverter.GetBytes(0u));
        file.AddRange(BitConverter.GetBytes(0u));
        file.AddRange(BitConverter.GetBytes((uint)(16 + root.Count)));
        file.AddRange(root);
        file.AddRange(tables);
        return file.ToArray();
    }

    [Fact]
    public void ReadsRecordsValuesAndStrings()
    {
        var doc = EsfDocument.Parse(SampleEsf());

        Assert.Equal("CBAB", doc.Signature);
        Assert.Equal("ROOT", doc.Root.Name);
        Assert.Equal(1, doc.Root.Version);
        Assert.Equal("hello", doc.Root.Str(1));
    }

    [Fact]
    public void TwentyFourBitValuesAreBigEndian()
    {
        // 01 86 A0 is 100000; read little-endian (as RPFM does) it would be 10520065.
        Assert.Equal(100000L, EsfDocument.Parse(SampleEsf()).Root.Int(0));
    }

    [Fact]
    public void ArrayRecordsHaveOneElementPerBlock()
    {
        var array = EsfDocument.Parse(SampleEsf()).Root.Required("ARR");

        Assert.True(array.HasNestedBlocks);
        Assert.Equal(2, array.BlockCount);
        Assert.Equal([1L, 5L], array.Elements().Select(e => e.Int(0)));
    }

    [Fact]
    public void PathsAndWildcardsSelectRecords()
    {
        var root = EsfDocument.Parse(SampleEsf()).Root;

        Assert.Equal(5L, root.Path("ARR[1]")!.Int(0));
        Assert.Null(root.Path("ARR[2]"));
        Assert.Equal(2, root.Select("ARR[*]").Count());
    }

    [Fact]
    public void FindReportsPaths()
    {
        var hits = EsfSearch.Find(EsfDocument.Parse(SampleEsf()).Root, s => s == "5").ToList();

        Assert.Equal("ROOT/ARR[1]#0", Assert.Single(hits).Path);
    }

    [Fact]
    public void RejectsOtherFiles()
    {
        Assert.Throws<EsfFormatException>(() => EsfDocument.Parse(new byte[32]));
    }
}
