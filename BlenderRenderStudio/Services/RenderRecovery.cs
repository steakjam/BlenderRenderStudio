using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BlenderRenderStudio.Helpers;
using BlenderRenderStudio.Models;

namespace BlenderRenderStudio.Services;

/// <summary>
/// 渲染进程恢复服务。
/// - 启动 Blender 时保存 PID + 渲染配置到恢复文件
/// - 应用重启时检测残留的 Blender 进程并恢复监控
/// - 通过轮询输出目录检测新帧，更新项目进度
/// </summary>
public class RenderRecovery
{
    private static readonly string RecoveryFile = Path.Combine(SettingsService.StorageDir, "active_render.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private Timer? _pollTimer;
    private Process? _trackedProcess;
    private RenderSession? _session;
    private SafeDispatcher? _safeDispatcher;
    private volatile bool _stopped; // 原子停止标志，防止窗口关闭后继续入队

    /// <summary>保存当前渲染会话信息（Blender 启动后立即调用）</summary>
    public static void SaveSession(int pid, string projectId, string outputPath, int startFrame, int endFrame)
    {
        try
        {
            // 确保目录存在
            var dir = Path.GetDirectoryName(RecoveryFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var session = new RenderSession
            {
                Pid = pid,
                ProjectId = projectId,
                OutputPath = outputPath,
                StartFrame = startFrame,
                EndFrame = endFrame,
                StartedAt = DateTime.Now,
            };
            var json = JsonSerializer.Serialize(session, JsonOpts);
            File.WriteAllText(RecoveryFile, json);
            Debug.WriteLine($"[RenderRecovery] 保存会话: PID={pid}, Project={projectId}, Output={outputPath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RenderRecovery] 保存会话失败: {ex.Message}");
        }
    }

    /// <summary>渲染正常完成或用户停止时清除恢复文件</summary>
    public static void ClearSession()
    {
        try { if (File.Exists(RecoveryFile)) File.Delete(RecoveryFile); }
        catch { /* ignore */ }
    }

    /// <summary>
    /// 应用启动时调用：检测是否有残留的 Blender 渲染进程。
    /// 如果找到，恢复监控并返回 true。
    /// </summary>
    public bool TryRecover(SafeDispatcher safeDispatcher)
    {
        _stopped = false;
        _safeDispatcher = safeDispatcher;

        Debug.WriteLine($"[RenderRecovery] TryRecover: 检查恢复文件 {RecoveryFile}");

        RenderSession? session;
        try
        {
            if (!File.Exists(RecoveryFile))
            {
                Debug.WriteLine("[RenderRecovery] 无恢复文件，跳过");
                return false;
            }
            var json = File.ReadAllText(RecoveryFile);
            session = JsonSerializer.Deserialize<RenderSession>(json, JsonOpts);
            if (session == null) return false;
            Debug.WriteLine($"[RenderRecovery] 读取会话: PID={session.Pid}, Project={session.ProjectId}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RenderRecovery] 读取恢复文件失败: {ex.Message}");
            ClearSession();
            return false;
        }

        // 检查 PID 是否仍在运行且是 blender 进程
        Process? process;
        try
        {
            process = Process.GetProcessById(session.Pid);
            if (process.HasExited)
            {
                Debug.WriteLine($"[RenderRecovery] PID {session.Pid} 已退出");
                ClearSession();
                return false;
            }

            // 校验进程名是否为 blender（防止 PID 被复用）
            var name = process.ProcessName.ToLowerInvariant();
            Debug.WriteLine($"[RenderRecovery] PID {session.Pid} 进程名: {name}");
            if (!name.Contains("blender"))
            {
                Debug.WriteLine($"[RenderRecovery] PID {session.Pid} 不是 Blender 进程，清除会话");
                ClearSession();
                return false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RenderRecovery] 进程检查失败: {ex.Message}");
            ClearSession();
            return false;
        }

        // 恢复监控
        _session = session;
        _trackedProcess = process;
        Debug.WriteLine($"[RenderRecovery] 成功恢复 Blender 进程监控 PID={session.Pid}");

        // 更新项目状态为渲染中
        var project = ProjectService.GetById(session.ProjectId);
        if (project != null)
        {
            project.Status = ProjectStatus.Rendering;
            // 立即扫描一次磁盘刷新进度（使用 session 中的实际渲染路径）
            UpdateProgressFromDisk(project, session);
            ProjectService.Update(project);
            Debug.WriteLine($"[RenderRecovery] 项目 {session.ProjectId} 已恢复，已完成帧: {project.CompletedFrames}");
        }
        else
        {
            Debug.WriteLine($"[RenderRecovery] 项目 {session.ProjectId} 不存在，仅监控进程退出");
        }

        // 启动轮询定时器（每 3 秒扫描输出目录 + 检查进程是否退出）
        _pollTimer = new Timer(PollCallback, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));

        return true;
    }

    /// <summary>停止监控（应用关闭时调用）</summary>
    public void StopMonitoring()
    {
        // 1. 先设置停止标志，阻止后续 PollCallback 入队
        _stopped = true;

        // 2. 先停止定时器触发（不再产生新回调），再 Dispose
        var timer = _pollTimer;
        _pollTimer = null;
        if (timer != null)
        {
            timer.Change(Timeout.Infinite, Timeout.Infinite);
            // 等待短暂时间确保正在执行的回调有机会完成
            Thread.Sleep(50);
            timer.Dispose();
        }

        // 3. 清理引用，防止残余回调访问已释放对象
        _safeDispatcher = null;
        _session = null;
        _trackedProcess = null;
        Debug.WriteLine("[RenderRecovery] StopMonitoring 完成，已清理所有引用");
    }

    private void PollCallback(object? state)
    {
        // 快速检查停止标志，避免已停止后继续执行
        if (_stopped) return;

        // 捕获局部变量，防止并发 StopMonitoring 置 null 导致 NRE
        var session = _session;
        var process = _trackedProcess;
        var safeDispatcher = _safeDispatcher;
        if (session == null || process == null || safeDispatcher == null || safeDispatcher.IsShutdown) return;

        try
        {
            bool exited = process.HasExited;

            // 在后台线程完成磁盘 IO，减少 UI 线程负担
            var project = ProjectService.GetById(session.ProjectId);
            if (project == null) return;

            string outputPath = session.OutputPath ?? project.OutputPattern;
            int start = session.StartFrame;
            int end = session.EndFrame;
            int found = CountFramesOnDisk(outputPath, start, end);

            // 再次检查停止标志（IO 期间可能已关闭）
            if (_stopped) return;

            // 通过 SafeDispatcher 在 UI 线程做轻量状态更新
            safeDispatcher.Run(() =>
            {
                project.CompletedFrames = found;

                if (exited)
                {
                    project.Status = project.CompletedFrames >= project.TotalFrames
                        ? ProjectStatus.Completed
                        : ProjectStatus.Idle;
                    ProjectService.Update(project);
                    ClearSession();
                    StopMonitoring();
                    Debug.WriteLine($"[RenderRecovery] Blender 已退出，最终进度: {project.CompletedFrames}/{project.TotalFrames}");
                }
                else
                {
                    ProjectService.Update(project);
                }
            });
        }
        catch (Exception ex)
        {
            // 进程访问异常 → 视为已退出
            Debug.WriteLine($"[RenderRecovery] 轮询异常: {ex.Message}");
            ClearSession();
            StopMonitoring();
        }
    }

    /// <summary>在后台线程统计磁盘已完成帧数（纯 IO，无 UI 操作）</summary>
    private static int CountFramesOnDisk(string outputPath, int start, int end)
    {
        if (string.IsNullOrEmpty(outputPath)) return 0;

        string? dir = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;

        int found = 0;
        for (int i = start; i <= end; i++)
        {
            if (RenderConfig.FindFrameFile(outputPath, i) != null)
                found++;
        }
        return found;
    }

    /// <summary>
    /// 从磁盘扫描已完成帧数。优先使用 session 中记录的 OutputPath（渲染时实际使用的路径），
    /// 回退到 project.OutputDirectory。
    /// </summary>
    private static void UpdateProgressFromDisk(RenderProject project, RenderSession? session = null)
    {
        string outputPath = session?.OutputPath ?? project.OutputPattern;
        if (string.IsNullOrEmpty(outputPath)) return;

        int start = session?.StartFrame ?? (project.OutputType == 2 ? project.SingleFrameNumber : project.StartFrame);
        int end = session?.EndFrame ?? (project.OutputType == 2 ? project.SingleFrameNumber : project.EndFrame);

        // 检查输出目录是否存在（模式路径取父目录）
        string? dir = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        int found = 0;
        for (int i = start; i <= end; i++)
        {
            if (RenderConfig.FindFrameFile(outputPath, i) != null)
                found++;
        }
        project.CompletedFrames = found;
    }
}

/// <summary>渲染会话持久化数据</summary>
public class RenderSession
{
    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    [JsonPropertyName("projectId")]
    public string ProjectId { get; set; } = string.Empty;

    [JsonPropertyName("outputPath")]
    public string OutputPath { get; set; } = string.Empty;

    [JsonPropertyName("startFrame")]
    public int StartFrame { get; set; }

    [JsonPropertyName("endFrame")]
    public int EndFrame { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTime StartedAt { get; set; }
}
