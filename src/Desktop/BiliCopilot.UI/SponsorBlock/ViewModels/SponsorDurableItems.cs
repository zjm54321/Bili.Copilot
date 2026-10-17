// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.Rules;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.UI;

namespace BiliCopilot.UI.SponsorBlock.ViewModels;

/// <summary>
/// 白名单中的一个 UP 主 UID（已保存在本机的规范数字 UID）。移除需要行内确认；
/// 实际写入由设置视图模型以当前修订号显式发起。
/// </summary>
public sealed partial class SponsorWhitelistItem : ObservableObject
{
    private readonly Action<SponsorWhitelistItem> _remove;
    private readonly Action<SponsorWhitelistItem> _requestConfirm;

    [ObservableProperty]
    private bool _isConfirmVisible;

    internal SponsorWhitelistItem(string uid, Action<SponsorWhitelistItem> requestConfirm, Action<SponsorWhitelistItem> remove)
    {
        Uid = uid;
        _requestConfirm = requestConfirm;
        _remove = remove;
    }

    /// <summary>UP 主 UID（规范十进制数字）.</summary>
    public string Uid { get; }

    /// <summary>辅助功能名称.</summary>
    public string AutomationName => "UP 主 UID " + Uid;

    [RelayCommand]
    private void RequestRemove() => _requestConfirm(this);

    [RelayCommand]
    private void CancelRemove() => IsConfirmVisible = false;

    [RelayCommand]
    private void ConfirmRemove()
    {
        IsConfirmVisible = false;
        _remove(this);
    }
}

/// <summary>过滤偏好中的一个分类开关（分类来自唯一分类表）。只修改界面上的草稿，保存需显式操作.</summary>
public sealed partial class SponsorFilterCategoryItem : ObservableObject
{
    private readonly Action _changed;
    private bool _projecting;

    [ObservableProperty]
    private bool _isChecked;

    internal SponsorFilterCategoryItem(SponsorCategory category, Action changed)
    {
        Id = category.Id;
        Title = SponsorUiText.CategoryTitle(category.Id);
        CategoryColor = SponsorUiText.CategoryColor(category.Id);
        _changed = changed;
    }

    /// <summary>分类 ID（不显示给用户）.</summary>
    public string Id { get; }

    /// <summary>中文名称.</summary>
    public string Title { get; }

    /// <summary>分类颜色.</summary>
    public Color CategoryColor { get; }

    internal void Project(bool value)
    {
        _projecting = true;
        try
        {
            IsChecked = value;
        }
        finally
        {
            _projecting = false;
        }
    }

    partial void OnIsCheckedChanged(bool value)
    {
        if (!_projecting)
        {
            _changed();
        }
    }
}

/// <summary>
/// 一条本机操作记录的只读显示行。只有文字，没有任何“重发/重放”入口；
/// 记录不会被解析成可发送的请求。
/// </summary>
public sealed class SponsorJournalItem
{
    internal SponsorJournalItem(string key, string title, string detail, string state, bool needsAttention)
    {
        Key = key;
        Title = title;
        Detail = detail;
        StateText = state;
        NeedsAttention = needsAttention;
    }

    /// <summary>稳定键（记录 ID，不显示）.</summary>
    public string Key { get; }

    /// <summary>操作名称.</summary>
    public string Title { get; }

    /// <summary>对象说明（分类、投票方向等，不含身份或网址）.</summary>
    public string Detail { get; }

    /// <summary>结果状态.</summary>
    public string StateText { get; }

    /// <summary>结果未知或未写入本机.</summary>
    public bool NeedsAttention { get; }
}
