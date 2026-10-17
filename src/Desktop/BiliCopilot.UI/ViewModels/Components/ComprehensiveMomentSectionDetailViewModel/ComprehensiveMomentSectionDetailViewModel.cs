// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Items;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Richasy.BiliKernel.Bili.Moment;

namespace BiliCopilot.UI.ViewModels.Components;

/// <summary>
/// 综合动态分区详情视图模型.
/// </summary>
public sealed partial class ComprehensiveMomentSectionDetailViewModel : LayoutPageViewModelBase, IMomentSectionDetailViewModel
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ComprehensiveMomentSectionDetailViewModel"/> class.
    /// </summary>
    public ComprehensiveMomentSectionDetailViewModel(
        IMomentDiscoveryService service)
    {
        _service = service;
        _logger = this.Get<ILogger<VideoMomentSectionDetailViewModel>>();
        _account = this.Get<AccountViewModel>();
        _accountGeneration = _account.AccountGeneration;
    }

    /// <inheritdoc/>
    protected override string GetPageKey()
        => "ComprehensiveMoment";

    [RelayCommand]
    private async Task InitializeAsync()
    {
        if (IsLoading || _isRefreshing) return;
        if (Upers.Count > 0 && _accountGeneration == _account.AccountGeneration)
        {
            if (SelectedUper is not null) await SelectedUper.InitializeCommand.ExecuteAsync(null);
            return;
        }

        await RebuildAsync();
    }

    private async Task RebuildAsync()
    {
        _isRebuilding = true;
        IsLoading = true;
        ++_selectionGeneration;
        try
        {
            // Retire all old rows first; reset SDK cursors only after real requests drain.
            await Task.WhenAll(Upers.Select(uper => uper.RetireAsync()).ToArray());
            SelectedUper = default;
            Upers.Clear();
            _accountGeneration = _account.AccountGeneration;
            var total = new MomentUperSectionViewModel(default, RefreshCommand);
            Upers.Add(total);
            await LoadUpersAsync(total);
            if (_accountGeneration != _account.AccountGeneration) return;
            SelectedUper = total;
            IsEmpty = total.Items.Count == 0;
            total.NotifyListUpdated();
            Initialized?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _isRebuilding = false;
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading || _isRefreshing) return;
        _isRefreshing = true;
        ++_selectionGeneration;
        try
        {
            if (_accountGeneration == _account.AccountGeneration && SelectedUper is { IsTotal: false } selected)
            {
                await selected.RefreshCommand.ExecuteAsync(null);
                IsEmpty = selected.IsEmpty;
            }
            else await RebuildAsync();
        }
        finally { _isRefreshing = false; }
    }

    [RelayCommand]
    private async Task SelectUperAsync(MomentUperSectionViewModel? item)
    {
        if (item is null || _isRebuilding || _isRefreshing || !Upers.Any(uper => ReferenceEquals(uper, item)))
        {
            return;
        }

        if (_accountGeneration != _account.AccountGeneration)
        {
            await RebuildAsync();
            return;
        }
        var generation = ++_selectionGeneration;
        var drains = Upers.Where(uper => !ReferenceEquals(uper, item)).Select(uper => uper.CancelAndDrainAsync()).ToArray();
        await Task.WhenAll(drains);
        if (generation != _selectionGeneration || _isRebuilding || _isRefreshing
            || _accountGeneration != _account.AccountGeneration) return;
        SelectedUper = item;
        await item.InitializeCommand.ExecuteAsync(null);
    }

    private async Task LoadUpersAsync(MomentUperSectionViewModel total)
    {
        try
        {
            var generation = _account.AccountGeneration;
            var token = _account.AccountCancellationToken;
            var response = await _service.GetComprehensiveMomentsAsync(default, default, token);
            if (token.IsCancellationRequested || generation != _account.AccountGeneration) return;
            var upers = response.Users;
            if (upers?.Count > 0)
            {
                foreach (var item in upers)
                {
                    if (token.IsCancellationRequested || generation != _account.AccountGeneration) return;
                    Upers.Add(new MomentUperSectionViewModel(item));
                }
            }

            total.InjectFirstPageData(response);
        }
        catch (OperationCanceledException) when (_account.AccountCancellationToken.IsCancellationRequested
            || _accountGeneration != _account.AccountGeneration) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "尝试获取综合动态时发生错误.");
        }
    }
}
