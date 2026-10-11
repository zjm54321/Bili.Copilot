using BiliCopilot.UI.Models.Constants;

namespace BiliCopilot.UI.Classic;

public static class ClassicNavigationPolicy
{
    public static ClassicPlaybackRequest? Find(ClassicSourceResult source, ClassicSessionOptions options,
        bool forward, bool allowRecommendations)
    {
        var request = source.Media.Request;
        var item = request.Item;
        var step = forward ? 1 : -1;
        var part = UniqueIndex(source.Parts, p => p.Identifier.Id, item.PreferPart?.Identifier.Id);
        if (part is int p && p + step >= 0 && p + step < source.Parts.Count)
            return request with { Item = item with { PreferPart = source.Parts[p + step], StartPosition = 0 } };

        if (request.Playlist.Count > 0)
        {
            if (request.PlaylistIndex is int index && index >= 0 && index < request.Playlist.Count
                && index + step >= 0 && index + step < request.Playlist.Count)
                return SelectPlaylist(request, index + step);
            if (options.EndWithPlaylist) return null;
        }

        if (item.Type == BiliMediaType.Pgc)
        {
            var episode = UniqueIndex(source.Episodes, e => e.Identifier.Id, item.Episode?.Identifier.Id);
            if (episode is int e && e + step >= 0 && e + step < source.Episodes.Count)
                return request with { Item = item with { Episode = source.Episodes[e + step], StartPosition = 0 } };
        }
        var season = UniqueIndex(source.SeasonVideos, v => v.Identifier.Id, item.Video?.Identifier.Id);
        if (season is int s && s + step >= 0 && s + step < source.SeasonVideos.Count)
            return request with { Item = new(BiliMediaType.Video, source.SeasonVideos[s + step], null,
                null, null, null, null, 0, item.IsPrivate), PlaylistIndex = null };
        if (forward && allowRecommendations && source.Recommendations.Count > 0)
            return request with { Item = new(BiliMediaType.Video, source.Recommendations[0], null,
                null, null, null, null, 0, item.IsPrivate), PlaylistIndex = null };
        return null;
    }

    public static ClassicPlaybackRequest? SelectPlaylist(ClassicPlaybackRequest request, int ordinal)
        => ordinal < 0 || ordinal >= request.Playlist.Count ? null : request with
        {
            Item = request.Playlist[ordinal] with { IsPrivate = request.Item.IsPrivate || request.Playlist[ordinal].IsPrivate },
            PlaylistIndex = ordinal,
        };

    private static int? UniqueIndex<T>(IReadOnlyList<T> items, Func<T, string?> id, string? selected)
    {
        if (string.IsNullOrWhiteSpace(selected)) return null;
        int? result = null;
        for (var i = 0; i < items.Count; ++i)
        {
            if (!string.Equals(id(items[i]), selected, StringComparison.Ordinal)) continue;
            if (result is not null) return null;
            result = i;
        }
        return result;
    }
}
