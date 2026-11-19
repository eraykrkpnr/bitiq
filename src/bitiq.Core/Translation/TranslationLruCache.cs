using Bitiq.Core.Ocr;

namespace Bitiq.Core.Translation;

/// <summary>
/// Normalize edilmiş kaynak metin + dil çifti anahtarlı thread-safe LRU çeviri önbelleği.
/// </summary>
public sealed class TranslationLruCache
{
    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<CacheEntry> _lru = new();

    private sealed record CacheEntry(string Key, string Value);

    public TranslationLruCache(int capacity = 500)
    {
        _capacity = Math.Max(16, capacity);
    }

    public int Count
    {
        get
        {
            lock (_lock)
                return _map.Count;
        }
    }

    public static string? ResolveFingerprint(TranslationContextSnapshot? context, string text)
    {
        if (context is null || context.IsEmpty)
            return null;

        string normalized = SubtitleStabilizer.NormalizeForComparison(text);
        if (string.IsNullOrEmpty(normalized))
            return null;

        // Kısa UI metinlerinde (<= 3 kelime) yalnızca oyun profiline göre önbellekle; uzun cümlelerde son geçmiş satırını da ekle
        int wordCount = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        string fp = wordCount <= 3 ? context.ProfileFingerprint : context.FullFingerprint;
        return string.IsNullOrEmpty(fp) ? null : fp;
    }

    public static string MakeKey(string sourceLang, string targetLang, string text, string? contextFingerprint = null)
    {
        string normalized = SubtitleStabilizer.NormalizeForComparison(text);
        string prefix = $"{sourceLang.ToLowerInvariant()}>{targetLang.ToLowerInvariant()}:{normalized}";
        return string.IsNullOrEmpty(contextFingerprint) ? prefix : $"{prefix}|{contextFingerprint}";
    }

    public bool TryGet(string sourceLang, string targetLang, string text, out string translation, string? contextFingerprint = null)
    {
        string key = MakeKey(sourceLang, targetLang, text, contextFingerprint);
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                translation = node.Value.Value;
                return true;
            }
        }

        translation = string.Empty;
        return false;
    }

    public void Set(string sourceLang, string targetLang, string text, string translation, string? contextFingerprint = null)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(translation))
            return;

        string key = MakeKey(sourceLang, targetLang, text, contextFingerprint);
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                existing.Value = new CacheEntry(key, translation);
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }

            if (_map.Count >= _capacity && _lru.Last is not null)
            {
                var oldest = _lru.Last;
                _lru.RemoveLast();
                _map.Remove(oldest.Value.Key);
            }

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(key, translation));
            _lru.AddFirst(node);
            _map[key] = node;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _lru.Clear();
        }
    }
}
