using System.Text;

namespace Bitiq.Core.Translation;

public sealed class GlossaryEntry
{
    public string Source { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;

    public GlossaryEntry() { }

    public GlossaryEntry(string source, string target)
    {
        Source = source;
        Target = target;
    }

    public GlossaryEntry Clone() => new(Source, Target);
}

public sealed class CharacterEntry
{
    public string Name { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;

    public CharacterEntry() { }

    public CharacterEntry(string name, string? note = null)
    {
        Name = name;
        Note = note ?? string.Empty;
    }

    public CharacterEntry Clone() => new(Name, Note);
}

public sealed class GameProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Synopsis { get; set; } = string.Empty;
    public string ToneNote { get; set; } = string.Empty;
    public List<CharacterEntry> Characters { get; set; } = [];
    public List<GlossaryEntry> Glossary { get; set; } = [];

    public bool IsEffectivelyEmpty =>
        string.IsNullOrWhiteSpace(Title)
        && string.IsNullOrWhiteSpace(Synopsis)
        && string.IsNullOrWhiteSpace(ToneNote)
        && !Characters.Any(c => !string.IsNullOrWhiteSpace(c.Name))
        && !Glossary.Any(g => !string.IsNullOrWhiteSpace(g.Source) && !string.IsNullOrWhiteSpace(g.Target));

    public GameProfile Clone() => new()
    {
        Id = Id,
        Title = Title,
        Synopsis = Synopsis,
        ToneNote = ToneNote,
        Characters = Characters.Select(c => c.Clone()).ToList(),
        Glossary = Glossary.Select(g => g.Clone()).ToList()
    };

    public override string ToString() =>
        string.IsNullOrWhiteSpace(Title) ? Id : Title;
}

public sealed record ContextLine(string Source, string Target);

public sealed record TranslationContextSnapshot(GameProfile? Profile, IReadOnlyList<ContextLine> History)
{
    public bool IsEmpty => (Profile is null || Profile.IsEffectivelyEmpty) && History.Count == 0;

    public string ProfileFingerprint =>
        Profile is null || Profile.IsEffectivelyEmpty
            ? string.Empty
            : ComputeProfileFingerprint(Profile);

    public string FullFingerprint
    {
        get
        {
            string pf = ProfileFingerprint;
            string hf = History.Count > 0 ? ComputeFnv1aHex(History[^1].Source) : string.Empty;
            if (pf.Length == 0 && hf.Length == 0)
                return string.Empty;
            return $"{pf}:{hf}";
        }
    }

    private static string ComputeProfileFingerprint(GameProfile p)
    {
        var sb = new StringBuilder(256);
        sb.Append(p.Id).Append('|')
          .Append(p.Title.Trim()).Append('|')
          .Append(p.Synopsis.Trim()).Append('|')
          .Append(p.ToneNote.Trim()).Append('|');
        foreach (var c in p.Characters)
        {
            if (!string.IsNullOrWhiteSpace(c.Name))
                sb.Append(c.Name.Trim()).Append('=').Append(c.Note.Trim()).Append(';');
        }
        sb.Append('|');
        foreach (var g in p.Glossary)
        {
            if (!string.IsNullOrWhiteSpace(g.Source) && !string.IsNullOrWhiteSpace(g.Target))
                sb.Append(g.Source.Trim()).Append("=>").Append(g.Target.Trim()).Append(';');
        }
        return ComputeFnv1aHex(sb.ToString());
    }

    public static string ComputeFnv1aHex(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "0";

        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offsetBasis;
        foreach (char ch in text)
        {
            hash ^= (byte)(ch & 0xFF);
            hash *= prime;
            hash ^= (byte)(ch >> 8);
            hash *= prime;
        }
        return hash.ToString("x16");
    }
}
