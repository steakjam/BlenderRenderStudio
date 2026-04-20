using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlenderRenderStudio.Helpers;
using BlenderRenderStudio.Models;

namespace BlenderRenderStudio.Services;

/// <summary>
/// 批量渲染队列服务。串行执行（一次一个 Blender 实例），支持：
/// - 拖拽排序调整优先级
/// - 跳过当前项目
/// - 暂停/恢复队列
/// - 断点续渲
/// - 完成/失败通知
/// - 帧级进度广播（实时更新 job + project）
/// </summary>
public class RenderQueueService : ObservableObject
{
    private readonly RenderEngine _engine = new();
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private bool _isPaused;
    private string? _currentProjectId;
    private SafeDispatcher? _safeDispatcher;

    public ObservableCollection<RenderJob> Jobs { get; } = [];

    public bool IsRunning { get => _isRunning; set => SetProperty(ref _isRunning, value); }
    public bool IsPaused { get => _isPaused; set => SetProperty(ref _isPaused, value); }
    public string? CurrentProjectId { get => _currentProjectId; set => SetProperty(ref _currentProjectId, value); }

    /// <summary>渲染引擎事件代理（供 UI 绑定）</summary>
    public RenderEngine Engine => _engine;

    /// <summary>导航到项目工作区的回调（由 MainWindow 注入）</summary>
    public Action<RenderProject>? NavigateToProject { get; set; }

    /// <summary>注入 UI 线程 DispatcherQueue（确保属性更新在 UI 线程）</summary>
    public void SetSafeDispatcher(SafeDispatcher sd) => _safeDispatcher = sd;

    /// <summary>添加项目到队列</summary>
    public void Enqueue(string projectId)
    {
        if (Jobs.Any(j => j.ProjectId == projectId
            && j.Status is RenderJobStatus.Queued or RenderJobStatus.Running))
            return; // 已在队列中

        var project = ProjectService.GetById(projectId);
        var job = new RenderJob
        {
            ProjectId = projectId,
            ProjectName = project?.Name ?? projectId,
            TotalFrames = project?.TotalFrames ?? 0,
            CompletedFrames = project?.CompletedFrames ?? 0,
        };
        Jobs.Add(job);

        if (project != null)
        {
            project.Status = ProjectStatus.Queued;
            ProjectService.Update(project);
        }

        // 队列非空且未运行 → 自动启动
        if (!IsRunning)
            _ = StartAsync();
    }

    /// <summary>从队列移除</summary>
    public void Remove(string projectId)
    {
        var job = Jobs.FirstOrDefault(j => j.ProjectId == projectId);
        if (job != null && job.Status == RenderJobStatus.Queued)
        {
            Jobs.Remove(job);
            var project = ProjectService.GetById(projectId);
            if (project != null)
            {
                project.Status = ProjectStatus.Idle;
                ProjectService.Update(project);
            }
        }
    }

    /// <summary>拖拽排序</summary>
    public void Reorder(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= Jobs.Count) return;
        if (toIndex < 0 || toIndex >= Jobs.Count) return;
        Jobs.Move(fromIndex, toIndex);
    }

    /// <summary>开始执行队列</summary>
    public async Task StartAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        IsPaused = false;
        _cts = new CancellationTokenSource();

        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                // 等待暂停结束
                while (IsPaused && !_cts.Token.IsCancellationRequested)
                    await Task.Delay(500, _cts.Token);

                // 取下一个排队任务
                var job = Jobs.FirstOrDefault(j => j.Status == RenderJobStatus.Queued);
                if (job == null) break; // 队列空

                var project = ProjectService.GetById(job.ProjectId);
                if (project == null)
                {
                    job.Status = RenderJobStatus.Failed;
                    job.ErrorMessage = "项目不存在";
                    continue;
                }

                // 执行渲染
                CurrentProjectId = project.Id;
                job.Status = RenderJobStatus.Running;
                job.StartedAt = DateTime.Now;
                job.TotalFrames = project.TotalFrames;
                // 重要：从 0 开始计数，因为引擎的 SkipExistingFrames 会对已存在帧
                // 触发 FrameSaved 事件，OnFrameSaved 回调会正确累加到实际完成数。
                // 若从 project.CompletedFrames 起始，则 skip 事件会导致双重计数。
                job.CompletedFrames = 0;
                project.Status = ProjectStatus.Rendering;
                project.LastRenderAt = DateTime.Now;
                ProjectService.Update(project);

                // 帧完成时广播进度到 job + project
                void OnFrameSaved(int frame, string path)
                {
                    RunOnUI(() =>
                    {
                        job.CompletedFrames++;
                        project.CompletedFrames = job.CompletedFrames;
                    });
                }
                _engine.FrameSaved += OnFrameSaved;

                try
                {
                    var config = BuildConfig(project);
                    await Task.Run(() => _engine.StartAsync(config, _cts.Token));

                    RunOnUI(() =>
                    {
                        job.Status = RenderJobStatus.Completed;
                        job.CompletedAt = DateTime.Now;
                        project.Status = ProjectStatus.Completed;
                    });
                }
                catch (OperationCanceledException)
                {
                    RunOnUI(() =>
                    {
                        job.Status = RenderJobStatus.Skipped;
                        project.Status = ProjectStatus.Paused;
                    });
                    break;
                }
                catch (Exception ex)
                {
                    RunOnUI(() =>
                    {
                        job.Status = RenderJobStatus.Failed;
                        job.ErrorMessage = ex.Message;
                        project.Status = ProjectStatus.Error;
                    });
                }
                finally
                {
                    _engine.FrameSaved -= OnFrameSaved;
                    ProjectService.Update(project);
                    CurrentProjectId = null;
                }
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>暂停队列（完成当前帧后暂停）</summary>
    public void Pause() => IsPaused = true;

    /// <summary>恢复队列</summary>
    public void Resume() => IsPaused = false;

    /// <summary>停止队列</summary>
    public void Stop()
    {
        _cts?.Cancel();
        _engine.Stop();
    }

    /// <summary>跳过当前项目</summary>
    public void SkipCurrent()
    {
        _engine.Stop();
        // 当前任务标记为 Skipped，循环会自动取下一个
        var current = Jobs.FirstOrDefault(j => j.Status == RenderJobStatus.Running);
        if (current != null) current.Status = RenderJobStatus.Skipped;
    }

    private void RunOnUI(Action action)
    {
        if (_safeDispatcher != null)
            _safeDispatcher.RunIfNeeded(action);
        else
            action();
    }

    private static RenderConfig BuildConfig(RenderProject project)
    {
        var outputType = project.OutputType switch
        {
            1 => RenderOutputType.Video,
            2 => RenderOutputType.SingleFrame,
            _ => RenderOutputType.ImageSequence,
        };

        int startFrame = outputType == RenderOutputType.SingleFrame
            ? project.SingleFrameNumber : project.StartFrame;
        int endFrame = outputType == RenderOutputType.SingleFrame
            ? project.SingleFrameNumber : project.EndFrame;

        return new RenderConfig
        {
            BlenderPath = SettingsService.Load().BlenderPath,
            BlendFilePath = project.BlendFilePath,
            OutputPath = project.OutputPattern,
            StartFrame = startFrame,
            EndFrame = endFrame,
            BatchSize = outputType == RenderOutputType.Video
                ? endFrame - startFrame + 1 : project.BatchSize,
            MemoryThreshold = project.MemoryThreshold,
            MemoryPollSeconds = project.MemoryPollSeconds,
            RestartDelaySeconds = project.RestartDelaySeconds,
            MaxAutoRestarts = project.MaxAutoRestarts,
            AutoRestartOnCrash = project.AutoRestartOnCrash,
            EnableBlackFrameDetection = project.EnableBlackFrameDetection
                && outputType != RenderOutputType.Video,
            BlackFrameBrightnessThreshold = project.BlackFrameThreshold,
            ProgressFilePath = project.ProgressFilePath,
            OutputType = outputType,
            SingleFrameNumber = project.SingleFrameNumber,
            SkipExistingFrames = project.CompletedFrames > 0,
            ProjectId = project.Id,
        };
    }
}
