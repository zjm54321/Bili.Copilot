// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Rules;
using CommunityToolkit.Mvvm.ComponentModel;
using Windows.UI;

namespace BiliCopilot.UI.SponsorBlock.ViewModels;

/// <summary>
/// 一个分类的处理方式。行数据完全来自唯一分类表 <see cref="SponsorRules.Categories"/>，
/// 可选值来自 <see cref="SponsorApplicationCoordinator.CategoryPolicyChoices"/>。
/// </summary>
public sealed partial class SponsorCategoryPolicyItem : ObservableObject
{
    private readonly Action<SponsorCategoryPolicyItem, CategoryPolicy> _changed;
    private bool _projecting;

    [ObservableProperty]
    private int _selectedIndex = -1;

    internal SponsorCategoryPolicyItem(SponsorCategory category, Action<SponsorCategoryPolicyItem, CategoryPolicy> changed)
    {
        Id = category.Id;
        Title = SponsorUiText.CategoryTitle(category.Id);
        Description = SponsorUiText.CategoryDescription(category.Id);
        ActionsText = "可用于：" + string.Join("、", category.AllowedActions.Select(SponsorUiText.ActionTitle));
        CategoryColor = SponsorUiText.CategoryColor(category.Id);
        DefaultText = "默认：" + SponsorUiText.PolicyTitle(category.DefaultPolicy);
        _changed = changed;
    }

    /// <summary>分类 ID（仅用于自动化名称和调试，不显示给用户）.</summary>
    public string Id { get; }

    /// <summary>中文名称.</summary>
    public string Title { get; }

    /// <summary>说明.</summary>
    public string Description { get; }

    /// <summary>允许的操作.</summary>
    public string ActionsText { get; }

    /// <summary>默认处理方式.</summary>
    public string DefaultText { get; }

    /// <summary>分类颜色.</summary>
    public Color CategoryColor { get; }

    /// <summary>处理方式选项.</summary>
    public IReadOnlyList<string> PolicyLabels { get; } =
        SponsorApplicationCoordinator.CategoryPolicyChoices.Select(SponsorUiText.PolicyTitle).ToList();

    internal void Project(CategoryPolicy policy)
    {
        _projecting = true;
        try
        {
            SelectedIndex = SponsorApplicationCoordinator.CategoryPolicyChoices.IndexOf(policy);
        }
        finally
        {
            _projecting = false;
        }
    }

    partial void OnSelectedIndexChanged(int value)
    {
        var choices = SponsorApplicationCoordinator.CategoryPolicyChoices;
        if (!_projecting && value >= 0 && value < choices.Length)
        {
            _changed(this, choices[value]);
        }
    }
}
