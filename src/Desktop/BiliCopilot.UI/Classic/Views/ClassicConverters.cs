// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.UI.Xaml.Data;

namespace BiliCopilot.UI.Classic.Views;

/// <summary>
/// 经典播放器前端显示转换器集合。仅做展示映射，不读取或写入设置，也不创建引擎/进程。
/// </summary>
internal sealed partial class ClassicExperienceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is PlaybackExperience experience
            ? experience == PlaybackExperience.Classic ? "经典播放器" : "新播放器"
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
}

internal sealed partial class ClassicBackendKindConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is ClassicBackendKind kind
            ? kind switch
            {
                ClassicBackendKind.Island => "岛式窗口",
                ClassicBackendKind.Native => "原生窗口",
                ClassicBackendKind.External => "外部播放器",
                ClassicBackendKind.Web => "网页播放器",
                _ => kind.ToString(),
            }
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
}

internal sealed partial class ClassicDisplayModeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is ClassicDisplayMode mode
            ? mode switch
            {
                ClassicDisplayMode.Default => "默认",
                ClassicDisplayMode.FullScreen => "全屏",
                ClassicDisplayMode.FullWindow => "全窗口",
                ClassicDisplayMode.CompactOverlay => "紧凑覆盖",
                ClassicDisplayMode.NewWindow => "新窗口",
                _ => mode.ToString(),
            }
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
}

internal sealed partial class ClassicDecodePreferenceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is ClassicDecodePreference preference
            ? preference switch
            {
                ClassicDecodePreference.Auto => "自动",
                ClassicDecodePreference.Software => "软件解码",
                ClassicDecodePreference.D3D11 => "D3D11",
                _ => preference.ToString(),
            }
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
}

/// <summary>
/// 判断能力标志位是否可用（<see cref="ClassicCapabilities"/> 为 Flags，ConverterParameter 为标志名）。
/// </summary>
internal sealed partial class ClassicCapabilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is ClassicCapabilities capabilities
            && parameter is string name
            && Enum.TryParse<ClassicCapabilities>(name, out var flag)
            && (capabilities & flag) == flag;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
}

/// <summary>
/// 仅在经典播放体验下显示经典专属设置。
/// </summary>
internal sealed partial class ClassicExperienceToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is PlaybackExperience.Classic ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
}

/// <summary>
/// bool 到 Visibility 的展示映射（true = Visible），用于布局状态显隐。
/// </summary>
internal sealed partial class ClassicBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
}

/// <summary>
/// 外部播放器/网页播放器由各自宿主自行控制播放，需要额外说明。
/// </summary>
internal sealed partial class ClassicBackendSelfControlConverter : IValueConverter
{
    public static bool IsSelfControlled(object value)
        => value is ClassicBackendKind.External or ClassicBackendKind.Web;

    public object Convert(object value, Type targetType, object parameter, string language)
        => IsSelfControlled(value);

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
}

internal sealed partial class ClassicBackendSelfControlTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is ClassicBackendKind kind
            ? kind switch
            {
                ClassicBackendKind.External => "播放由外部播放器自行控制，暂停、进度、音量与画质可能无法从本应用调整。",
                ClassicBackendKind.Web => "播放由网页播放器自行控制，本应用仅提供页面地址，部分控制可能不可用。",
                _ => string.Empty,
            }
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
}

internal sealed partial class ClassicPlaybackStateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is ClassicPlaybackState state
            ? state switch
            {
                ClassicPlaybackState.Idle => "空闲",
                ClassicPlaybackState.Loading => "加载中",
                ClassicPlaybackState.Playing => "播放中",
                ClassicPlaybackState.Paused => "已暂停",
                ClassicPlaybackState.Ended => "已结束",
                ClassicPlaybackState.Failed => "播放失败",
                ClassicPlaybackState.Closing => "正在关闭",
                ClassicPlaybackState.Closed => "已关闭",
                _ => state.ToString(),
            }
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
}

/// <summary>
/// 按播放状态切换可见性；State 为要匹配的状态名，IsReverse 为反转。
/// </summary>
internal sealed partial class ClassicPlaybackStateVisibilityConverter : IValueConverter
{
    public string State { get; set; } = string.Empty;

    public bool IsReverse { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var matched = value is ClassicPlaybackState state
            && Enum.TryParse<ClassicPlaybackState>(State, out var expected)
            && state == expected;
        return (matched ^ IsReverse) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
}

/// <summary>
/// 播放列表条目标题：优先视频、剧集、直播标题，其次季标题。
/// </summary>
internal sealed partial class ClassicMediaItemTitleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is ClassicMediaItem item
            ? item.Video?.Identifier.Title ?? item.Episode?.Identifier.Title ?? item.Live?.Identifier.Title ?? item.Season?.Identifier.Title ?? string.Empty
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotImplementedException();
}
