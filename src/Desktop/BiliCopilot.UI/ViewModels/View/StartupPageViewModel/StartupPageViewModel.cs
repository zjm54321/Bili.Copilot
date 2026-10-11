// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.ViewModels.Core;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Richasy.BiliKernel.Bili.Authorization;
using Richasy.WinUIKernel.Share.Toolkits;
using Richasy.WinUIKernel.Share.ViewModels;

namespace BiliCopilot.UI.ViewModels.View;

/// <summary>
/// 启动页视图模型.
/// </summary>
public sealed partial class StartupPageViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StartupPageViewModel"/> class.
    /// </summary>
    public StartupPageViewModel(
        IAuthenticationService authenticationService,
        ILogger<StartupPageViewModel> logger,
        DispatcherQueue dispatcherQueue)
    {
        _authenticationService = authenticationService;
        _logger = logger;
        _dispatcherQueue = dispatcherQueue;
    }

    /// <summary>
    /// 初始化视图模型.
    /// </summary>
    /// <returns><see cref="Task"/>.</returns>
    [RelayCommand]
    private async Task InitializeAsync(Image qrcodeImageControl)
    {
        Version = this.Get<IAppToolkit>().GetPackageVersion();
        QRCodeImage = qrcodeImageControl;
        await ReloadQRCodeAsync();
    }

    [RelayCommand]
    private async Task ReloadQRCodeAsync()
    {
        if (_isExiting)
        {
            return;
        }

        var previousAttempt = _loginAttempt;
        var attempt = new StartupLoginAttempt(ex => _logger.LogInformation(ex, "取消登录时出现异常."));
        var cancellationToken = attempt.Token;
        _loginAttempt = attempt;
        previousAttempt?.Cancel();
        try
        {
            IsQRCodeLoading = true;
            ErrorTip = string.Empty;
            await _authenticationService.SignInAsync(cancellationToken: cancellationToken);
            if (!IsCurrentAttempt(attempt))
            {
                return;
            }

            var isSignedIn = await CheckAuthorizeStatusAsync(cancellationToken);
            if (!IsCurrentAttempt(attempt))
            {
                return;
            }

            if (isSignedIn)
            {
                await ExitAsync(true);
            }
            else
            {
                _logger.LogWarning("未能成功获取授权信息，扫码可能出现了异常.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Both a normal return and an exception are valid cancellation outcomes.
        }
        catch (Exception ex)
        {
            if (IsCurrentAttempt(attempt))
            {
                ErrorTip = ex.Message;
            }

            _logger.LogInformation(ex, "登录过程中出现异常.");
        }
        finally
        {
            if (ReferenceEquals(_loginAttempt, attempt))
            {
                if (IsCurrentAttempt(attempt))
                {
                    IsQRCodeLoading = false;
                }

                _loginAttempt = null;
            }

            attempt.Complete();
        }
    }

    [RelayCommand]
    private Task ExitAsync(bool shouldRestart)
    {
        if (_isExiting)
        {
            return Task.CompletedTask;
        }

        _isExiting = true;
        var attempt = _loginAttempt;
        _loginAttempt = null;
        attempt?.Cancel();

        if (shouldRestart)
        {
            this.Get<AppViewModel>().RestartCommand.Execute(default);
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private void RenderQRCode(byte[] imageData)
    {
        var attempt = _loginAttempt;
        if (attempt is null || !IsCurrentAttempt(attempt))
        {
            return;
        }

        if (QRCodeImage is null)
        {
            throw new InvalidOperationException("二维码图片控件尚未就绪，请初始化模块.");
        }

        _dispatcherQueue.TryEnqueue(async () =>
        {
            if (!IsCurrentAttempt(attempt))
            {
                return;
            }

            using var stream = new MemoryStream(imageData);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream()).AsTask();
            if (!IsCurrentAttempt(attempt))
            {
                return;
            }

            QRCodeImage.Source = bitmap;
            IsQRCodeLoading = false;
        });
    }

    private bool IsCurrentAttempt(StartupLoginAttempt attempt)
        => !_isExiting && ReferenceEquals(_loginAttempt, attempt) && !attempt.Token.IsCancellationRequested;

    private async Task<bool> CheckAuthorizeStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (this.Get<IBiliTokenResolver>().GetToken() is null)
            {
                return false;
            }

            await _authenticationService.EnsureTokenAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "检查授权状态时出现异常.");
        }

        return false;
    }
}
