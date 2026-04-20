using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace BlenderRenderStudio.Helpers;

/// <summary>
/// 安全的 UI 线程调度工具。
/// 封装 DispatcherQueue 的生命周期检查 + 异常捕获，
/// 防止窗口关闭后 TryEnqueue 访问已释放的 XAML 对象导致 0xC000027B。
/// </summary>
public sealed class SafeDispatcher
{
    private volatile bool _shutdown;
    private DispatcherQueue? _dispatcher;

    public SafeDispatcher(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>标记为已关闭，后续所有调度请求静默忽略</summary>
    public void Shutdown()
    {
        _shutdown = true;
        _dispatcher = null;
        Debug.WriteLine("[SafeDispatcher] Shutdown");
    }

    public bool IsShutdown => _shutdown;

    /// <summary>在 UI 线程执行同步操作（已有线程访问权时直接执行）</summary>
    public void Run(Action action)
    {
        if (_shutdown) return;
        var d = _dispatcher;
        if (d == null) return;

        if (d.HasThreadAccess)
        {
            SafeExecute(action);
            return;
        }

        d.TryEnqueue(() =>
        {
            if (_shutdown) return;
            SafeExecute(action);
        });
    }

    /// <summary>在 UI 线程执行异步操作</summary>
    public void RunAsync(Func<Task> asyncAction)
    {
        if (_shutdown) return;
        var d = _dispatcher;
        if (d == null) return;

        if (d.HasThreadAccess)
        {
            _ = SafeExecuteAsync(asyncAction);
            return;
        }

        d.TryEnqueue(() =>
        {
            if (_shutdown) return;
            _ = SafeExecuteAsync(asyncAction);
        });
    }

    /// <summary>仅在后台线程时调度到 UI，已在 UI 线程则直接执行</summary>
    public void RunIfNeeded(Action action)
    {
        if (_shutdown) return;
        var d = _dispatcher;
        if (d == null) return;

        if (d.HasThreadAccess)
        {
            SafeExecute(action);
        }
        else
        {
            d.TryEnqueue(() =>
            {
                if (_shutdown) return;
                SafeExecute(action);
            });
        }
    }

    private static void SafeExecute(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SafeDispatcher] 同步异常: {ex.Message}");
        }
    }

    private static async Task SafeExecuteAsync(Func<Task> asyncAction)
    {
        try
        {
            await asyncAction();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SafeDispatcher] 异步异常: {ex.Message}");
        }
    }
}
