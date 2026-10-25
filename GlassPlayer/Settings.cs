using System;
using System.IO;
using System.Text.Json;

namespace GlassPlayer;

class AppSettings
{
    public bool Autoplay { get; set; } = true;
    public bool LaunchSpotify { get; set; } = true;
    public bool AmbientGlow { get; set; } = true;
    public bool RememberVolume { get; set; } = true;
    public double Volume { get; set; } = 0.8;

    static readonly string File_ = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlassPlayer", "settings.json");

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(File_)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            File.WriteAllText(File_, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}
