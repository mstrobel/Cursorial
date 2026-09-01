namespace Cursorial.Rendering;

public interface IGraphemeCache
{
    static IGraphemeCache None => NoOpGraphemeCache.Empty;

    string Cache(scoped ReadOnlySpan<char> grapheme);
    
    private sealed class NoOpGraphemeCache : IGraphemeCache
    {
        public static readonly IGraphemeCache Empty = new NoOpGraphemeCache();
        public string Cache(scoped ReadOnlySpan<char> grapheme) => grapheme.ToString();
    }
}

public sealed class GraphemeCache : IGraphemeCache
{
    private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _cache =
        new Dictionary<string, string>().GetAlternateLookup<ReadOnlySpan<char>>();

    public string Cache(scoped ReadOnlySpan<char> grapheme)
    {
        if (grapheme.IsEmpty) return string.Empty;

        if (_cache.TryGetValue(grapheme, out var cached))
            return cached;

        cached = grapheme.ToString();
        _cache[grapheme] = cached;
        return cached;
    }
}