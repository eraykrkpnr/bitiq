using System.Text.Encodings.Web;
using System.Text.Json;
using Bitiq.Core.Translation;

namespace Bitiq.Core.Services;

/// <summary>
/// Oyun profillerini (uygulama klasörü altındaki profiles\*.json) yönetir ve JSON içe/dışa aktarımını sağlar.
/// </summary>
public sealed class GameProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _profilesDir;

    public GameProfileService(SettingsService settingsService)
        : this(settingsService.ProfilesDirectory)
    {
    }

    public GameProfileService(string profilesDirectory)
    {
        _profilesDir = profilesDirectory;
    }

    public string ProfilesDirectory => _profilesDir;

    public List<GameProfile> LoadAll()
    {
        if (!Directory.Exists(_profilesDir))
            return [];

        var list = new List<GameProfile>();
        foreach (string file in Directory.GetFiles(_profilesDir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string json = File.ReadAllText(file);
                var profile = JsonSerializer.Deserialize<GameProfile>(json, JsonOptions);
                if (profile is null)
                    continue;

                if (string.IsNullOrWhiteSpace(profile.Id))
                    profile.Id = Path.GetFileNameWithoutExtension(file);
                SanitizeCollections(profile);
                list.Add(profile);
            }
            catch
            {
                // Bozuk JSON profili diğer profilleri engellemesin
            }
        }

        return list.OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Save(GameProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Id))
            profile.Id = Guid.NewGuid().ToString("N");

        SanitizeCollections(profile);
        Directory.CreateDirectory(_profilesDir);

        string path = GetProfilePath(profile.Id);
        string tmp = path + ".tmp";
        string json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    public void Delete(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return;

        string path = GetProfilePath(profileId);
        if (File.Exists(path))
            File.Delete(path);
    }

    public void ExportToFile(GameProfile profile, string filePath)
    {
        ArgumentNullException.ThrowIfNull(profile);
        SanitizeCollections(profile);
        string json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(filePath, json);
    }

    public GameProfile ImportFromFile(string filePath)
    {
        string json = File.ReadAllText(filePath);
        var profile = JsonSerializer.Deserialize<GameProfile>(json, JsonOptions)
            ?? throw new InvalidDataException("Geçersiz oyun profili JSON dosyası.");

        if (string.IsNullOrWhiteSpace(profile.Id))
            profile.Id = Guid.NewGuid().ToString("N");
        SanitizeCollections(profile);

        // Aynı ID'li profil varsa yeni ID ver ki mevcut profilin üzerine istemeden yazılmasın
        if (File.Exists(GetProfilePath(profile.Id)))
            profile.Id = Guid.NewGuid().ToString("N");

        Save(profile);
        return profile;
    }

    private string GetProfilePath(string profileId)
    {
        string safeId = string.Concat(profileId.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));
        if (string.IsNullOrWhiteSpace(safeId))
            safeId = Guid.NewGuid().ToString("N");
        return Path.Combine(_profilesDir, $"{safeId}.json");
    }

    private static void SanitizeCollections(GameProfile profile)
    {
        profile.Characters ??= [];
        profile.Glossary ??= [];
        profile.Characters = profile.Characters
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => new CharacterEntry(c.Name.Trim(), c.Note?.Trim()))
            .ToList();
        profile.Glossary = profile.Glossary
            .Where(g => !string.IsNullOrWhiteSpace(g.Source) && !string.IsNullOrWhiteSpace(g.Target))
            .Select(g => new GlossaryEntry(g.Source.Trim(), g.Target.Trim()))
            .ToList();
    }
}
