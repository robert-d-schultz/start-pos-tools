namespace StartPos.Core.Esf;

/// <summary>Writes an ESF tree as an indented text outline.</summary>
public sealed class EsfDump
{
    /// <summary>Records deeper than this are listed but not expanded.</summary>
    public int MaxDepth { get; init; } = int.MaxValue;

    /// <summary>Blocks of an array record shown before the rest are summarised.</summary>
    public int MaxElements { get; init; } = int.MaxValue;

    /// <summary>Array values show at most this many items.</summary>
    public int MaxArrayItems { get; init; } = 16;

    public void Write(EsfRecord record, TextWriter output) => WriteRecord(record, output, 0, "");

    private void WriteRecord(EsfRecord record, TextWriter output, int depth, string label)
    {
        string indent = new(' ', depth * 2);
        output.WriteLine($"{indent}{label}{record.Name} v{record.Version}" +
                         (record.HasNestedBlocks ? $" [{record.BlockCount}]" : "") + $"  @0x{record.Offset:x} {record.ContentSize}b");
        if (depth >= MaxDepth)
            return;

        var blocks = record.Blocks;
        for (int b = 0; b < blocks.Count; b++)
        {
            if (b == MaxElements)
            {
                output.WriteLine($"{indent}  ... {blocks.Count - b} more");
                break;
            }
            string prefix = record.HasNestedBlocks ? $"[{b}] " : "";
            int valueIndex = 0;
            foreach (var node in blocks[b])
            {
                if (node is EsfRecord child)
                    WriteRecord(child, output, depth + 1, prefix);
                else if (node is EsfValue v)
                    output.WriteLine($"{indent}  {prefix}#{valueIndex++} {Describe(v)}");
            }
        }
    }

    private string Describe(EsfValue v)
    {
        if (!v.IsArray)
            return $"{v.Kind} = {Quote(v)}";
        var items = (System.Collections.IList)v.Value;
        var shown = items.Cast<object>().Take(MaxArrayItems).Select(o => o switch
        {
            string s => $"\"{s}\"",
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => o.ToString(),
        });
        return $"{v.Kind}[{items.Count}] = [{string.Join(", ", shown)}{(items.Count > MaxArrayItems ? ", ..." : "")}]";
    }

    private static string Quote(EsfValue v) => v.Value is string s ? $"\"{s}\"" : v.AsString();
}
