using StartPos.Core;
using StartPos.Core.Esf;
using StartPos.Core.Rpfm;

namespace StartPos.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            return args[0] switch
            {
                "decompile" => await PackCommands.DecompileCommand(args[1..]),
                "diff" => await PackCommands.DiffCommand(args[1..]),
                "table" => await PackCommands.TableCommand(args[1..]),
                "config" => ConfigCommand(args[1..]),
                "esf-dump" => EsfCommands.EsfDumpCommand(args[1..]),
                "esf-find" => EsfCommands.EsfFindCommand(args[1..]),
                "esf-stats" => EsfCommands.EsfStatsCommand(args[1..]),
                "esf-names" => EsfCommands.EsfNamesCommand(args[1..]),
                _ => Fail($"Unknown command '{args[0]}'.\n\n{Usage}"),
            };
        }
        catch (Exception ex) when (ex is EsfFormatException or IOException or ArgumentException or InvalidOperationException
                                       or RpfmException or TimeoutException or System.Net.WebSockets.WebSocketException)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 2;
        }
    }

    private const string Usage = """
        startpos <command> [options]

        Start pos tools for Total War: WARHAMMER III. Packs are read and written through the
        RPFM 5.3 server (rpfm_server.exe), which is started when it is not running.

        Commands:
          decompile <input.pack|startpos.esf> [--out FILE] [--campaign KEY] [--name NAME]
              Turns a built startpos.esf back into a .pack of start_pos tables. The input is a
              pack holding campaigns/<folder>/startpos.esf, or the .esf itself; a startpos is
              picked by the campaign recorded in it, whatever folder it is in. The tables go
              into db/<table>_tables/!!!<NAME>_{campaign,vanilla_factions,new_factions,
              vanilla_regions,new_regions} (NAME: the campaign key by default). Mercenary sets,
              agent types and subtype names groups baked into the startpos are checked against
              the tables; rows the startpos needs and the tables lack are written (partly
              borrowed or guessed where the startpos doesn't keep a column).
          diff <a> <b> [--decompile] [--decompile-right] [--campaign KEY] [--left-campaign KEY]
               [--right-campaign KEY] [--ignore-campaign] [--everything] [--out FILE] [--examples N]
               [--rows N] [--json FILE] [--no-game]
              Compares the start_pos tables of two inputs. A startpos.esf (or with --decompile /
              --decompile-right, a pack's startpos.esf) is decompiled first; a pack is otherwise
              read for its start_pos tables. Only what a built startpos keeps is compared:
              tables as a build would leave them, and against a decompiled startpos, what the
              build decides by itself is no difference; --everything compares every column of
              every row. --campaign picks the campaign of both sides,
              --left-/--right-campaign of one. --ignore-campaign leaves the campaign column out,
              to compare two campaigns (e.g. cr_combi_expanded and wh3_main_combi). --out writes
              the differing rows to a pack: db/<table>_tables/left_<campaign> and
              right_<campaign>. --json saves the left tables, --no-game skips reading the
              game's tables. Exit code 1 if they differ.
          table <name> [--pack FILE] [--vanilla] [--where COLUMN=VALUE] [--out FILE]
              Prints a DB table as tab-separated rows, as the game sees it with --pack loaded
              (its own tables, its dependencies, then vanilla); --vanilla reads the game's own
              packs only.
          config [--rpfm FOLDER]
              Shows or sets the RPFM folder (the folder with rpfm_server.exe).

        ESF inspection:
          esf-dump <file.esf> [--path A/B/C] [--depth N] [--elements N] [--out FILE] [--outer]
              Writes the ESF tree as an indented outline (--outer: the part outside the
              compressed block, without unpacking it).
          esf-find <file.esf> <value>... [--contains] [--path A/B/C] [--max N]
              Lists where values (strings or numbers) occur in the ESF tree.
          esf-stats <file.esf> <path> [--top N]
              For every record the path selects ("NAME[*]" = all elements, "NAME:*" = all
              siblings), summarises each value slot: kinds, distinct values, most common.
          esf-names <file.esf> [--path A/B/C]
              Lists every record name with its count and where it first occurs.

        Paths are record names joined by '/': "NAME:n" is the n-th child record called NAME,
        "NAME[n]" the n-th element of array record NAME.

        Common options:
          --rpfm FOLDER   RPFM folder (default: RPFM_FOLDER, else the saved setting)
        """;

    private static int ConfigCommand(string[] args)
    {
        var opts = Options.Parse(args, ["rpfm"]);
        var settings = ToolSettings.Load();
        if (opts.Get("rpfm") is { } folder)
        {
            string full = Path.GetFullPath(folder);
            if (!File.Exists(Path.Combine(full, "rpfm_server.exe")))
                return Fail($"{full} has no rpfm_server.exe.");
            settings.RpfmFolder = full;
            settings.Save();
        }
        Console.WriteLine($"settings: {ToolSettings.FilePath}");
        Console.WriteLine($"rpfm folder: {settings.RpfmFolder ?? "(not set)"}");
        return 0;
    }

    public static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}
