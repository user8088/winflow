using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinFlow.Core.Models;

namespace WinFlow.Core.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON in
/// <c>%APPDATA%\WinFlow\settings.json</c>. Missing or malformed files
/// fall back to defaults rather than throwing; when a file is partially
/// broken (e.g. a hand-edit typo), the readable fields are kept and the
/// original file is preserved as <c>settings.json.bak</c>.
///
/// Enums round-trip as strings ("Cloud", "Type", "AutoCorrect") to match
/// the documented hand-editable format; legacy integer ordinals from
/// older versions still load.
///
/// Also acts as the single source of truth for the current settings:
/// writers should go through <see cref="Update"/> so that concurrent
/// changes to different fields never clobber each other with stale copies.
/// </summary>
public sealed class SettingsStore
{
    public string FilePath { get; }

    public SettingsStore(string? baseDirectory = null)
    {
        FilePath = Path.Combine(
            baseDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WinFlow"),
            "settings.json");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Writes enum member names; still reads legacy integer ordinals
        // (allowIntegerValues defaults to true).
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private AppSettings? _current;

    /// <summary>Raised when <see cref="Save"/> fails; settings remain in memory.</summary>
    public event Action<Exception>? SaveFailed;

    /// <summary>The current settings, loaded from disk on first access.</summary>
    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current ??= Load();
            }
        }
    }

    /// <summary>
    /// Atomically applies <paramref name="mutate"/> to the current settings,
    /// persists the result, and returns it.
    /// </summary>
    public AppSettings Update(Func<AppSettings, AppSettings> mutate)
    {
        lock (_gate)
        {
            _current = mutate(_current ?? Load());
            Save(_current);
            return _current;
        }
    }

    public AppSettings Load()
    {
        string json;
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            json = File.ReadAllText(FilePath);
        }
        catch
        {
            return new AppSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            return SanitizeEnumValues(settings);
        }
        catch (JsonException)
        {
            // One bad field must not wipe every preference: keep the original
            // file for the user, then salvage whatever fields still parse.
            BackUpCorruptFile();
            return RecoverFields(json);
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
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(settings, JsonOptions);
            string tempPath = Path.Combine(dir, Path.GetRandomFileName());

            try
            {
                File.WriteAllText(tempPath, json);
                if (File.Exists(FilePath))
                {
                    File.Replace(tempPath, FilePath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tempPath, FilePath);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        catch (Exception ex)
        {
            // Settings are best-effort; a failed save must not crash dictation.
            Debug.WriteLine($"Settings save failed: {ex}");
            SaveFailed?.Invoke(ex);
        }
    }

    private void BackUpCorruptFile()
    {
        try
        {
            File.Copy(FilePath, FilePath + ".bak", overwrite: true);
        }
        catch
        {
            // Best-effort; recovery below proceeds either way.
        }
    }

    /// <summary>
    /// Tolerant re-parse: reads recognizable fields individually so a single
    /// broken value only defaults that field instead of the whole settings set.
    /// </summary>
    private static AppSettings RecoverFields(string json)
    {
        var settings = new AppSettings();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return settings;
            }

            foreach (JsonProperty property in doc.RootElement.EnumerateObject())
            {
                JsonElement value = property.Value;
                switch (property.Name.ToLowerInvariant())
                {
                    case "schemaversion":
                        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int version))
                        {
                            settings = settings with { SchemaVersion = version };
                        }
                        break;
                    case "sttmode":
                        if (TryReadEnum(value, out SttMode sttMode))
                        {
                            settings = settings with { SttMode = sttMode };
                        }
                        break;
                    case "nearfieldmic":
                        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        {
                            settings = settings with { NearFieldMic = value.GetBoolean() };
                        }
                        break;
                    case "language":
                        if (value.ValueKind == JsonValueKind.String)
                        {
                            settings = settings with { Language = value.GetString() };
                        }
                        break;
                    case "modeldirectory":
                        if (value.ValueKind == JsonValueKind.String)
                        {
                            settings = settings with { ModelDirectory = value.GetString() };
                        }
                        break;
                    case "inputmethod":
                        if (TryReadEnum(value, out InputMethod inputMethod))
                        {
                            settings = settings with { InputMethod = inputMethod };
                        }
                        break;
                    case "correctionmode":
                        if (TryReadEnum(value, out CorrectionMode correctionMode))
                        {
                            settings = settings with { CorrectionMode = correctionMode };
                        }
                        break;
                }
            }
        }
        catch
        {
            // Not even structurally valid JSON; the .bak keeps the user's data.
        }

        return settings;
    }

    /// <summary>
    /// JsonSerializer accepts out-of-range integer ordinals without error;
    /// reset any undefined enum members to <see cref="AppSettings"/> defaults.
    /// </summary>
    private static AppSettings SanitizeEnumValues(AppSettings settings)
    {
        var defaults = new AppSettings();
        AppSettings sanitized = settings;

        if (!Enum.IsDefined(settings.SttMode))
        {
            sanitized = sanitized with { SttMode = defaults.SttMode };
        }

        if (!Enum.IsDefined(settings.InputMethod))
        {
            sanitized = sanitized with { InputMethod = defaults.InputMethod };
        }

        if (!Enum.IsDefined(settings.CorrectionMode))
        {
            sanitized = sanitized with { CorrectionMode = defaults.CorrectionMode };
        }

        return sanitized;
    }

    private static bool TryReadEnum<TEnum>(JsonElement element, out TEnum value)
        where TEnum : struct, Enum
    {
        if (element.ValueKind == JsonValueKind.String
            && Enum.TryParse(element.GetString(), ignoreCase: true, out value)
            && Enum.IsDefined(value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int ordinal))
        {
            var candidate = (TEnum)Enum.ToObject(typeof(TEnum), ordinal);
            if (Enum.IsDefined(candidate))
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }
}
