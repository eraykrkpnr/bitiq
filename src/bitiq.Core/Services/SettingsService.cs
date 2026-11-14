using System.Text.Encodings.Web;
using System.Text.Json;
using Bitiq.Core.Models;

namespace Bitiq.Core.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string SettingsDirectory { get; }
    public string SettingsFilePath { get; }

    public SettingsService(string? customDirectory = null)
    {
        if (customDirectory is not null)
        {
            SettingsDirectory = customDirectory;
        }
        else
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string bitiqDir = Path.Combine(localAppData, "bitiq");
            string legacyDir = Path.Combine(localAppData, "BetterSubs");

            if (!Directory.Exists(bitiqDir) && Directory.Exists(legacyDir))
            {
                try
                {
                    Directory.Move(legacyDir, bitiqDir);
                }
                catch
                {
                    bitiqDir = legacyDir;
                }
            }

            SettingsDirectory = bitiqDir;
        }

        SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");
    }

    public string ModelsDirectory => Path.Combine(SettingsDirectory, "models");
    public string LlamaDirectory => Path.Combine(SettingsDirectory, "llama");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                var defaults = new AppSettings
                {
                    OcrPreprocessEnabled = true,
                    SmartOcrDefaultApplied = true
                };
                EnsureBuiltInProfiles(defaults);
                return defaults;
            }

            var json = File.ReadAllText(SettingsFilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            if (!settings.SmartOcrDefaultApplied)
            {
                settings.OcrPreprocessEnabled = true;
                settings.FrameChangeThreshold = Math.Min(settings.FrameChangeThreshold, 1.2);
                settings.SmartOcrDefaultApplied = true;
                EnsureBuiltInProfiles(settings);
                Save(settings);
                return settings;
            }

            EnsureBuiltInProfiles(settings);
            return settings;
        }
        catch
        {
            var fallback = new AppSettings
            {
                OcrPreprocessEnabled = true,
                SmartOcrDefaultApplied = true
            };
            EnsureBuiltInProfiles(fallback);
            return fallback;
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var tempPath = SettingsFilePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, SettingsFilePath, overwrite: true);
    }

    private static void EnsureBuiltInProfiles(AppSettings settings)
    {
        var builtIns = StyleProfile.CreateBuiltInPresets();
        settings.SavedProfiles ??= [];

        foreach (var builtIn in builtIns)
        {
            var existing = settings.SavedProfiles.FirstOrDefault(
                p => string.Equals(p.Name, builtIn.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                settings.SavedProfiles.Add(builtIn);
            }
            else
            {
                existing.IsBuiltIn = true;
            }
        }

        settings.ActiveStyle ??= builtIns[0].Clone();

        // Eski varsayılan 0.2 harf aralığının taşma yapmaması için 0.0'a güncelle
        if (Math.Abs(settings.ActiveStyle.LetterSpacing - 0.2) < 0.001)
            settings.ActiveStyle.LetterSpacing = 0.0;
        foreach (var profile in settings.SavedProfiles)
        {
            if (Math.Abs(profile.LetterSpacing - 0.2) < 0.001)
                profile.LetterSpacing = 0.0;
        }
    }
}
