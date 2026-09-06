using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimDeck.Core.Ota;

public sealed class FirmwareEntry
{
    [JsonPropertyName("version")]   public string Version { get; set; } = "0.0.0";
    [JsonPropertyName("file")]      public string File { get; set; } = "";
    [JsonPropertyName("sha256")]    public string Sha256 { get; set; } = "";
    [JsonPropertyName("size")]      public long Size { get; set; }
    [JsonPropertyName("board")]     public string Board { get; set; } = "";
    [JsonPropertyName("notes")]     public string Notes { get; set; } = "";
    [JsonPropertyName("published")] public string Published { get; set; } = "";
}

/// <summary>
/// The manifest is the source of truth and is re-read on every access, so a
/// new build can be dropped in without restarting.
/// </summary>
public sealed class FirmwareRepo
{
    private readonly string _manifestPath;
    public string Directory { get; }

    public FirmwareRepo(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
        _manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(_manifestPath)) Write(new Dictionary<string, FirmwareEntry>());
    }

    private void Write(Dictionary<string, FirmwareEntry> blob)
    {
        var tmp = _manifestPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(blob,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _manifestPath, overwrite: true);
    }

    public Dictionary<string, FirmwareEntry> All()
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, FirmwareEntry>>(
                File.ReadAllText(_manifestPath), Json.Options) ?? new();
        }
        catch { return new(); }
    }

    /// <summary>Manifest entry for a module type, or null if the image file
    /// listed in the manifest has gone missing.</summary>
    public FirmwareEntry? Get(string moduleType)
    {
        if (!All().TryGetValue(moduleType, out var e)) return null;
        return File.Exists(Path.Combine(Directory, e.File)) ? e : null;
    }

    public static string Sha256File(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    public FirmwareEntry Publish(string moduleType, string binaryPath,
                                 string version, string board = "",
                                 string notes = "")
    {
        var fileName = $"{moduleType}_{version}.bin";
        var dest = Path.Combine(Directory, fileName);
        File.Copy(binaryPath, dest, overwrite: true);

        var entry = new FirmwareEntry
        {
            Version = version,
            File = fileName,
            Sha256 = Sha256File(dest),
            Size = new FileInfo(dest).Length,
            Board = board,
            Notes = notes,
            Published = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        var blob = All();
        blob[moduleType] = entry;
        Write(blob);
        return entry;
    }

    public void Remove(string moduleType)
    {
        var blob = All();
        if (blob.Remove(moduleType, out var e))
        {
            var p = Path.Combine(Directory, e.File);
            if (File.Exists(p)) try { File.Delete(p); } catch { }
            Write(blob);
        }
    }
}
