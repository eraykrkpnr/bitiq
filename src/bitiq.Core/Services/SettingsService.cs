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
        SettingsDirectory = !string.IsNullOrWhiteSpace(customDirectory)
            ? customDirectory
            : AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");
    }

    public string ModelsDirectory => Path.Combine(SettingsDirectory, "models");
    public string LlamaDirectory => Path.Combine(SettingsDirectory, "llama");
    public string ProfilesDirectory => Path.Combine(SettingsDirectory, "profiles");

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

            bool hasLocalModelField = false;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Name.Equals(nameof(AppSettings.LocalModel), StringComparison.OrdinalIgnoreCase))
                        {
                            hasLocalModelField = true;
                            break;
                        }
                    }
                }
            }
            catch { }

            bool migrated = false;
            // Mevcut kurulumlarda LocalModel alanı yoksa kurulu TranslateGemma 4B'yi koru (beklenmedik 6.16 GB indirmeyi önle)
            if (!hasLocalModelField && settings.FirstRunCompleted)
            {
                settings.LocalModel = LocalModelKind.TranslateGemma4B;
                migrated = true;
            }

            NormalizeModelAndContextSettings(settings);

            if (!settings.SmartOcrDefaultApplied)
            {
                settings.OcrPreprocessEnabled = true;
                settings.FrameChangeThreshold = Math.Min(settings.FrameChangeThreshold, 1.2);
                settings.SmartOcrDefaultApplied = true;
                migrated = true;
            }

            EnsureBuiltInProfiles(settings);
            if (migrated)
                Save(settings);

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

    private static void NormalizeModelAndContextSettings(AppSettings settings)
    {
        if (settings.LocalModel == LocalModelKind.HyMt2_7B)
        {
            settings.LocalModelQuantization = ModelQuantization.Q6_K;
        }
        else if (settings.LocalModelQuantization == ModelQuantization.Q6_K)
        {
            settings.LocalModelQuantization = ModelQuantization.Q8_0;
        }

        settings.ContextMaxLines = Math.Clamp(settings.ContextMaxLines, 2, 16);
        settings.ContextTrimToLines = Math.Clamp(settings.ContextTrimToLines, 1, settings.ContextMaxLines);
        settings.ContextIdleResetSeconds = Math.Clamp(settings.ContextIdleResetSeconds, 15, 600);
        settings.SimulatedVramLimitGb = Math.Clamp(settings.SimulatedVramLimitGb, 6.0, 24.0);
    }
}
