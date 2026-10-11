// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Classic;
using BiliCopilot.UI.ViewModels.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Richasy.WinUIKernel.Share.Toolkits;

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>
/// 设置页面视图模型 —— 经典播放器设置部分。
/// 读取时不做迁移、不写回、不切换当前播放引擎；写入仅在初始化完成后进行。
/// </summary>
public sealed partial class SettingsPageViewModel
{
    // 加载保护：初始化期间赋值只读入，不写回设置。
    private bool _classicInitialized;

    [ObservableProperty]
    private PlaybackExperience _playerExperience;

    [ObservableProperty]
    private ClassicBackendKind _classicPlayerBackend;

    [ObservableProperty]
    private ClassicDisplayMode _classicPlayerDisplayMode;

    [ObservableProperty]
    private ClassicDecodePreference _classicPlayerDecodePreference;

    [ObservableProperty]
    private string _classicExternalPlayerPath = string.Empty;

    [ObservableProperty]
    private bool _classicUseWebPlayerWhenLive;

    [ObservableProperty]
    private List<PlaybackExperience> _playbackExperienceCollection = [];

    [ObservableProperty]
    private List<ClassicBackendKind> _classicBackendCollection = [];

    [ObservableProperty]
    private List<ClassicDisplayMode> _classicDisplayModeCollection = [];

    [ObservableProperty]
    private List<ClassicDecodePreference> _classicDecodeCollection = [];

    /// <summary>是否已设置外部播放器路径。</summary>
    public bool HasClassicExternalPlayerPath => !string.IsNullOrEmpty(ClassicExternalPlayerPath);

    // 初始化挂接：由 InitializeAsync 调用一次。
    private void InitializeClassicSettings()
    {
        if (_classicInitialized)
        {
            return;
        }

        PlaybackExperienceCollection = [.. Enum.GetValues<PlaybackExperience>()];
        ClassicBackendCollection = [.. Enum.GetValues<ClassicBackendKind>()];
        ClassicDisplayModeCollection = [.. Enum.GetValues<ClassicDisplayMode>()];
        ClassicDecodeCollection = [.. Enum.GetValues<ClassicDecodePreference>()];

        // 读取使用与 ClassicSessionOptions.Capture 相同的字符串键，不迁移旧键。
        var settings = this.Get<ISettingsToolkit>();
        PlayerExperience = ClassicSessionOptions.ReadExperience(settings);
        ClassicPlayerBackend = settings.ReadLocalSetting("ClassicPlayerBackend", ClassicBackendKind.Island);
        ClassicPlayerDisplayMode = settings.ReadLocalSetting("ClassicPlayerDisplayMode", ClassicDisplayMode.Default);
        ClassicPlayerDecodePreference = settings.ReadLocalSetting("ClassicPlayerDecodePreference", ClassicDecodePreference.Auto);
        ClassicExternalPlayerPath = settings.ReadLocalSetting("ClassicExternalPlayerPath", string.Empty) ?? string.Empty;
        ClassicUseWebPlayerWhenLive = settings.ReadLocalSetting("ClassicUseWebPlayerWhenLive", false);

        _classicInitialized = true;
    }

    [RelayCommand]
    private async Task SelectClassicExternalPlayerAsync()
    {
        var file = await this.Get<IFileToolkit>().PickFileAsync(".exe", this.Get<AppViewModel>().ActivatedWindow);
        if (file is not null)
        {
            ClassicExternalPlayerPath = file.Path;
        }
    }

    [RelayCommand]
    private void ClearClassicExternalPlayer() => ClassicExternalPlayerPath = string.Empty;

    partial void OnPlayerExperienceChanged(PlaybackExperience value)
        => WriteClassicSetting("PlaybackExperience", value);

    partial void OnClassicPlayerBackendChanged(ClassicBackendKind value)
        => WriteClassicSetting("ClassicPlayerBackend", value);

    partial void OnClassicPlayerDisplayModeChanged(ClassicDisplayMode value)
        => WriteClassicSetting("ClassicPlayerDisplayMode", value);

    partial void OnClassicPlayerDecodePreferenceChanged(ClassicDecodePreference value)
        => WriteClassicSetting("ClassicPlayerDecodePreference", value);

    partial void OnClassicExternalPlayerPathChanged(string value)
    {
        WriteClassicSetting("ClassicExternalPlayerPath", value ?? string.Empty);
        OnPropertyChanged(nameof(HasClassicExternalPlayerPath));
    }

    partial void OnClassicUseWebPlayerWhenLiveChanged(bool value)
        => WriteClassicSetting("ClassicUseWebPlayerWhenLive", value);

    // 初始化保护：加载阶段的赋值不写回，避免初始化写回。
    private void WriteClassicSetting<T>(string key, T value)
    {
        if (!_classicInitialized)
        {
            return;
        }

        this.Get<ISettingsToolkit>().WriteLocalSetting(key, value);
    }
}
