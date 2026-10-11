using BiliCopilot.UI.Resolvers;
using BiliCopilot.UI.ViewModels.Items;

namespace BiliCopilot.UI.ViewModels.Core;

public sealed partial class PlayerViewModel
{
    /// <summary>Replay through the same real serial boundary as media replacement.</summary>
    public Task ReplayAsync(double? preferPosition = null)
    {
        var player = Player;
        return player is null ? Task.CompletedTask
            : RunWithoutPlayerInputAsync(() => player.ReplayAsync(preferPosition));
    }

    private Task SelectSourceAsync(SourceItemViewModel source)
    {
        if (source is null || IsPlaybackClosing || IsConnecting || !Sources.Contains(source) || Player is null)
            return Task.CompletedTask;

        var player = Player;
        var position = player.Position;
        var snapshot = _snapshot;
        var resolver = _sourceResolver;
        return RunWithoutPlayerInputAsync(async () =>
        {
            if (!Sources.Contains(source)) return;
            foreach (var item in Sources) item.IsSelected = item.Equals(source);
            SelectedSource = source;
            if (resolver is VideoMediaSourceResolver videoResolver)
            {
                snapshot.PreferQuality = source.Data.Quality;
                videoResolver.ResetSnapshot(snapshot);
                // Replay itself consumes this captured position. Never save it in a VM field
                // or wait for IsPlaybackInitialized (Replay need not toggle that property).
                await player.ReplayAsync(position);
            }
        });
    }
}
