using StartPos.Core.Esf;

namespace StartPos.Cli;

/// <summary>Commands for looking inside ESF files (used to map startpos records to tables).</summary>
internal static class EsfCommands
{

    public static int EsfNamesCommand(string[] args)
    {
        var opts = Options.Parse(args, ["path"]);
        if (opts.Positional.Count != 1)
            return Fail("esf-names takes an .esf file.");
        var doc = EsfDocument.Load(opts.Positional[0]);
        EsfRecord root = doc.Root;
        if (opts.Get("path") is { } path)
            root = root.Path(path) ?? throw new ArgumentException($"No record at path {path}.");

        // Record name -> (count, shortest path it was seen at).
        var names = new Dictionary<string, (int Count, string Path)>(StringComparer.Ordinal);
        var stack = new Stack<(EsfRecord, string)>();
        stack.Push((root, root.Name));
        while (stack.Count > 0)
        {
            var (record, at) = stack.Pop();
            var seen = names.TryGetValue(record.Name, out var s) ? s : (Count: 0, Path: at);
            names[record.Name] = (seen.Count + 1, seen.Path.Length <= at.Length ? seen.Path : at);
            foreach (var block in record.Blocks)
                foreach (var child in block.OfType<EsfRecord>())
                    stack.Push((child, $"{at}/{child.Name}"));
        }
        foreach (var (name, (count, at)) in names.OrderBy(p => p.Value.Path, StringComparer.Ordinal))
            Console.WriteLine($"{count,8}  {at}");
        return 0;
    }

    public static int EsfStatsCommand(string[] args)
    {
        var opts = Options.Parse(args, ["top"]);
        if (opts.Positional.Count != 2)
            return Fail("esf-stats takes an .esf file and a path.");
        int top = opts.GetInt("top") ?? 8;

        var doc = EsfDocument.Load(opts.Positional[0]);
        var records = doc.Root.Select(opts.Positional[1]).ToList();
        Console.WriteLine($"{records.Count} record(s)");
        var slots = new SortedDictionary<int, Dictionary<string, int>>();
        var kinds = new SortedDictionary<int, HashSet<string>>();
        var childNames = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            int i = 0;
            foreach (var node in record.Children)
            {
                if (node is EsfRecord child)
                {
                    childNames[child.Name] = childNames.GetValueOrDefault(child.Name) + 1;
                    continue;
                }
                var v = (EsfValue)node;
                string text = v.IsArray ? $"[{string.Join(",", ((System.Collections.IList)v.Value).Cast<object>().Take(6).Select(o => Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture)))}{(((System.Collections.IList)v.Value).Count > 6 ? ",..." : "")}]" : v.AsString();
                var counter = slots.TryGetValue(i, out var c) ? c : slots[i] = new Dictionary<string, int>(StringComparer.Ordinal);
                counter[text] = counter.GetValueOrDefault(text) + 1;
                (kinds.TryGetValue(i, out var k) ? k : kinds[i] = []).Add(v.Kind.ToString());
                i++;
            }
        }
        foreach (var (i, counter) in slots)
        {
            var common = counter.OrderByDescending(p => p.Value).Take(top).Select(p => $"{Trim(p.Key)} ×{p.Value}");
            Console.WriteLine($"#{i} {string.Join("/", kinds[i])}: {counter.Count} distinct; {string.Join(", ", common)}");
        }
        if (childNames.Count > 0)
            Console.WriteLine("child records: " + string.Join(", ", childNames.Select(p => $"{p.Key} ×{p.Value}")));
        return 0;

        static string Trim(string s) => s.Length > 60 ? s[..60] + "…" : s;
    }

    public static int EsfFindCommand(string[] args)
    {
        var opts = Options.Parse(args, ["path", "max"], ["contains"]);
        if (opts.Positional.Count < 2)
            return Fail("esf-find takes an .esf file and at least one value.");

        var doc = EsfDocument.Load(opts.Positional[0]);
        EsfRecord root = doc.Root;
        if (opts.Get("path") is { } path)
            root = root.Path(path) ?? throw new ArgumentException($"No record at path {path}.");

        var wanted = opts.Positional.Skip(1).ToList();
        bool contains = opts.Has("contains");
        int max = opts.GetInt("max") ?? 50;
        var counts = wanted.ToDictionary(w => w, _ => 0);
        foreach (var hit in EsfSearch.Find(root, s => wanted.Any(w => contains ? s.Contains(w, StringComparison.Ordinal) : s == w)))
        {
            string text = hit.ArrayIndex >= 0 ? ((System.Collections.IList)hit.Value.Value)[hit.ArrayIndex]?.ToString() ?? "" : hit.Value.AsString();
            string key = wanted.First(w => contains ? text.Contains(w, StringComparison.Ordinal) : text == w);
            if (counts[key]++ < max)
                Console.WriteLine($"{hit.Path}{(hit.ArrayIndex >= 0 ? $"[{hit.ArrayIndex}]" : "")} {hit.Value.Kind} (0x{hit.Value.Marker:x2} @0x{hit.Value.Offset:x}) = {text}");
        }
        foreach (var (w, n) in counts)
            Console.WriteLine($"-- {w}: {n} hit(s)");
        return 0;
    }

    public static int EsfDumpCommand(string[] args)
    {
        var opts = Options.Parse(args, ["path", "depth", "elements", "out"], ["outer"]);
        if (opts.Positional.Count != 1)
            return Fail("esf-dump takes one .esf file.");

        var doc = EsfDocument.Load(opts.Positional[0], decompress: !opts.Has("outer"));
        EsfRecord root = doc.Root;
        if (opts.Get("path") is { } path)
            root = root.Path(path) ?? throw new ArgumentException($"No record at path {path}.");

        var dump = new EsfDump
        {
            MaxDepth = opts.GetInt("depth") ?? int.MaxValue,
            MaxElements = opts.GetInt("elements") ?? int.MaxValue,
        };
        using var output = opts.Get("out") is { } outPath ? new StreamWriter(outPath) : Console.Out;
        output.WriteLine($"{doc.Signature}, {doc.Length} bytes{(doc.WasCompressed ? " (decompressed)" : "")}");
        dump.Write(root, output);
        return 0;
    }

    private static int Fail(string message) => Program.Fail(message);
}
