namespace StartPos.Core.Esf;

/// <summary>Finds values in an ESF tree and reports where they are.</summary>
public static class EsfSearch
{
    public sealed record Hit(string Path, EsfValue Value, int ArrayIndex);

    /// <summary>
    /// Every value (or array item) whose string form matches <paramref name="match"/>.
    /// Paths read like WORLD/FACTION_ARRAY[3]/FACTION/CHARACTER_ARRAY[0]/CHARACTER#5, where
    /// [n] is the block of an array record and #n the value's index among its block's values.
    /// </summary>
    public static IEnumerable<Hit> Find(EsfRecord root, Func<string, bool> match)
    {
        var stack = new Stack<(EsfRecord Record, string Path)>();
        stack.Push((root, root.Name));
        while (stack.Count > 0)
        {
            var (record, path) = stack.Pop();
            var blocks = record.Blocks;
            var children = new List<(EsfRecord, string)>();
            for (int b = 0; b < blocks.Count; b++)
            {
                string blockPath = record.HasNestedBlocks ? $"{path}[{b}]" : path;
                int valueIndex = 0;
                var seen = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var node in blocks[b])
                {
                    if (node is EsfRecord child)
                    {
                        int nth = seen[child.Name] = seen.GetValueOrDefault(child.Name, -1) + 1;
                        children.Add((child, $"{blockPath}/{child.Name}{(nth > 0 ? $":{nth}" : "")}"));
                        continue;
                    }
                    var value = (EsfValue)node;
                    int index = valueIndex++;
                    if (value.Value is System.Collections.IList list && value.Value is not byte[])
                    {
                        for (int i = 0; i < list.Count; i++)
                            if (match(Format(list[i])))
                                yield return new Hit($"{blockPath}#{index}", value, i);
                    }
                    else if (value.Value is not byte[] && match(value.AsString()))
                    {
                        yield return new Hit($"{blockPath}#{index}", value, -1);
                    }
                }
            }
            for (int i = children.Count - 1; i >= 0; i--)
                stack.Push(children[i]);
        }
    }

    private static string Format(object? o) => o switch
    {
        null => "",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => o.ToString() ?? "",
    };
}
