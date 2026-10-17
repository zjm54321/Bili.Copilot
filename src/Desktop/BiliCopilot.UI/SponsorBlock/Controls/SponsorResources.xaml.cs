// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace BiliCopilot.UI.SponsorBlock.Controls;

/// <summary>空降助手控件的局部资源字典（每个控件各自合并，不进入全局资源）.</summary>
public sealed partial class SponsorResources : ResourceDictionary
{
    /// <summary>Initializes a new instance of the <see cref="SponsorResources"/> class.</summary>
    public SponsorResources() => InitializeComponent();
}

/// <summary>x:Bind 函数绑定辅助（编译期绑定，无反射）.</summary>
public static class SponsorXaml
{
    /// <summary>true → Visible.</summary>
    public static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>true → Collapsed.</summary>
    public static Visibility Hide(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>两个条件同时成立时显示.</summary>
    public static Visibility ShowBoth(bool first, bool second) => first && second ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>非空对象 → Visible.</summary>
    public static Visibility ShowObject(object? value) => value is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>非空文本 → Visible.</summary>
    public static Visibility ShowText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>取反.</summary>
    public static bool Not(bool value) => !value;

    /// <summary>两个条件同时成立.</summary>
    public static bool Both(bool first, bool second) => first && second;

    /// <summary>仅显示（不处理）的条目降低不透明度.</summary>
    public static double Dim(bool dimmed) => dimmed ? 0.62 : 1.0;

    /// <summary>不可用时降低不透明度.</summary>
    public static double DimUnless(bool active) => active ? 1.0 : 0.62;

    /// <summary>first 且非 second.</summary>
    public static bool AndNot(bool first, bool second) => first && !second;

    /// <summary>first 且非 second 时显示.</summary>
    public static Visibility AndNotVisible(bool first, bool second) => first && !second ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>优先显示 first，为空时使用 fallback.</summary>
    public static string Pick(string? first, string? fallback) => string.IsNullOrEmpty(first) ? fallback ?? string.Empty : first;

    /// <summary>颜色 → 画刷.</summary>
    public static Brush Brush(Color color) => new SolidColorBrush(color);
}
