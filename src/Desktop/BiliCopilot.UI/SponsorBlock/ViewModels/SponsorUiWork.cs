// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.SponsorBlock.Integration;

namespace BiliCopilot.UI.SponsorBlock.ViewModels;

/// <summary>
/// 界面命令任务登记：把视图模型真正创建的任务交给应用唯一的空降助手运行时
/// (<c>SponsorRuntime.TrackUiWork</c>)，让全局关闭时能等待它结束。
/// 返回的永远是同一个任务，不包装、不调度、不重试。
/// </summary>
/// <remarks>
/// 运行时是应用依赖容器中已登记的单例（由它创建并绑定这些视图模型），这里沿用应用现有的
/// <c>this.Get&lt;T&gt;()</c> 取法，不创建第二个协调器。没有应用容器（设计器/独立预览）时不登记，
/// 此时也不存在需要排空的全局关闭。登记的任务不得等待运行时 Close/Wait。
/// </remarks>
internal static class SponsorUiWork
{
    internal static Task Track(object owner, Task actualTask)
    {
        ArgumentNullException.ThrowIfNull(actualTask);
        Register(owner, actualTask);
        // T5-10: Register precedes returning the identical operation task; registered UI runners never join their own Runtime.Close/Wait.
        #pragma warning disable VSTHRD003
        return actualTask;
        #pragma warning restore VSTHRD003
    }

    internal static Task<T> Track<T>(object owner, Task<T> actualTask)
    {
        ArgumentNullException.ThrowIfNull(actualTask);
        Register(owner, actualTask);
        // T5-11: Register precedes returning the identical typed operation task; no wrapper or self-dependent close join is introduced.
        #pragma warning disable VSTHRD003
        return actualTask;
        #pragma warning restore VSTHRD003
    }

    /// <summary>弹幕时间辅助是否可用（由运行时如实报告；没有运行时视为不可用）.</summary>
    internal static (bool Available, string? Reason) DanmakuAid(object owner)
    {
        try
        {
            var runtime = owner.Get<SponsorRuntime>();
            return (runtime.DanmakuAidAvailable, runtime.DanmakuAidAvailable ? null : runtime.DanmakuAidUnavailableReason);
        }
        catch (Exception)
        {
            return (false, null);
        }
    }

    private static void Register(object owner, Task actualTask)
    {
        SponsorRuntime? runtime;
        try
        {
            runtime = owner.Get<SponsorRuntime>();
        }
        catch (Exception)
        {
            // No app kernel: nothing global to drain. Never fabricate a tracker.
            return;
        }

        _ = runtime.TrackUiWork(actualTask);
    }
}
