using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlenderRenderStudio.Helpers;

namespace BlenderRenderStudio.Models;

/// <summary>渲染项目数据模型（可观察，支持 UI 实时绑定）</summary>
public class RenderProject : ObservableObject
{
    private int _completedFrames;
    private ProjectStatus _status = ProjectStatus.Idle;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "未命名项目";
    public string BlendFilePath { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public string OutputPrefix { get; set; } = "frame_";
    public string BlenderPath { get; set; } = string.Empty;

    /// <summary>Blender -o 参数的完整模式路径（目录 + 前缀 + #####）</summary>
    [JsonIgnore]
    public string OutputPattern => string.IsNullOrEmpty(OutputDirectory) ? string.Empty
        : Path.Combine(OutputDirectory, OutputPrefix + "#####");

    // ── 渲染参数 ──
    public int StartFrame { get; set; } = 1;
    public int EndFrame { get; set; } = 250;
    public int BatchSize { get; set; } = 50;
    public int OutputType { get; set; } // 0=序列, 1=视频, 2=单帧
    public int SingleFrameNumber { get; set; } = 1;

    // ── 高级参数 ──
    public float MemoryThreshold { get; set; } = 85.0f;
    public float MemoryPollSeconds { get; set; } = 1.0f;
    public float RestartDelaySeconds { get; set; } = 3.0f;
    public int MaxAutoRestarts { get; set; } = 10;
    public bool AutoRestartOnCrash { get; set; } = true;
    public bool EnableBlackFrameDetection { get; set; } = true;
    public double BlackFrameThreshold { get; set; } = 5.0;

    // ── 进度（可观察，供 ProjectListPage 卡片实时绑定）──
    public int CompletedFrames
    {
        get => _completedFrames;
        set { if (SetProperty(ref _completedFrames, value)) OnPropertyChanged(nameof(ProgressPercent)); }
    }
    public int LastRenderedFrame { get; set; }
    public ProjectStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(IsRendering));
                OnPropertyChanged(nameof(IsRenderingOrQueued));
                OnPropertyChanged(nameof(StatusDisplayText));
            }
        }
    }

    // ── 计算属性（供 UI 绑定）──
    [JsonIgnore] public bool IsRendering => Status == ProjectStatus.Rendering;
    [JsonIgnore] public bool IsRenderingOrQueued => Status is ProjectStatus.Rendering or ProjectStatus.Queued;
    [JsonIgnore] public string StatusDisplayText => Status switch
    {
        ProjectStatus.Rendering => "渲染中",
        ProjectStatus.Queued => "等待渲染",
        ProjectStatus.Paused => "已暂停",
        ProjectStatus.Completed => "已完成",
        ProjectStatus.Error => "出错",
        _ => string.Empty,
    };

    // ── 元数据 ──
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime LastRenderAt { get; set; }
    public string? CoverImagePath { get; set; }  // 首张完成帧路径，用于卡片缩略图

    /// <summary>项目独立缓存目录</summary>
    [JsonIgnore]
    public string CacheDirectory => Path.Combine(
        Services.SettingsService.StorageDir, "ProjectCache", Id);

    /// <summary>项目进度文件路径</summary>
    [JsonIgnore]
    public string ProgressFilePath => Path.Combine(CacheDirectory, "progress.txt");

    /// <summary>缩略图缓存目录</summary>
    [JsonIgnore]
    public string ThumbnailCacheDir => Path.Combine(CacheDirectory, "thumbnails");

    /// <summary>总帧数</summary>
    [JsonIgnore]
    public int TotalFrames => OutputType == 2 ? 1 : Math.Max(EndFrame - StartFrame + 1, 1);

    /// <summary>完成百分比</summary>
    [JsonIgnore]
    public double ProgressPercent => TotalFrames > 0 ? (double)CompletedFrames / TotalFrames * 100 : 0;

    /// <summary>显示用的文件名</summary>
    [JsonIgnore]
    public string BlendFileName => string.IsNullOrEmpty(BlendFilePath)
        ? "未选择" : Path.GetFileName(BlendFilePath);
}

public enum ProjectStatus
{
    Idle,       // 空闲
    Queued,     // 在队列中等待
    Rendering,  // 渲染中
    Paused,     // 暂停
    Completed,  // 完成
    Error       // 出错
}

/// <summary>批量渲染队列中的任务（可观察，供队列页实时绑定）</summary>
public class RenderJob : ObservableObject
{
    private RenderJobStatus _status = RenderJobStatus.Queued;
    private int _completedFrames;
    private int _totalFrames;
    private string _projectName = string.Empty;

    public required string ProjectId { get; set; }
    public RenderJobStatus Status
    {
        get => _status;
        set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(StatusDisplayText)); }
    }
    public DateTime QueuedAt { get; set; } = DateTime.Now;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }

    // ── 进度信息（队列页实时显示）──
    public string ProjectName
    {
        get => _projectName;
        set => SetProperty(ref _projectName, value);
    }
    public int CompletedFrames
    {
        get => _completedFrames;
        set { if (SetProperty(ref _completedFrames, value)) OnPropertyChanged(nameof(ProgressPercent)); }
    }
    public int TotalFrames
    {
        get => _totalFrames;
        set { if (SetProperty(ref _totalFrames, value)) OnPropertyChanged(nameof(ProgressPercent)); }
    }
    public double ProgressPercent => TotalFrames > 0 ? (double)CompletedFrames / TotalFrames * 100 : 0;
    public string StatusDisplayText => Status switch
    {
        RenderJobStatus.Queued => "排队中",
        RenderJobStatus.Running => "渲染中",
        RenderJobStatus.Completed => "已完成",
        RenderJobStatus.Failed => "失败",
        RenderJobStatus.Skipped => "已跳过",
        _ => string.Empty,
    };
}

public enum RenderJobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Skipped
}
