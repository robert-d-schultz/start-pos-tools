using System.Security.Cryptography;
using StartPos.Core.Esf;
using StartPos.Core.Model;
using StartPos.Core.Rpfm;

namespace StartPos.Core.Decompile;

/// <summary>
/// A campaigns/&lt;folder&gt;/startpos.esf of a pack, extracted, with the campaign it is really
/// for (read from the file: the folder name can differ, e.g. IEE ships its cr_combi_expanded
/// startpos in wh3_main_combi too so multiplayer finds it).
/// </summary>
public sealed record StartPosFile(string PackPath, string Folder, string Campaign, string LocalPath, string Hash)
{
    public bool InOwnFolder => Folder.Equals(Campaign, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The startpos files of a pack, grouped by the campaign they are for.</summary>
public sealed class StartPosListing
{
    public IReadOnlyList<StartPosFile> Files { get; }

    /// <summary>One file per campaign: the one in the campaign's own folder when there is one.</summary>
    public IReadOnlyList<StartPosFile> Campaigns { get; }

    /// <summary>Copies and files whose folder is not their campaign, as sentences.</summary>
    public IReadOnlyList<string> Notes { get; }

    public StartPosListing(IReadOnlyList<StartPosFile> files)
    {
        Files = files;
        var campaigns = new List<StartPosFile>();
        var notes = new List<string>();
        foreach (var group in files.GroupBy(f => f.Campaign, StringComparer.OrdinalIgnoreCase))
        {
            var chosen = group.OrderByDescending(f => f.InOwnFolder).ThenBy(f => f.PackPath, StringComparer.Ordinal).First();
            campaigns.Add(chosen);
            foreach (var other in group.Where(f => f != chosen))
                notes.Add(other.Hash == chosen.Hash
                    ? $"{other.PackPath} is a copy of {chosen.PackPath} (both are campaign {chosen.Campaign}); it is not listed separately."
                    : $"{other.PackPath} is also for campaign {chosen.Campaign} but differs from {chosen.PackPath}; {chosen.PackPath} is used.");
            if (!chosen.InOwnFolder)
                notes.Add($"{chosen.PackPath} is for campaign {chosen.Campaign}, not {chosen.Folder}.");
        }
        Campaigns = campaigns.OrderBy(f => f.Campaign, StringComparer.Ordinal).ToList();
        Notes = notes;
    }

    /// <summary>
    /// The startpos to decompile: the one for <paramref name="campaign"/> (or, failing that, the
    /// one in a folder of that name), or the only campaign when none is given.
    /// </summary>
    public StartPosFile Find(string? campaign)
    {
        if (campaign == null)
            return Campaigns.Count switch
            {
                0 => throw new InvalidOperationException($"The pack has no campaigns/<campaign>/{DecompileJob.StartPosFileName}."),
                1 => Campaigns[0],
                _ => throw new InvalidOperationException($"The pack has startpos files for several campaigns; pick one: {string.Join(", ", Campaigns.Select(c => c.Campaign))}."),
            };
        return Campaigns.FirstOrDefault(f => f.Campaign.Equals(campaign, StringComparison.OrdinalIgnoreCase))
               ?? Files.FirstOrDefault(f => f.Folder.Equals(campaign, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"The pack has no startpos for campaign {campaign}" +
                                                      (Campaigns.Count > 0 ? $" (it has: {string.Join(", ", Campaigns.Select(c => c.Campaign))})." : "."));
    }

    /// <summary>Extracts every campaigns/&lt;folder&gt;/startpos.esf of an open pack under <paramref name="workFolder"/> and reads which campaign it is.</summary>
    public static async Task<StartPosListing> LoadAsync(RpfmSession rpfm, string packKey, string workFolder, CancellationToken ct = default)
    {
        var paths = (await rpfm.ListFilesAsync(packKey, ct))
            .Where(p => p.StartsWith("campaigns/", StringComparison.OrdinalIgnoreCase) &&
                        p.EndsWith("/" + DecompileJob.StartPosFileName, StringComparison.OrdinalIgnoreCase) && p.Count(c => c == '/') == 2)
            .ToList();
        var files = new List<StartPosFile>();
        foreach (string path in paths)
        {
            string local = await rpfm.ExtractFileAsync(packKey, path, workFolder, ct);
            files.Add(Read(path, local));
        }
        return new StartPosListing(files);
    }

    /// <summary>Reads the campaign of a startpos file from its uncompressed part, without unpacking the rest.</summary>
    public static StartPosFile Read(string packPath, string localPath)
    {
        byte[] data = File.ReadAllBytes(localPath);
        string campaign = StartPosEsf.ReadCampaign(EsfDocument.Parse(data, decompress: false));
        string folder = packPath.Split('/') is { Length: >= 2 } parts ? parts[^2] : "";
        return new StartPosFile(packPath, folder, campaign, localPath, Convert.ToHexString(SHA256.HashData(data)));
    }
}
