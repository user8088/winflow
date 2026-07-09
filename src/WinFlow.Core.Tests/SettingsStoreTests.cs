using WinFlow.Core.Models;
using WinFlow.Core.Services;

namespace WinFlow.Core.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "WinFlowTests", Path.GetRandomFileName());

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private SettingsStore NewStore() => new(_dir);

    private static readonly AppSettings NonDefaultSettings = new()
    {
        SttMode = SttMode.Cloud,
        NearFieldMic = false,
        Language = "de",
        ModelDirectory = @"D:\models",
        InputMethod = InputMethod.Type,
        CorrectionMode = CorrectionMode.Aggressive,
    };

    [Fact]
    public void LoadOnMissingFileReturnsDefaults()
    {
        Assert.Equal(new AppSettings(), NewStore().Load());
    }

    [Fact]
    public void SaveLoadRoundTripPreservesEveryProperty()
    {
        NewStore().Save(NonDefaultSettings);

        AppSettings loaded = NewStore().Load();

        Assert.Equal(NonDefaultSettings, loaded);
    }

    [Fact]
    public void SavedFileUsesStringEnumsAndSchemaVersion()
    {
        SettingsStore store = NewStore();
        store.Save(NonDefaultSettings);

        string json = File.ReadAllText(store.FilePath);

        // The user manual documents string enum values in settings.json.
        Assert.Contains("\"sttMode\": \"Cloud\"", json);
        Assert.Contains("\"inputMethod\": \"Type\"", json);
        Assert.Contains("\"correctionMode\": \"Aggressive\"", json);
        Assert.Contains($"\"schemaVersion\": {AppSettings.CurrentSchemaVersion}", json);
    }

    [Fact]
    public void LoadReadsStringEnumsAsDocumentedInManual()
    {
        SettingsStore store = NewStore();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.FilePath, """
            {
              "sttMode": "Local",
              "inputMethod": "Paste",
              "correctionMode": "AutoCorrect"
            }
            """);

        AppSettings loaded = store.Load();

        Assert.Equal(SttMode.Local, loaded.SttMode);
        Assert.Equal(InputMethod.Paste, loaded.InputMethod);
        Assert.Equal(CorrectionMode.AutoCorrect, loaded.CorrectionMode);
    }

    [Fact]
    public void LoadReadsLegacyIntegerEnums()
    {
        SettingsStore store = NewStore();
        Directory.CreateDirectory(_dir);
        // Files written before JsonStringEnumConverter used bare ordinals.
        File.WriteAllText(store.FilePath, """
            {
              "sttMode": 1,
              "nearFieldMic": false,
              "inputMethod": 2,
              "correctionMode": 0
            }
            """);

        AppSettings loaded = store.Load();

        Assert.Equal(SttMode.Local, loaded.SttMode);
        Assert.False(loaded.NearFieldMic);
        Assert.Equal(InputMethod.Type, loaded.InputMethod);
        Assert.Equal(CorrectionMode.Off, loaded.CorrectionMode);
    }

    [Fact]
    public void LegacyFileWithoutSchemaVersionDefaultsToCurrent()
    {
        SettingsStore store = NewStore();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.FilePath, """{ "sttMode": "Cloud" }""");

        Assert.Equal(AppSettings.CurrentSchemaVersion, store.Load().SchemaVersion);
    }

    [Fact]
    public void MalformedJsonReturnsDefaultsAndLeavesBackup()
    {
        SettingsStore store = NewStore();
        Directory.CreateDirectory(_dir);
        const string garbage = "{ not json at all !!!";
        File.WriteAllText(store.FilePath, garbage);

        AppSettings loaded = store.Load();

        Assert.Equal(new AppSettings(), loaded);
        Assert.Equal(garbage, File.ReadAllText(store.FilePath + ".bak"));
    }

    [Fact]
    public void OneBrokenFieldOnlyDefaultsThatFieldAndLeavesBackup()
    {
        SettingsStore store = NewStore();
        Directory.CreateDirectory(_dir);
        // A hand-edit typo in one enum must not wipe the other preferences.
        File.WriteAllText(store.FilePath, """
            {
              "sttMode": "NotARealMode",
              "nearFieldMic": false,
              "language": "fr",
              "modelDirectory": "C:\\models",
              "inputMethod": "Type",
              "correctionMode": "Off"
            }
            """);

        AppSettings loaded = store.Load();

        Assert.Equal(new AppSettings().SttMode, loaded.SttMode);
        Assert.False(loaded.NearFieldMic);
        Assert.Equal("fr", loaded.Language);
        Assert.Equal(@"C:\models", loaded.ModelDirectory);
        Assert.Equal(InputMethod.Type, loaded.InputMethod);
        Assert.Equal(CorrectionMode.Off, loaded.CorrectionMode);
        Assert.True(File.Exists(store.FilePath + ".bak"));
    }

    [Fact]
    public void UpdatePersistsSoAFreshStoreSeesTheChange()
    {
        SettingsStore store = NewStore();

        AppSettings updated = store.Update(s => s with { SttMode = SttMode.Local, Language = "en" });

        Assert.Equal(SttMode.Local, updated.SttMode);
        Assert.Equal(updated, store.Current);
        Assert.Equal(updated, NewStore().Load());
    }

    [Fact]
    public void CurrentLoadsFromDiskOnFirstAccess()
    {
        NewStore().Save(NonDefaultSettings);

        Assert.Equal(NonDefaultSettings, NewStore().Current);
    }
}
