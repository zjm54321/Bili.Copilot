// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Controls.Settings;

namespace BiliCopilot.UI.Classic.Views;

/// <summary>
/// 经典播放器设置控件。
/// </summary>
public sealed partial class ClassicPlayerSettingControl : SettingsPageControlBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ClassicPlayerSettingControl"/> class.
    /// </summary>
    public ClassicPlayerSettingControl() => InitializeComponent();

    private void OnPickExternalPlayerClick(object sender, RoutedEventArgs e)
        => ViewModel.SelectClassicExternalPlayerCommand.Execute(default);

    private void OnClearExternalPlayerClick(object sender, RoutedEventArgs e)
        => ViewModel.ClearClassicExternalPlayerCommand.Execute(default);
}
