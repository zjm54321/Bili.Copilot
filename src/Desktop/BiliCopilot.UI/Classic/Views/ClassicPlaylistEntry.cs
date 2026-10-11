// Copyright (c) Bili Copilot. All rights reserved.


namespace BiliCopilot.UI.Classic.Views;

/// <summary>
/// 播放列表条目及其在 <c>Request.Playlist</c> 中的显式零基序号。
/// <c>ItemsView</c> 不提供 <c>SelectedIndex</c>，因此以显式序号绑定，
/// 保证存在重复/相等条目时仍选中正确的播放列表项。
/// </summary>
public sealed record ClassicPlaylistEntry(int Ordinal, ClassicMediaItem Item);
