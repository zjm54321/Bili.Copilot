// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Models.Constants;
using BiliCopilot.UI.Pages.Overlay;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Core;
using CommunityToolkit.Mvvm.Input;
using Humanizer;
using Richasy.BiliKernel.Models.Article;
using Richasy.BiliKernel.Models.Media;
using Richasy.BiliKernel.Models.User;
using Richasy.WinUIKernel.Share.ViewModels;
using Windows.System;
using WinRT;

namespace BiliCopilot.UI.ViewModels.Items;

/// <summary>
/// 通知消息项视图模型.
/// </summary>
[GeneratedBindableCustomProperty]
public sealed partial class NotifyMessageItemViewModel : ViewModelBase<NotifyMessage>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NotifyMessageItemViewModel"/> class.
    /// </summary>
    public NotifyMessageItemViewModel(NotifyMessage data)
        : base(data)
    {
        PublishRelativeTime = data.PublishTime.Humanize(default, new System.Globalization.CultureInfo("zh-CN"));
        FirstUserAvatar = data.Users?.First().Avatar.Uri;
        FirstUserName = data.Users?.First().Name;
        IsMultipleUsers = data.Users.Count > 1;
        Message = data.Message;
        SourceContent = data.SourceContent;
        if (data.Type == NotifyMessageType.Like)
        {
            var count = data.Properties["Count"];
            Subtitle = IsMultipleUsers
                ? string.Format(
                    ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.LikeMessageMultipleDescription),
                    FirstUserName,
                    data.Users[1].Name,
                    count,
                    data.Business)
                : string.Format(
                    ResourceToolkit.GetLocalizedString(Models.Constants.StringNames.LikeMessageSingleDescription),
                    FirstUserName,
                    data.Business);
        }
    }

    [RelayCommand]
    private async Task ActiveAsync()
    {
        var target = NotifyMessageTarget.Resolve(Data);
        if (target.Kind == NotifyMessageTargetKind.Video && target.Id is string videoId)
        {
            // Use the same player command as home/search video cards, including player ownership.
            var video = new VideoInformation(new MediaIdentifier(videoId, default, default), default);
            new VideoItemViewModel(video, VideoCardStyle.Search).PlayCommand.Execute(default);
            return;
        }

        if (target.Kind == NotifyMessageTargetKind.Article && target.Id is string articleId)
        {
            this.Get<NavigationViewModel>().NavigateToOver(typeof(ArticleReaderPage), new ArticleIdentifier(articleId, SourceContent, default, default));
            return;
        }

        if (target.WebUri is Uri uri)
        {
            await Launcher.LaunchUriAsync(uri).AsTask();
        }
    }
}
