// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.ViewModels;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace BiliCopilot.UI.SponsorBlock.Controls;

/// <summary>
/// 片段时间轴标记条。宿主把它放在现有进度条轨道的同一网格单元内（高度 4，水平拉伸，与轨道左右对齐）。
/// 只绘制，不处理输入；位置以实际时长为基准，绝不插值或伪造进度。
/// </summary>
public sealed partial class SponsorTimelineBar : UserControl
{
    /// <summary>ViewModel 依赖属性.</summary>
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(SponsorPlaybackViewModel), typeof(SponsorTimelineBar), new PropertyMetadata(default(SponsorPlaybackViewModel), OnViewModelChanged));

    private SponsorPlaybackViewModel? _subscribed;

    /// <summary>Initializes a new instance of the <see cref="SponsorTimelineBar"/> class.</summary>
    public SponsorTimelineBar()
    {
        InitializeComponent();
        Loaded += (_, _) => Subscribe(ViewModel);
        Unloaded += (_, _) => Subscribe(null);
    }

    /// <summary>播放视图模型.</summary>
    public SponsorPlaybackViewModel? ViewModel
    {
        get => (SponsorPlaybackViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (SponsorTimelineBar)d;
        if (bar.IsLoaded)
        {
            bar.Subscribe(e.NewValue as SponsorPlaybackViewModel);
        }
    }

    private void Subscribe(SponsorPlaybackViewModel? vm)
    {
        if (_subscribed is not null)
        {
            _subscribed.MarkersChanged -= OnMarkersChanged;
        }

        _subscribed = vm;
        if (vm is not null)
        {
            vm.MarkersChanged += OnMarkersChanged;
        }

        Render();
    }

    private void OnMarkersChanged(object? sender, EventArgs e) => Render();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Render();

    private void Render()
    {
        MarkerCanvas.Children.Clear();
        var markers = _subscribed?.Markers ?? [];
        var width = ActualWidth;
        var height = ActualHeight;
        AutomationProperties.SetName(this, markers.Count > 0 ? $"空降助手片段标记 {markers.Count} 个" : string.Empty);
        if (markers.Count == 0 || width <= 0 || height <= 0)
        {
            return;
        }

        foreach (var marker in markers)
        {
            var left = marker.StartRatio * width;
            var w = marker.IsPoint ? 3 : Math.Max(2, (marker.EndRatio - marker.StartRatio) * width);
            if (marker.IsPoint)
            {
                left = Math.Clamp(left - 1.5, 0, Math.Max(0, width - w));
            }

            var rect = new Rectangle
            {
                Width = w,
                Height = marker.IsPoint ? height + 4 : height,
                RadiusX = 1,
                RadiusY = 1,
                Fill = new SolidColorBrush(marker.CategoryColor),
                Opacity = marker.IsDimmed ? 0.45 : 0.95,
            };
            Canvas.SetLeft(rect, left);
            Canvas.SetTop(rect, marker.IsPoint ? -2 : 0);
            MarkerCanvas.Children.Add(rect);
        }
    }
}
