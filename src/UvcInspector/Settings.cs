using System.IO;
using System.Text.Json;

namespace UvcInspector;

public sealed record Settings(string? FfmpegPath = null, double? WorkspaceHeight = null)
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UvcInspector");
    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(DirectoryPath, "settings.json"))) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        var target = Path.Combine(DirectoryPath, "settings.json");
        var temporary = target + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, target, true);
    }
}
