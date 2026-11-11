using System.IO;
using System.Text.Json;

namespace LiteRec.Core;

public sealed class AppSettings
{
    public int ResolutionIndex { get; set; } = 0;  // 720p
    public int FpsIndex { get; set; } = 0;         // 60
    public int QualityIndex { get; set; } = 1;     // Equilibrada
    public bool UseHevc { get; set; }
    public bool SystemAudio { get; set; } = true;
    public bool Microphone { get; set; }
    public bool ShowCursor { get; set; } = true;
    public bool ShowClicks { get; set; }
    public bool HideFromCapture { get; set; } = true;
    public bool MinimizeOnRecord { get; set; }
    public string OutputFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "LiteRec");

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiteRec", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* ajustes corruptos: usar valores por defecto */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
