namespace StartPos.Cli;

/// <summary>Minimal "--name value" option parsing; everything else is positional.</summary>
internal sealed class Options
{
    public List<string> Positional { get; } = [];
    private readonly Dictionary<string, string> _named = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public static Options Parse(string[] args, IReadOnlyCollection<string> valued, IReadOnlyCollection<string>? flags = null)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                o.Positional.Add(args[i]);
                continue;
            }
            string name = args[i][2..];
            if (flags?.Contains(name) == true)
                o._flags.Add(name);
            else if (valued.Contains(name) && i + 1 < args.Length)
                o._named[name] = args[++i];
            else
                throw new ArgumentException($"Unknown option or missing value: {args[i]}");
        }
        return o;
    }

    public string? Get(string name) => _named.GetValueOrDefault(name);
    public bool Has(string flag) => _flags.Contains(flag);

    public int? GetInt(string name) =>
        Get(name) is { } s ? int.TryParse(s, out int v) ? v : throw new ArgumentException($"--{name} needs a number, got '{s}'.") : null;
}
