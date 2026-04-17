using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BlenderRenderStudio.Models;

namespace BlenderRenderStudio.Services;

/// <summary>
/// Blender 批量渲染引擎。移植自 Python 脚本，增加自动重启、事件驱动架构。
/// </summary>
public class RenderEngine
{
    // ── 正则（与 Python 版一致）──────────────────────────────────────
    private static readonly Regex FrameRe = new(@"Fra:\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex MemRe = new(@"Mem:\s*([0-9.]+[A-Za-z]*)", RegexOptions.Compiled);
    private static readonly Regex SampleRe = new(@"Sample\s+(\d+)/(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SavedRe = new(@"Saved:\s+'(.+?)'", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TimeRe = new(@"Time:\s*([0-9:.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex WarningRe = new(@"\b(warning|missing)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ErrorRe = new(@"\b(error|failed|exception)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const int RestartBatchCode = 75;

    // ── 事件 ────────────────────────────────────────────────────────
    public event Action<int>? FrameStarted;
    public event Action<int, int>? SampleProgress;       // current, total
    public event Action<int, string>? FrameSaved;         // frame, outputPath
    public event Action<string>? BlenderMemoryUpdate;     // e.g. "2.3G"
    public event Action<double>? FrameTimeUpdate;         // seconds
    public event Action<string, string>? Log;             // level, message
    public event Action<int, int>? BatchStarted;          // start, end
    public event Action? RenderCompleted;
    public event Action<float, float>? SystemMemoryUpdate; // phys%, commit%
    public event Action<int, string>? MemoryRestart;       // resumeFrame, reason

    private Process? _currentProcess;
    private RenderConfig _config = new();
    private int _autoRestartCount;

    public bool IsRunning { get; private set; }
    public int CurrentFrame { get; private set; }
    public int TotalSavedFrames { get; private set; }

    // ── 公开接口 ────────────────────────────────────────────────────

    public async Task StartAsync(RenderConfig config, CancellationToken ct)
    {
        _config = config;
        _autoRestartCount = 0;
        IsRunning = true;

        try
        {
            await RenderLoopAsync(ct);
        }
        finally
        {
            IsRunning = false;
            RenderCompleted?.Invoke();
        }
    }

    public void Stop()
    {
        StopProcess();
        IsRunning = false;
    }

    // ── 渲染循环 ────────────────────────────────────────────────────

    private async Task RenderLoopAsync(CancellationToken ct)
    {
        // 单帧模式：直接渲染一帧，不走批处理循环
        if (_config.OutputType == RenderOutputType.SingleFrame)
        {
            Log?.Invoke("batch", $"单帧渲染 帧 {_config.SingleFrameNumber}");
            int code = await RunBatchAsync(_config.SingleFrameNumber, _config.SingleFrameNumber, ct);
            if (code == 0)
                Log?.Invoke("done", $"单帧 {_config.SingleFrameNumber} 渲染完成！");
            else if (!ct.IsCancellationRequested)
                Log?.Invoke("error", $"单帧渲染失败 (code={code})");
            return;
        }

        // 视频模式：整段一次渲染，不分批
        if (_config.OutputType == RenderOutputType.Video)
        {
            Log?.Invoke("batch", $"视频渲染 帧 {_config.StartFrame}-{_config.EndFrame}");
            int code = await RunBatchAsync(_config.StartFrame, _config.EndFrame, ct);
            if (code == 0)
                Log?.Invoke("done", "视频渲染完成！");
            else if (!ct.IsCancellationRequested)
                Log?.Invoke("error", $"视频渲染失败 (code={code})");
            return;
        }

        // 图片序列模式：原有批处理逻辑
        int currentStart = ClampResume(LoadProgress(), _config.StartFrame, _config.EndFrame);

        if (currentStart > _config.EndFrame)
        {
            Log?.Invoke("done", "所有帧已渲染完成");
            TryDeleteProgress();
            return;
        }

        if (currentStart > _config.StartFrame)
            Log?.Invoke("resume", $"检测到上次中断帧：{currentStart}，将从该帧继续");

        while (currentStart <= _config.EndFrame && !ct.IsCancellationRequested)
        {
            // ── 跳过磁盘上已存在输出文件的连续帧 ──
            while (currentStart <= _config.EndFrame)
            {
                var existingFile = RenderConfig.FindFrameFile(_config.OutputPath, currentStart);
                if (existingFile == null) break;
                Log?.Invoke("skip", $"帧 {currentStart} 输出已存在，跳过");
                FrameSaved?.Invoke(currentStart, existingFile);
                TotalSavedFrames++;
                currentStart++;
                SaveProgress(currentStart);
            }

            if (currentStart > _config.EndFrame || ct.IsCancellationRequested) break;

            int currentEnd = Math.Min(currentStart + _config.BatchSize - 1, _config.EndFrame);
            int code = await RunBatchAsync(currentStart, currentEnd, ct);

            if (ct.IsCancellationRequested) break;

            if (code == RestartBatchCode)
            {
                // 内存过高重启
                await Task.Delay(TimeSpan.FromSeconds(_config.RestartDelaySeconds), ct);
                currentStart = ClampResume(LoadProgress(), _config.StartFrame, _config.EndFrame);
                continue;
            }

            if (code != 0)
            {
                if (_config.AutoRestartOnCrash && _autoRestartCount < _config.MaxAutoRestarts)
                {
                    _autoRestartCount++;
                    Log?.Invoke("warn", $"Blender 异常退出 (code={code})，自动重启 #{_autoRestartCount}");
                    await Task.Delay(TimeSpan.FromSeconds(_config.RestartDelaySeconds), ct);
                    currentStart = ClampResume(LoadProgress(), _config.StartFrame, _config.EndFrame);
                    continue;
                }

                Log?.Invoke("error", $"Blender 退出码 {code}，已达最大重启次数，停止渲染");
                return;
            }

            currentStart = currentEnd + 1;
            SaveProgress(currentStart);
        }

        if (!ct.IsCancellationRequested)
        {
            TryDeleteProgress();
            Log?.Invoke("done", "所有批次渲染完成！");
        }
    }

    // ── 单批次渲染 ──────────────────────────────────────────────────

    private async Task<int> RunBatchAsync(int startFrame, int endFrame, CancellationToken ct)
    {
        BatchStarted?.Invoke(startFrame, endFrame);
        Log?.Invoke("batch", $"批次 {startFrame}-{endFrame}");

        var args = _config.BuildCommand(startFrame, endFrame);
        var psi = new ProcessStartInfo
        {
            FileName = args[0],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        for (int i = 1; i < args.Length; i++)
            psi.ArgumentList.Add(args[i]);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _currentProcess = process;

        int currentFrame = startFrame;
        string blenderMem = "?";
        int sampleCur = 0, sampleTotal = 0;
        var savedFrames = new HashSet<int>();
        bool restartPending = false;
        string restartNote = "";
        var memMonitor = new MemoryMonitor(_config.MemoryThreshold, _config.MemoryPollSeconds);

        try
        {
            process.Start();
            CurrentFrame = startFrame;
            FrameStarted?.Invoke(startFrame);

            // 合并 stdout + stderr（Blender 将渲染进度输出到 stderr，等同于 Python 的 stderr=subprocess.STDOUT）
            var lineChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

            var stdoutTask = Task.Run(async () =>
            {
                try { while (await process.StandardOutput.ReadLineAsync() is string l) await lineChannel.Writer.WriteAsync(l); }
                catch { /* stream closed */ }
            });

            var stderrTask = Task.Run(async () =>
            {
                try { while (await process.StandardError.ReadLineAsync() is string l) await lineChannel.Writer.WriteAsync(l); }
                catch { /* stream closed */ }
            });

            _ = Task.Run(async () =>
            {
                await Task.WhenAll(stdoutTask, stderrTask);
                lineChannel.Writer.TryComplete();
            });

            await foreach (var rawLine in lineChannel.Reader.ReadAllAsync(ct))
            {
                if (string.IsNullOrWhiteSpace(rawLine)) continue;
                var line = rawLine.Trim();

                // 内存检测
                if (!restartPending)
                {
                    var memStatus = memMonitor.Check();
                    if (memStatus is { IsOverThreshold: true })
                    {
                        restartPending = true;
                        restartNote = $"sys={memStatus.PhysicalUsed:F1}% commit={memStatus.CommitUsed:F1}%";
                        SystemMemoryUpdate?.Invoke(memStatus.PhysicalUsed, memStatus.CommitUsed);
                    }
                }

                // 系统内存定期上报
                var sysCheck = memMonitor.Check();
                if (sysCheck != null)
                    SystemMemoryUpdate?.Invoke(sysCheck.PhysicalUsed, sysCheck.CommitUsed);

                // 解析 Fra: / Mem:
                var frameMatch = FrameRe.Match(line);
                if (frameMatch.Success)
                {
                    int newFrame = int.Parse(frameMatch.Groups[1].Value);
                    if (newFrame != currentFrame)
                    {
                        currentFrame = newFrame;
                        CurrentFrame = newFrame;
                        FrameStarted?.Invoke(newFrame);
                    }
                }

                var memMatch = MemRe.Match(line);
                if (memMatch.Success)
                {
                    blenderMem = memMatch.Groups[1].Value;
                    BlenderMemoryUpdate?.Invoke(blenderMem);
                }

                // Sample 进度
                var sampleMatch = SampleRe.Match(line);
                if (sampleMatch.Success)
                {
                    sampleCur = int.Parse(sampleMatch.Groups[1].Value);
                    sampleTotal = int.Parse(sampleMatch.Groups[2].Value);
                    SampleProgress?.Invoke(sampleCur, sampleTotal);
                    continue;
                }

                // Saved
                var savedMatch = SavedRe.Match(line);
                if (savedMatch.Success)
                {
                    string outputPath = savedMatch.Groups[1].Value;
                    savedFrames.Add(currentFrame);
                    TotalSavedFrames++;
                    int resumeFrame = currentFrame + 1;
                    SaveProgress(resumeFrame);

                    FrameSaved?.Invoke(currentFrame, outputPath);
                    Log?.Invoke("saved", $"帧 {currentFrame} → {outputPath}");

                    if (restartPending)
                    {
                        StopProcess();
                        MemoryRestart?.Invoke(resumeFrame, restartNote);
                        Log?.Invoke("warn", $"内存过高({restartNote})，当前帧完成后重启，下一批从 {resumeFrame} 开始");
                        return RestartBatchCode;
                    }
                    continue;
                }

                // Time
                var timeMatch = TimeRe.Match(line);
                if (timeMatch.Success)
                {
                    double? sec = ParseTime(timeMatch.Groups[1].Value);
                    if (sec.HasValue) FrameTimeUpdate?.Invoke(sec.Value);
                    continue;
                }

                // 过滤 Blender 插件在 headless 模式下的无害报错
                if (line.Contains("bpy.app.handlers.") || line.Contains("Error in bpy.app"))
                    continue;

                // Error / Warning
                if (ErrorRe.IsMatch(line))
                {
                    Log?.Invoke("error", line);
                    continue;
                }
                if (WarningRe.IsMatch(line))
                {
                    Log?.Invoke("warn", line);
                    continue;
                }

                // 其他行
                Log?.Invoke("debug", line);
            }

            if (ct.IsCancellationRequested)
            {
                int resumeFrame = savedFrames.Contains(currentFrame)
                    ? currentFrame + 1
                    : currentFrame;
                SaveProgress(resumeFrame);
                StopProcess();
                Log?.Invoke("interrupt", $"用户中断，进度保留到帧 {resumeFrame}");
                return -1;
            }

            await process.WaitForExitAsync(ct);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            StopProcess();
            return -1;
        }
        catch (Exception ex)
        {
            Log?.Invoke("error", $"进程异常: {ex.Message}");
            return -1;
        }
        finally
        {
            _currentProcess = null;
        }
    }

    // ── 工具方法 ────────────────────────────────────────────────────

    private void StopProcess()
    {
        try
        {
            if (_currentProcess is { HasExited: false } p)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
        }
        catch { /* ignore */ }
    }

    private int? LoadProgress()
    {
        try
        {
            var path = _config.ProgressFilePath;
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            return int.TryParse(text, out int v) ? v : null;
        }
        catch { return null; }
    }

    private void SaveProgress(int frame)
    {
        try { File.WriteAllText(_config.ProgressFilePath, frame.ToString()); }
        catch { /* ignore */ }
    }

    private void TryDeleteProgress()
    {
        try { File.Delete(_config.ProgressFilePath); } catch { /* ignore */ }
    }

    private static int ClampResume(int? frame, int start, int end)
    {
        if (frame is null || frame < start) return start;
        return Math.Min(frame.Value, end + 1);
    }

    private static double? ParseTime(string text)
    {
        try
        {
            var parts = text.Split(':').Select(double.Parse).ToArray();
            while (parts.Length < 3) parts = [0, .. parts];
            return parts[^3] * 3600 + parts[^2] * 60 + parts[^1];
        }
        catch { return null; }
    }
}
