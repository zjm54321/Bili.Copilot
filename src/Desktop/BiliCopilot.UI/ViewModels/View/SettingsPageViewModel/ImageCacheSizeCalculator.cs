// Copyright (c) Bili Copilot. All rights reserved.


namespace BiliCopilot.UI.ViewModels.View;

/// <summary>
/// Filesystem-only image cache scan. Errors propagate instead of reporting an empty cache.
/// </summary>
internal static class ImageCacheSizeCalculator
{
    internal static long Calculate(string cachePath)
        => Directory.EnumerateFiles(cachePath, "*.*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
}
