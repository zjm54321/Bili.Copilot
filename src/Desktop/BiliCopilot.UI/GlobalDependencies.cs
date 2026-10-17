// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Extensions;
using BiliCopilot.UI.Resolvers;
using BiliCopilot.UI.Toolkits;
using BiliCopilot.UI.ViewModels.Components;
using BiliCopilot.UI.ViewModels.Core;
using BiliCopilot.UI.ViewModels.View;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Richasy.BiliKernel;
using Richasy.WinUIKernel.Share;
using Richasy.WinUIKernel.Share.Toolkits;
using Richasy.WinUIKernel.Share.ViewModels;
using RichasyKernel;
using Serilog;
using System.Diagnostics.CodeAnalysis;

namespace BiliCopilot.UI;

internal static class GlobalDependencies
{
    /// <summary>
    /// 获取服务提供程序.
    /// </summary>
    public static Kernel Kernel { get; private set; }

    public static void Initialize()
    {
        if (Kernel is not null)
        {
            return;
        }

        Kernel = Kernel.CreateBuilder()
            .AddSerilog()
            .AddDispatcherQueue()
            .AddShareToolkits()
            .AddXamlRootProvider()

            .AddBiliClient()
            .AddBiliAuthenticator()
            .AddWinUICookiesResolver()
            .AddWinUITokenResolver()
            .AddWinUIQRCodeResolver(RenderQRCodeAsync)
            .AddTVAuthenticationService()
            .AddMyProfileService()
            .AddUserService()
            .AddSingleton<Services.UserSpace.UserSpaceService>()
            .AddRelationshipService()
            .AddViewLaterService()
            .AddViewHistoryService()
            .AddCommentService()
            .AddVideoDiscoveryService()
            .AddLiveDiscoveryService()
            .AddEntertainmentDiscoveryService()
            .AddArticleDiscoveryService()
            .AddSingleton<Services.Articles.ArticleRawHtmlClient>()
            .AddSingleton<Services.Articles.ArticleDetailReader>()
            .AddArticleOperationService()
            .AddMomentDiscoveryService()
            .AddMomentOperationService()
            .AddMessageService()
            .AddMessageNotifications()
            .AddFavoriteService()
            .AddSearchService()
            .AddDanmakuService()
            .AddSubtitleService()
            .AddPlayerService()

            .AddTransient<CommentMainViewModel>()
            .AddSingleton<PinnerViewModel>()
            .AddSingleton<AppViewModel>()
            .AddSingleton<BiliCopilot.UI.Live.Application.LiveApplicationBridge>()
            .AddSingleton<BiliCopilot.UI.Classic.ClassicPlayerLauncher>()
            .AddSingleton<EmoteModuleViewModel>()
            .AddSingleton<NavigationViewModel>()
            .AddSingleton<StartupPageViewModel>()
            .AddSingleton<AccountViewModel>()
            .AddSingleton<SearchBoxViewModel>()
            .AddSingleton<PopularPageViewModel>()
            .AddSingleton<MomentPageViewModel>()
            .AddSingleton<VideoPartitionPageViewModel>()
            .AddSingleton<LivePartitionPageViewModel>()
            .AddSingleton<ArticlePartitionPageViewModel>()
            .AddSingleton<AnimePageViewModel>()
            .AddSingleton<CinemaPageViewModel>()
            .AddSingleton<FansPageViewModel>()
            .AddSingleton<FollowsPageViewModel>()
            .AddSingleton<SearchPageViewModel>()
            .AddSingleton<ArticleReaderPageViewModel>()
            .AddSingleton<ViewLaterPageViewModel>()
            .AddSingleton<HistoryPageViewModel>()
            .AddSingleton<FavoritesPageViewModel>()
            .AddSingleton<MessagePageViewModel>()
            .AddSingleton<SettingsPageViewModel>()
            .AddSingleton<BiliCopilot.UI.SponsorBlock.Content.SponsorContentProjectionService>()
            .AddSingleton<BiliCopilot.UI.SponsorBlock.Integration.SponsorRuntime>()
            .AddSingleton<UserSpacePageViewModel>()
            .AddSingleton<NotificationViewModel>()
            .AddTransient<UserMomentDetailViewModel>()
            .AddTransient<VideoMediaSourceResolver>()
            .AddTransient<VideoMediaHistoryResolver>()
            .AddTransient<PgcMediaSourceResolver>()
            .AddTransient<PgcMediaHistoryResolver>()
            .AddTransient<PlayerViewModel>()
            .AddTransient<DanmakuSendViewModel>()
            .AddNotificationViewModel()
            .Build();

        Kernel.InitializeShareKernel(Kernel.Get<ILogger<App>>());
    }

    public static IKernelBuilder AddDispatcherQueue(this IKernelBuilder builder)
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        builder.Services.AddSingleton(queue);
        return builder;
    }

    public static IKernelBuilder AddMessageNotifications(this IKernelBuilder builder)
    {
        builder.Services.AddSingleton<Services.Messages.IMessageNotificationService, Services.Messages.MessageNotificationService>();
        return builder;
    }

    public static IKernelBuilder AddNotificationViewModel(this IKernelBuilder builder)
    {
        builder.Services.AddSingleton<INotificationViewModel, NotificationViewModel>();
        return builder;
    }

    public static IKernelBuilder AddShareToolkits(this IKernelBuilder builder)
    {
        builder.Services.AddSingleton<IAppToolkit, AppToolkit>()
            .AddSingleton<ISettingsToolkit, SettingsToolkit>()
            .AddSingleton<IFileToolkit, FileToolkit>()
            .AddSingleton<IResourceToolkit, ResourceToolkit>()
            .AddSingleton<IFontToolkit, SharedFontToolkit>();
        return builder;
    }

    public static IKernelBuilder AddXamlRootProvider(this IKernelBuilder builder)
    {
        builder.Services.AddSingleton<IXamlRootProvider, XamlRootProvider>();
        return builder;
    }

    public static IKernelBuilder AddSingleton<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(this IKernelBuilder kernelBuilder)
        where T : class
    {
        kernelBuilder.Services.AddSingleton<T>();
        return kernelBuilder;
    }

    public static IKernelBuilder AddTransient<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(this IKernelBuilder kernelBuilder)
        where T : class
    {
        kernelBuilder.Services.AddTransient<T>();
        return kernelBuilder;
    }

    public static RichasyKernel.IKernelBuilder AddSerilog(this RichasyKernel.IKernelBuilder builder)
    {
        var loggerPath = Path.Combine(Microsoft.Windows.Storage.ApplicationData.GetDefault().LocalFolder.Path, "Logger");
        if (!Directory.Exists(loggerPath))
        {
            Directory.CreateDirectory(loggerPath);
        }

        // Create a logger with current date.
        Log.Logger = new LoggerConfiguration()
            .WriteTo.File(Path.Combine(loggerPath, $"log-{DateTimeOffset.Now:yyyy-MM-dd}.txt"))
            .CreateLogger();

        builder.Services.AddLogging(b => b.AddSerilog(dispose: true));
        return builder;
    }

    public static T Get<T>(this object ele)
        where T : class
        => Kernel.GetRequiredService<T>();

    private static Task RenderQRCodeAsync(byte[] imageData)
    {
        var vm = Kernel.GetRequiredService<StartupPageViewModel>();
        vm.RenderQRCodeCommand.Execute(imageData);
        return Task.CompletedTask;
    }
}
