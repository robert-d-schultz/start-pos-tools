using System.Text.Json;

namespace StartPos.Core;

/// <summary>Settings shared by the window app and the command line, in %APPDATA%\StartPosTools\settings.json.</summary>
public sealed class ToolSettings
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StartPosTools", "settings.json");

    /// <summary>Folder holding rpfm_server.exe (RPFM 5.3).</summary>
    public string? RpfmFolder { get; set; }

    // The window's fields, restored on the next start.
    public string? DecompileInput { get; set; }
    public string? DecompileCampaign { get; set; }
    public string? DecompileOutput { get; set; }
    public string? DecompileName { get; set; }
    public string? CompareLeft { get; set; }
    public string? CompareRight { get; set; }
    public string? CompareLeftCampaign { get; set; }
    public string? CompareRightCampaign { get; set; }
    public string? CompareOutput { get; set; }
    public bool CompareIgnoreCampaign { get; set; } = true;
    public bool CompareOnlyBuilt { get; set; } = true;

    /// <summary>Compare reads the left (right) pack's start_pos tables rather than decompiling its startpos.esf.</summary>
    public bool CompareLeftTables { get; set; }
    public bool CompareRightTables { get; set; }

    public static ToolSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ToolSettings>(File.ReadAllText(FilePath)) ?? new ToolSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken settings file is not worth failing over; start from defaults.
        }
        return new ToolSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The RPFM folder to use: <paramref name="overrideFolder"/>, the RPFM_FOLDER environment variable, or the saved setting.</summary>
    public static string? ResolveRpfmFolder(string? overrideFolder) =>
        !string.IsNullOrWhiteSpace(overrideFolder) ? overrideFolder
        : Environment.GetEnvironmentVariable("RPFM_FOLDER") is { Length: > 0 } env ? env
        : Load().RpfmFolder;
}
