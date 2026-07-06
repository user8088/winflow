using System.IO;
using System.Text.Json;

namespace WinFlow.Core.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON in
/// <c>%APPDATA%\WinFlow\settings.json</c>. Missing or malformed files
/// fall back to defaults rather than throwing.
/// </summary>
public sealed class SettingsStore
{
    public string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WinFlow", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Settings are best-effort; a failed save must not crash dictation.
        }
    }
}
