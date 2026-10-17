// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Items;

namespace BiliCopilot.UI.Controls.Moment;

/// <summary>
/// 综合动态分区主体.
/// </summary>
public sealed partial class ComprehensiveMainBody : MomentUperSectionControlBase
{
    private MomentUperSectionViewModel? _observed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ComprehensiveMainBody"/> class.
    /// </summary>
    public ComprehensiveMainBody()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args) => BindModel(ViewModel);
    private void OnUnloaded(object sender, RoutedEventArgs args) => BindModel(null);

    /// <inheritdoc/>
    protected override void OnViewModelChanged(MomentUperSectionViewModel? oldValue, MomentUperSectionViewModel? newValue)
        => BindModel(newValue);

    private void BindModel(MomentUperSectionViewModel? model)
    {
        if (_observed is not null) _observed.ListUpdated -= OnListUpdatedAsync;
        _observed = IsLoaded ? model : null;
        if (_observed is not null)
        {
            _observed.ListUpdated += OnListUpdatedAsync;
            // Seeded first-page data can precede the content template's Loaded event.
            OnListUpdatedAsync(_observed, EventArgs.Empty);
        }
    }

    private async void OnListUpdatedAsync(object? sender, EventArgs e)
    {
        if (!IsLoaded || !ReferenceEquals(sender, _observed)) return;
        await View.DelayCheckItemsAsync();
    }
}
