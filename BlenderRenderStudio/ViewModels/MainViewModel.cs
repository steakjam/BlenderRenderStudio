using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlenderRenderStudio.Helpers;
using BlenderRenderStudio.Models;
using BlenderRenderStudio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BlenderRenderStudio.ViewModels;

public class MainViewModel : ObservableObject
{
    private readonly RenderEngine _engine = new();
    private readonly SafeDispatcher _safeDispatcher;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _thumbnailCts; // 缩略图批量加载取消令牌
    private bool _isStopping; // 用户主动停止标记，防止 RenderCompleted 覆盖进度

    // ── 配置属性 ────────────────────────────────────────────────────

    private string _blenderPath = @"D:\SteamLibrary\steamapps\common\Blender\blender.exe";
    private string _blendFilePath = string.Empty;
    private string _outputPath = string.Empty;
    private string _outputPrefix = "frame_";
    private int _startFrame = 1;
    private int _endFrame = 250;
    private int _batchSize = 50;
    private float _memoryThreshold = 85.0f;
    private float _memoryPollSeconds = 1.0f;
    private float _restartDelaySeconds = 3.0f;
    private int _maxAutoRestarts = 10;
    private bool _autoRestartOnCrash = true;
    private bool _enableBlackFrameDetection = true;
    private double _blackFrameThreshold = 5.0;
    private int _selectedOutputTypeIndex; // 0=图片序列, 1=视频, 2=单帧
    private int _singleFrameNumber = 1;

    public string BlenderPath { get => _blenderPath; set => SetProperty(ref _blenderPath, value); }
    public string BlendFilePath { get => _blendFilePath; set { if (SetProperty(ref _blendFilePath, value)) StartCommand?.RaiseCanExecuteChanged(); } }
    public string OutputPath { get => _outputPath; set => SetProperty(ref _outputPath, value); }
    public string OutputPrefix { get => _outputPrefix; set => SetProperty(ref _outputPrefix, value); }

    /// <summary>Blender -o 参数的完整模式路径（目录 + 前缀 + #####）</summary>
    public string OutputPattern => string.IsNullOrEmpty(OutputPath) ? string.Empty
        : Path.Combine(OutputPath, OutputPrefix + "#####");
    public int StartFrame { get => _startFrame; set => SetProperty(ref _startFrame, value); }
    public int EndFrame { get => _endFrame; set => SetProperty(ref _endFrame, value); }
    public int BatchSize { get => _batchSize; set => SetProperty(ref _batchSize, value); }
    public float MemoryThreshold { get => _memoryThreshold; set => SetProperty(ref _memoryThreshold, value); }
    public float MemoryPollSeconds { get => _memoryPollSeconds; set => SetProperty(ref _memoryPollSeconds, value); }
    public float RestartDelaySeconds { get => _restartDelaySeconds; set => SetProperty(ref _restartDelaySeconds, value); }
    public int MaxAutoRestarts { get => _maxAutoRestarts; set => SetProperty(ref _maxAutoRestarts, value); }
    public bool AutoRestartOnCrash { get => _autoRestartOnCrash; set => SetProperty(ref _autoRestartOnCrash, value); }
    public bool EnableBlackFrameDetection { get => _enableBlackFrameDetection; set => SetProperty(ref _enableBlackFrameDetection, value); }
    public double BlackFrameThreshold { get => _blackFrameThreshold; set => SetProperty(ref _blackFrameThreshold, value); }
    public int SingleFrameNumber { get => _singleFrameNumber; set => SetProperty(ref _singleFrameNumber, value); }

    /// <summary>项目级进度文件路径（隔离不同项目的渲染进度）</summary>
    public string? ProjectProgressFilePath { get; set; }

    /// <summary>所属项目 ID（用于 PID 恢复跟踪）</summary>
    public string? ProjectId { get; set; }

    /// <summary>输出类型下拉索引：0=图片序列, 1=视频, 2=单帧</summary>
    public int SelectedOutputTypeIndex
    {
        get => _selectedOutputTypeIndex;
        set
        {
            if (SetProperty(ref _selectedOutputTypeIndex, value))
            {
                OnPropertyChanged(nameof(IsImageSequence));
                OnPropertyChanged(nameof(IsVideoMode));
                OnPropertyChanged(nameof(IsSingleFrame));
                OnPropertyChanged(nameof(ShowFrameRange));
                OnPropertyChanged(nameof(ShowBatchSize));
            }
        }
    }

    /// <summary>当前选择的输出类型枚举</summary>
    public RenderOutputType OutputType => SelectedOutputTypeIndex switch
    {
        1 => RenderOutputType.Video,
        2 => RenderOutputType.SingleFrame,
        _ => RenderOutputType.ImageSequence,
    };

    // UI 可见性辅助属性
    public bool IsImageSequence => OutputType == RenderOutputType.ImageSequence;
    public bool IsVideoMode => OutputType == RenderOutputType.Video;
    public bool IsSingleFrame => OutputType == RenderOutputType.SingleFrame;
    public bool ShowFrameRange => !IsSingleFrame; // 图片序列和视频都需要帧范围
    public bool ShowBatchSize => IsImageSequence;  // 仅图片序列需要批大小

    // ── 运行状态属性 ────────────────────────────────────────────────

    private bool _isRendering;
    private double _overallProgress;
    private int _currentFrame;
    private int _currentSample;
    private int _totalSamples;
    private string _blenderMemory = "-";
    private float _systemMemPhys;
    private float _systemMemCommit;
    private double _currentFrameTime;
    private string _estimatedRemaining = "-";
    private int _completedFrames;
    private int _blackFrameCount;
    private int _errorFrameCount;
    private int _autoPreviewVersion; // 防止 FrameSaved 的异步预览覆盖 FrameStarted 的清除
    private double _renderAspectRatio; // 渲染图像宽高比，首次加载后缓存
    private int _renderPixelWidth;     // 渲染图像原始宽度
    private int _renderPixelHeight;    // 渲染图像原始高度
    private ImageSource? _previewImage;
    private bool _isLoadingPreview;       // 单帧预览加载中（显示 ProgressRing）
    private bool _isPlaying;              // 空格键播放中
    private bool _isBulkInserting;        // 批量插入帧列表中，抑制 CollectionChanged
    private string? _pendingPreviewPath;  // 最新待加载路径（合并快速连续请求）

    // ── 内存管理（仿 Windows 资源管理器） ────────────────────────────
    // 网格缩略图 LRU 缓存：保留最近 200 张缩略图的解码数据
    private readonly ThumbnailCache _gridCache = new(200);
    private string _statusText = "就绪";

    public bool IsRendering { get => _isRendering; set { SetProperty(ref _isRendering, value); OnPropertyChanged(nameof(IsNotRendering)); RaiseCommands(); } }
    public bool IsNotRendering => !IsRendering;

    /// <summary>是否由本 ViewModel 的 RenderEngine 发起的渲染（区别于闪退恢复的外部进程）</summary>
    public bool IsRenderingLocally { get; set; }
    public double OverallProgress { get => _overallProgress; set { if (SetProperty(ref _overallProgress, value)) OnPropertyChanged(nameof(OverallProgressText)); } }
    public string OverallProgressText => OverallProgress.ToString("F1");
    public int CurrentFrame { get => _currentFrame; set => SetProperty(ref _currentFrame, value); }
    public int CurrentSample { get => _currentSample; set => SetProperty(ref _currentSample, value); }
    public int TotalSamples { get => _totalSamples; set => SetProperty(ref _totalSamples, value); }
    public string BlenderMemory { get => _blenderMemory; set => SetProperty(ref _blenderMemory, value); }
    public float SystemMemPhys { get => _systemMemPhys; set => SetProperty(ref _systemMemPhys, value); }
    public float SystemMemCommit { get => _systemMemCommit; set => SetProperty(ref _systemMemCommit, value); }
    public double CurrentFrameTime { get => _currentFrameTime; set => SetProperty(ref _currentFrameTime, value); }
    public string EstimatedRemaining { get => _estimatedRemaining; set => SetProperty(ref _estimatedRemaining, value); }
    public int CompletedFrames { get => _completedFrames; set => SetProperty(ref _completedFrames, value); }
    public int BlackFrameCount { get => _blackFrameCount; set => SetProperty(ref _blackFrameCount, value); }
    public int ErrorFrameCount { get => _errorFrameCount; set => SetProperty(ref _errorFrameCount, value); }
    public ImageSource? PreviewImage
    {
        get => _previewImage;
        set
        {
            var old = _previewImage;
            System.Diagnostics.Trace.WriteLine($"[VM] PreviewImage setter: old={old?.GetType().Name ?? "null"}, new={value?.GetType().Name ?? "null"}");
            if (SetProperty(ref _previewImage, value))
                ScheduleDispose(old);
        }
    }
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public bool IsLoadingPreview { get => _isLoadingPreview; set => SetProperty(ref _isLoadingPreview, value); }
    public bool IsPlaying { get => _isPlaying; set => SetProperty(ref _isPlaying, value); }
    public bool IsBulkInserting { get => _isBulkInserting; set => SetProperty(ref _isBulkInserting, value); }

    // ── 预览模式 ─────────────────────────────────────────────────────

    private bool _isGridView;
    private bool _isFullscreenPreview;

    /// <summary>是否处于网格缩略图视图</summary>
    public bool IsGridView
    {
        get => _isGridView;
        set
        {
            if (SetProperty(ref _isGridView, value))
            {
                OnPropertyChanged(nameof(IsSingleView));
                // 切换离开网格视图时取消正在进行的缩略图批量加载
                if (!value)
                    CancelThumbnailLoading();
            }
        }
    }

    /// <summary>取消正在进行的缩略图批量加载</summary>
    public void CancelThumbnailLoading()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts = null;
    }

    /// <summary>是否处于全屏预览模式（隐藏左侧面板/状态栏/日志）</summary>
    public bool IsFullscreenPreview
    {
        get => _isFullscreenPreview;
        set { if (SetProperty(ref _isFullscreenPreview, value)) OnPropertyChanged(nameof(IsNormalMode)); }
    }

    public bool IsSingleView => !IsGridView;
    public bool IsNormalMode => !IsFullscreenPreview;

    private bool _showLogPanel;
    private bool _isSettingsView;

    /// <summary>是否显示日志面板（默认关闭，用户在设置中手动开启）</summary>
    public bool ShowLogPanel
    {
        get => _showLogPanel;
        set => SetProperty(ref _showLogPanel, value);
    }

    /// <summary>是否显示设置页面（页内导航）</summary>
    public bool IsSettingsView
    {
        get => _isSettingsView;
        set { if (SetProperty(ref _isSettingsView, value)) OnPropertyChanged(nameof(IsMainView)); }
    }

    public bool IsMainView => !IsSettingsView;

    // ── 集合 ────────────────────────────────────────────────────────

    public ObservableCollection<FrameResult> FrameResults { get; } = [];
    public ObservableCollection<LogEntry> LogEntries { get; } = [];

    /// <summary>网格视图的缩略图集合（帧号 + 图片）</summary>
    public ObservableCollection<FrameThumbnail> GridThumbnails { get; } = [];

    // ── 命令 ────────────────────────────────────────────────────────

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ClearLogCommand { get; }

    /// <summary>
    /// 由 MainWindow 注入：检测到上次中断进度时询问用户。
    /// 返回: "resume" = 续渲, "restart" = 从头开始, "cancel" = 取消不渲染。
    /// </summary>
    public Func<int, Task<string>>? AskResumeAsync { get; set; }

    // ── 构造 ────────────────────────────────────────────────────────

    public MainViewModel(SafeDispatcher safeDispatcher)
    {
        _safeDispatcher = safeDispatcher;

        // 加载上次保存的配置
        LoadSettings();
        AddLog("info", $"配置目录: {SettingsService.StorageDir}");

        StartCommand = new RelayCommand(OnStart, () => !IsRendering && !string.IsNullOrEmpty(BlendFilePath));
        StopCommand = new RelayCommand(OnStop, () => IsRendering);
        ClearLogCommand = new RelayCommand(() => LogEntries.Clear());

        // 挂载引擎事件
        _engine.FrameStarted += frame => RunOnUI(() =>
        {
            // 视频模式：上一帧开始新帧时，标记前一帧为 Completed
            if (OutputType == RenderOutputType.Video && CurrentFrame > 0 && CurrentFrame != frame)
            {
                var prev = GetOrCreateFrame(CurrentFrame);
                if (prev.Status == FrameStatus.Rendering)
                {
                    prev.Status = FrameStatus.Completed;
                    CompletedFrames++;
                }
            }

            CurrentFrame = frame;
            CurrentSample = 0;
            TotalSamples = 0;
            StatusText = $"渲染帧 {frame}...";

            // 标记当前帧为 Rendering
            var result = GetOrCreateFrame(frame);
            if (result.Status == FrameStatus.Pending)
                result.Status = FrameStatus.Rendering;

            // 同步网格缩略图状态（触发 ProgressRing 显示）
            var thumb = GridThumbnails.FirstOrDefault(t => t.FrameNumber == frame);
            if (thumb != null && thumb.Status != FrameStatus.Rendering)
                thumb.Status = FrameStatus.Rendering;

            UpdateProgress();
        });

        _engine.SampleProgress += (cur, total) => RunOnUI(() =>
        {
            CurrentSample = cur;
            TotalSamples = total;
            StatusText = $"帧 {CurrentFrame} - 采样 {cur}/{total}";
            UpdateProgress();
        });

        _engine.FrameSaved += (frame, path) => RunOnUI(async () =>
        {
            _autoPreviewVersion++; // 使上一帧的 LoadPreviewAsync 失效

            // 更新帧结果
            var result = GetOrCreateFrame(frame);

            // 续渲跳过帧：OnStart 已标记为 Completed，引擎 skip 循环会再次触发 FrameSaved。
            // 跳过重复处理，防止：1) CompletedFrames 双重计数 2) N 个并发解码打满 CPU
            if (result.Status == FrameStatus.Completed)
            {
                if (string.IsNullOrEmpty(result.OutputPath))
                    result.OutputPath = path;
                return;
            }

            CompletedFrames++;
            UpdateProgress();

            result.OutputPath = path;
            result.Status = FrameStatus.Completed;

            // 同步网格缩略图状态
            var savedThumb = GridThumbnails.FirstOrDefault(t => t.FrameNumber == frame);
            if (savedThumb != null)
            {
                savedThumb.Status = FrameStatus.Completed;
                savedThumb.OutputPath = path;
            }

            // 统一一次解码 960px，同时用于预览和缩略图（避免三次独立解码同一文件）
            var normalizedPath = path.Replace('/', '\\');
            if (!string.IsNullOrEmpty(normalizedPath) && File.Exists(normalizedPath))
            {
                try
                {
                    int version = _autoPreviewVersion;
                    using var decoded = await ImageHelper.DecodeAsync(normalizedPath, decodePixelWidth: 960);
                    if (decoded != null && version == _autoPreviewVersion)
                    {
                        SetRenderAspectRatio(decoded.PixelWidth, decoded.PixelHeight);

                        // 单帧预览
                        var previewSource = await ImageHelper.CreateSourceAsync(decoded);
                        if (previewSource != null && version == _autoPreviewVersion)
                            PreviewImage = previewSource;

                        // 网格缩略图（从缓存或预览同源，不再重新解码）
                        await UpdateGridThumbnailAsync(frame, normalizedPath);
                    }
                }
                catch { /* ignore */ }
            }

            // 黑帧检测（视频模式不做逐帧检测）
            if (EnableBlackFrameDetection && OutputType != RenderOutputType.Video)
                await AnalyzeFrameAsync(result);
        });

        _engine.BlenderMemoryUpdate += mem => RunOnUI(() => BlenderMemory = mem);

        _engine.SystemMemoryUpdate += (phys, commit) => RunOnUI(() =>
        {
            SystemMemPhys = phys;
            SystemMemCommit = commit;
        });

        _engine.FrameTimeUpdate += seconds => RunOnUI(() =>
        {
            CurrentFrameTime = seconds;
            UpdateEstimatedRemaining(seconds);
        });

        _engine.Log += (level, msg) => RunOnUI(() =>
        {
            AddLog(level, msg);
            if (level == "error")
            {
                var result = GetOrCreateFrame(CurrentFrame);
                result.Status = FrameStatus.Error;
                result.ErrorMessage = msg;
                ErrorFrameCount++;
            }
        });

        _engine.RenderCompleted += () => RunOnUI(() =>
        {
            // 用户主动停止时，保留当前进度，不覆盖
            if (_isStopping)
            {
                _isStopping = false;
                return;
            }

            // 标记所有仍在 Rendering 状态的帧为 Completed（视频模式最后一帧）
            foreach (var fr in FrameResults)
            {
                if (fr.Status == FrameStatus.Rendering)
                {
                    fr.Status = FrameStatus.Completed;
                    CompletedFrames++;
                }
            }

            // 确保当前帧显示为最后一帧（仅全部完成时）
            int totalFrames = OutputType == RenderOutputType.SingleFrame ? 1 : EndFrame - StartFrame + 1;
            if (CompletedFrames >= totalFrames)
            {
                int lastFrame = OutputType == RenderOutputType.SingleFrame ? SingleFrameNumber : EndFrame;
                if (CurrentFrame < lastFrame)
                    CurrentFrame = lastFrame;
            }

            IsRendering = false;
            IsRenderingLocally = false;
            OverallProgress = 100;
            EstimatedRemaining = "00:00";
            StatusText = OutputType == RenderOutputType.Video
                ? $"视频渲染完成"
                : OutputType == RenderOutputType.SingleFrame
                    ? $"单帧渲染完成"
                    : $"渲染完成 - {CompletedFrames} 帧, {BlackFrameCount} 黑帧, {ErrorFrameCount} 错误";
        });
    }

    // ── 命令实现 ────────────────────────────────────────────────────

    private async void OnStart()
    {
        if (IsRendering) return;

        try
        {

        // 保存当前配置
        SaveSettings();

        int actualStartFrame = StartFrame;
        int actualEndFrame = EndFrame;

        // 单帧模式：起止帧都设为单帧号
        if (OutputType == RenderOutputType.SingleFrame)
        {
            actualStartFrame = SingleFrameNumber;
            actualEndFrame = SingleFrameNumber;
        }

        // 检测是否有上次中断的进度文件（仅图片序列模式）
        int actualResumeFrame = actualStartFrame;
        bool isResuming = false;
        if (OutputType == RenderOutputType.ImageSequence)
        {
            var progressFile = ProjectProgressFilePath ?? SettingsService.ProgressFilePath;
            if (File.Exists(progressFile))
            {
                try
                {
                    var text = File.ReadAllText(progressFile).Trim();
                    if (int.TryParse(text, out int savedFrame) && savedFrame > actualStartFrame && savedFrame <= actualEndFrame)
                    {
                        if (AskResumeAsync != null)
                        {
                            string choice = await AskResumeAsync(savedFrame);
                            if (choice == "cancel") return;
                            if (choice == "restart")
                            {
                                // 删除进度文件
                                File.Delete(progressFile);
                                // 清理输出目录下的旧渲染帧文件
                                CleanOutputDirectory(OutputPattern, actualStartFrame, actualEndFrame);
                            }
                            else
                            {
                                actualResumeFrame = savedFrame;
                                isResuming = true;
                            }
                        }
                    }
                }
                catch { /* ignore */ }
            }
        }

        IsRendering = true;
        IsRenderingLocally = true;
        _isStopping = false;
        BlackFrameCount = 0;
        ErrorFrameCount = 0;
        CompletedFrames = 0;
        OverallProgress = 0;
        CurrentSample = 0;
        TotalSamples = 0;
        PreviewImage = null;
        EstimatedRemaining = "-";
        _frameTimes.Clear();
        _ewmaFrameTime = 0;
        _frameTimeSamples = 0;
        _renderAspectRatio = 0;
        _renderPixelWidth = 0;
        _renderPixelHeight = 0;
        ClearGridThumbnails();
        FrameResults.Clear();

        string modeLabel = OutputType switch
        {
            RenderOutputType.Video => "视频渲染",
            RenderOutputType.SingleFrame => $"单帧渲染 (帧 {SingleFrameNumber})",
            _ => "序列渲染",
        };
        StatusText = $"启动{modeLabel}...";

        // 预填充帧列表 + 同步创建网格缩略图占位符（批量插入期间抑制 CollectionChanged 重绘）
        IsBulkInserting = true;
        int alreadyDone = 0;
        for (int i = actualStartFrame; i <= actualEndFrame; i++)
        {
            var fr = new FrameResult { FrameNumber = i };
            if (i < actualResumeFrame)
            {
                fr.Status = FrameStatus.Completed;
                fr.OutputPath = RenderConfig.FindFrameFile(OutputPattern, i) ?? string.Empty;
                alreadyDone++;
            }
            FrameResults.Add(fr);

            // 网格占位符（FrameSaved 时填充图片）
            GridThumbnails.Add(new FrameThumbnail
            {
                FrameNumber = fr.FrameNumber,
                OutputPath = fr.OutputPath,
                Status = fr.Status,
            });
        }
        CompletedFrames = alreadyDone;
        IsBulkInserting = false;

        // 续渲时自动加载最后完成帧的预览
        if (isResuming && alreadyDone > 0)
        {
            var lastDone = FrameResults.LastOrDefault(f => f.Status == FrameStatus.Completed && !string.IsNullOrEmpty(f.OutputPath));
            if (lastDone != null)
                _ = LoadPreviewAsync(lastDone.OutputPath);
        }

        // 视频模式：批大小设为整个范围（不分批）
        int effectiveBatchSize = OutputType == RenderOutputType.Video
            ? actualEndFrame - actualStartFrame + 1
            : BatchSize;

        var config = new RenderConfig
        {
            BlenderPath = BlenderPath,
            BlendFilePath = BlendFilePath,
            OutputPath = OutputPattern,
            StartFrame = actualStartFrame,
            EndFrame = actualEndFrame,
            BatchSize = effectiveBatchSize,
            MemoryThreshold = MemoryThreshold,
            MemoryPollSeconds = MemoryPollSeconds,
            RestartDelaySeconds = RestartDelaySeconds,
            MaxAutoRestarts = MaxAutoRestarts,
            AutoRestartOnCrash = AutoRestartOnCrash,
            EnableBlackFrameDetection = EnableBlackFrameDetection && OutputType != RenderOutputType.Video,
            BlackFrameBrightnessThreshold = BlackFrameThreshold,
            ProgressFilePath = ProjectProgressFilePath ?? SettingsService.ProgressFilePath,
            OutputType = OutputType,
            SingleFrameNumber = SingleFrameNumber,
            SkipExistingFrames = isResuming,
            ProjectId = ProjectId,
        };

        _cts = new CancellationTokenSource();

            await Task.Run(() => _engine.StartAsync(config, _cts.Token));
        }
        catch (Exception ex)
        {
            AddLog("error", $"渲染异常: {ex.Message}");
        }
        finally
        {
            IsRendering = false;
            IsRenderingLocally = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnStop()
    {
        _isStopping = true; // 标记用户主动停止，防止 RenderCompleted 覆盖状态
        _cts?.Cancel();
        _engine.Stop();

        // 将所有 Rendering 状态的帧重置为 Pending（修复停止后 ProgressRing 不消失的 bug）
        foreach (var fr in FrameResults)
        {
            if (fr.Status == FrameStatus.Rendering)
                fr.Status = FrameStatus.Pending;
        }
        foreach (var thumb in GridThumbnails)
        {
            if (thumb.Status == FrameStatus.Rendering)
                thumb.Status = FrameStatus.Pending;
        }

        StatusText = "已停止";
        IsRendering = false;
    }

    /// <summary>
    /// 窗口关闭时调用，杀掉 Blender 进程、保存配置。
    /// </summary>
    public void Shutdown()
    {
        SaveSettings();
        _cts?.Cancel();
        _thumbnailCts?.Cancel();
        _engine.Stop();

        // 停止延迟释放定时器，防止窗口关闭后继续访问已释放对象
        if (_disposeTimer != null)
        {
            _disposeTimer.Stop();
            _disposeTimer = null;
        }
        // 立即释放所有待回收的资源
        FlushPendingDispose(forceAll: true);
    }

    // ── 配置持久化 ──────────────────────────────────────────────────

    private void LoadSettings()
    {
        var s = SettingsService.Load();
        _blenderPath = s.BlenderPath;
        _blendFilePath = s.BlendFilePath;
        _outputPath = s.OutputPath;
        _outputPrefix = s.OutputPrefix;
        _startFrame = s.StartFrame;
        _endFrame = s.EndFrame;
        _batchSize = s.BatchSize;
        _memoryThreshold = s.MemoryThreshold;
        _memoryPollSeconds = s.MemoryPollSeconds;
        _restartDelaySeconds = s.RestartDelaySeconds;
        _maxAutoRestarts = s.MaxAutoRestarts;
        _autoRestartOnCrash = s.AutoRestartOnCrash;
        _enableBlackFrameDetection = s.EnableBlackFrameDetection;
        _blackFrameThreshold = s.BlackFrameThreshold;
        _selectedOutputTypeIndex = s.OutputType;
        _singleFrameNumber = s.SingleFrameNumber;
        _showLogPanel = s.ShowLogPanel;
    }

    private void SaveSettings()
    {
        SettingsService.Save(new UserSettings
        {
            BlenderPath = BlenderPath,
            BlendFilePath = BlendFilePath,
            OutputPath = OutputPath,
            OutputPrefix = OutputPrefix,
            StartFrame = StartFrame,
            EndFrame = EndFrame,
            BatchSize = BatchSize,
            MemoryThreshold = MemoryThreshold,
            MemoryPollSeconds = MemoryPollSeconds,
            RestartDelaySeconds = RestartDelaySeconds,
            MaxAutoRestarts = MaxAutoRestarts,
            AutoRestartOnCrash = AutoRestartOnCrash,
            EnableBlackFrameDetection = EnableBlackFrameDetection,
            BlackFrameThreshold = BlackFrameThreshold,
            OutputType = SelectedOutputTypeIndex,
            SingleFrameNumber = SingleFrameNumber,
            ShowLogPanel = ShowLogPanel,
        });
    }

    // ── 帧分析 ──────────────────────────────────────────────────────

    private async Task AnalyzeFrameAsync(FrameResult frame)
    {
        if (string.IsNullOrEmpty(frame.OutputPath)) return;

        var result = await FrameAnalyzer.AnalyzeAsync(
            frame.OutputPath,
            BlackFrameThreshold);

        if (result != null)
        {
            frame.Brightness = result.AverageBrightness;
            if (result.IsBlackFrame)
            {
                frame.Status = FrameStatus.BlackFrame;
                BlackFrameCount++;
                // 同步网格缩略图状态（卡片变黄）
                var bfThumb = GridThumbnails.FirstOrDefault(t => t.FrameNumber == frame.FrameNumber);
                if (bfThumb != null) bfThumb.Status = FrameStatus.BlackFrame;
                AddLog("warn", $"帧 {frame.FrameNumber} 疑似黑图 (亮度={result.AverageBrightness:F1}, 标准差={result.StdDevBrightness:F1})");
            }
        }
    }

    // ── 预览加载 ────────────────────────────────────────────────────

    /// <summary>
    /// 自动预览加载（FrameSaved 触发），带请求合并 + 确定性内存释放：
    /// - SoftwareBitmapSource 替代 BitmapImage → IDisposable，Dispose 立即释放 D2D 纹理
    /// - BitmapDecoder + BitmapTransform 强制缩放 → 对 EXR/HDR/PNG 均有效
    ///   （BitmapImage.DecodePixelWidth 对 EXR 无效，WIC 仍会全分辨率解码 66MB/帧）
    /// - 请求合并：快速连续 FrameSaved 只加载最新帧，避免内存尖峰
    /// </summary>
    private async Task LoadPreviewAsync(string path)
    {
        _pendingPreviewPath = path;

        // 如果已有加载任务在运行，让它完成后处理最新路径
        if (_isLoadingPreview) return;
        _isLoadingPreview = true;

        try
        {
            while (_pendingPreviewPath != null)
            {
                var currentPath = _pendingPreviewPath;
                _pendingPreviewPath = null;
                int version = _autoPreviewVersion;

                currentPath = currentPath.Replace('/', '\\');
                if (!File.Exists(currentPath))
                {
                    AddLog("warn", $"预览文件不存在: {currentPath}");
                    continue;
                }

                // 释放旧预览（PreviewImage setter 自动 Dispose 旧 SoftwareBitmapSource）
                PreviewImage = null;

                // BitmapDecoder 强制缩放到 960px（对 EXR 等格式也生效）
                using var decoded = await ImageHelper.DecodeAsync(currentPath, decodePixelWidth: 960);
                if (version != _autoPreviewVersion || decoded == null) continue;

                // UI 线程上创建 SoftwareBitmapSource（SetBitmapAsync 内部复制数据）
                var source = await ImageHelper.CreateSourceAsync(decoded);
                if (version != _autoPreviewVersion || source == null) continue;

                PreviewImage = source;
                SetRenderAspectRatio(decoded.PixelWidth, decoded.PixelHeight);
            }
        }
        catch (Exception ex)
        {
            AddLog("warn", $"预览加载失败: {ex.Message}");
        }
        finally
        {
            _isLoadingPreview = false;
        }
    }

    /// <summary>当前渲染图像宽高比（未确定时返回 16:9 默认值）</summary>
    public double RenderAspectRatio => _renderAspectRatio > 0 ? _renderAspectRatio : 16.0 / 9.0;
    public int RenderPixelWidth => _renderPixelWidth;
    public int RenderPixelHeight => _renderPixelHeight;

    /// <summary>缓存渲染图像宽高比（仅首次有效）</summary>
    public void SetRenderAspectRatio(int pixelWidth, int pixelHeight)
    {
        if (_renderAspectRatio > 0 || pixelWidth <= 0 || pixelHeight <= 0) return;
        _renderAspectRatio = (double)pixelWidth / pixelHeight;
        _renderPixelWidth = pixelWidth;
        _renderPixelHeight = pixelHeight;
    }

    // ── 进度计算 ────────────────────────────────────────────────────

    private void UpdateProgress()
    {
        if (OutputType == RenderOutputType.SingleFrame)
        {
            // 单帧模式：基于采样进度
            OverallProgress = TotalSamples > 0 ? (double)CurrentSample / TotalSamples * 100 : 0;
            return;
        }

        int totalFrames = Math.Max(EndFrame - StartFrame + 1, 1);
        double sampleRatio = TotalSamples > 0 ? (double)CurrentSample / TotalSamples : 0;
        // 用已完成帧数 + 当前帧的采样进度来计算总进度
        double progress = (CompletedFrames + sampleRatio) / totalFrames * 100;
        OverallProgress = Math.Min(progress, 100);
    }

    private readonly Queue<double> _frameTimes = new();
    private double _ewmaFrameTime; // 指数加权移动平均
    private int _frameTimeSamples;

    private void UpdateEstimatedRemaining(double frameSeconds)
    {
        _frameTimes.Enqueue(frameSeconds);
        if (_frameTimes.Count > 30) _frameTimes.Dequeue();
        _frameTimeSamples++;

        // EWMA（指数加权移动平均），α=0.3 侧重近期帧速
        const double alpha = 0.3;
        if (_frameTimeSamples <= 3)
        {
            // 冷启动：简单平均
            _ewmaFrameTime = _frameTimes.Average();
        }
        else
        {
            // 剔除异常值（超过 EWMA 3 倍的帧，如重启延迟）
            if (frameSeconds < _ewmaFrameTime * 3)
                _ewmaFrameTime = alpha * frameSeconds + (1 - alpha) * _ewmaFrameTime;
        }

        int remaining = EndFrame - CurrentFrame;
        double totalSeconds = _ewmaFrameTime * remaining;

        var ts = TimeSpan.FromSeconds(totalSeconds);
        if (ts.TotalHours >= 1)
            EstimatedRemaining = $"约 {(int)ts.TotalHours} 小时 {ts.Minutes} 分钟";
        else if (ts.TotalMinutes >= 1)
            EstimatedRemaining = $"约 {(int)ts.TotalMinutes} 分钟";
        else
            EstimatedRemaining = $"{ts.Seconds} 秒";
    }

    // ── 网格缩略图 ────────────────────────────────────────────────────

    /// <summary>
    /// FrameSaved 时实时更新对应帧的网格缩略图。
    /// 找到已有占位符并加载图片，无论当前是否在网格视图（保证切换时已就绪）。
    /// </summary>
    private async Task UpdateGridThumbnailAsync(int frameNumber, string path)
    {
        var thumb = GridThumbnails.FirstOrDefault(t => t.FrameNumber == frameNumber);
        if (thumb == null) return;

        path = path.Replace('/', '\\');
        if (!File.Exists(path)) return;

        try
        {
            var decoded = await ImageHelper.DecodeAsync(path, decodePixelWidth: 240);
            if (decoded == null) return;

            // 同步写入磁盘缓存（130KB，耗时可忽略，必须在 CreateSourceAsync Dispose 之前）
            var cacheKey = ImageHelper.GetCacheKey(path);
            if (!string.IsNullOrEmpty(cacheKey))
            {
                var cachePath = ImageHelper.GetCachePath(SettingsService.ThumbnailCacheDir, cacheKey);
                ImageHelper.SaveThumbnailCache(decoded, cachePath);
            }

            var source = await ImageHelper.CreateSourceAsync(decoded);
            if (source != null)
            {
                // ScheduleDispose 旧 source，防止 GC finalizer 在错误线程释放 D2D 纹理
                if (thumb.Image != null && thumb.Image != source)
                    ScheduleDispose(thumb.Image);
                _gridCache.Put(path, source);
                thumb.Image = source;
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// 根据当前 FrameResults 刷新网格缩略图集合。
    /// 内存策略（仿 Windows 资源管理器）：
    /// - LRU 缓存命中的缩略图直接复用，不重新解码
    /// - 新加载的缩略图进入 LRU 缓存，超出容量时自动淘汰最旧条目
    /// - 后台并行解码 + UI 线程顺序创建 Source（避免 async void 崩溃）
    /// </summary>
    public async Task RefreshGridThumbnailsAsync()
    {
        // 取消上一次未完成的加载
        _thumbnailCts?.Cancel();
        _thumbnailCts = new CancellationTokenSource();
        var ct = _thumbnailCts.Token;

        foreach (var old in GridThumbnails)
        {
            ScheduleDispose(old.Image);
            old.Image = null;
        }
        GridThumbnails.Clear();
        // 必须同步清空缓存：ScheduleDispose 的 source 仍在 _gridCache 中，
        // 如果后续 _gridCache.Get 命中则会将已排队释放的 source 重新绑定到新 UI
        // → FlushPendingDispose 执行 Dispose 时 XAML 渲染线程仍在使用 → 0xC000027B
        _gridCache.Clear();

        var thumbsToLoad = new List<(FrameThumbnail thumb, string path)>();
        var cacheDir = SettingsService.ThumbnailCacheDir;
        foreach (var fr in FrameResults)
        {
            var thumb = new FrameThumbnail
            {
                FrameNumber = fr.FrameNumber,
                OutputPath = fr.OutputPath,
                Status = fr.Status,
            };
            GridThumbnails.Add(thumb);

            if (!string.IsNullOrEmpty(fr.OutputPath))
            {
                var path = fr.OutputPath.Replace('/', '\\');
                var cached = _gridCache.Get(path);
                if (cached != null)
                {
                    thumb.Image = cached;
                }
                else if (File.Exists(path))
                {
                    thumbsToLoad.Add((thumb, path));
                }
            }
        }

        // Step 1: 后台并行加载（优先磁盘缓存，缓存未命中才 WIC 解码）
        var decodeResults = new List<(FrameThumbnail thumb, string path, DecodedImage decoded, bool fromCache)>();
        int maxConcurrent = Math.Min(Environment.ProcessorCount, 16);
        var semaphore = new SemaphoreSlim(maxConcurrent);
        var tasks = thumbsToLoad.Select(async item =>
        {
            await semaphore.WaitAsync(CancellationToken.None);
            try
            {
                if (ct.IsCancellationRequested) return;

                DecodedImage? decoded = null;
                bool fromCache = false;

                var cacheKey = ImageHelper.GetCacheKey(item.path);
                if (!string.IsNullOrEmpty(cacheKey))
                {
                    var cachePath = ImageHelper.GetCachePath(cacheDir, cacheKey);
                    decoded = ImageHelper.LoadThumbnailCache(cachePath);
                    if (decoded != null) fromCache = true;
                }

                if (decoded == null && !ct.IsCancellationRequested)
                    decoded = await ImageHelper.DecodeAsync(item.path, decodePixelWidth: 240);

                if (decoded != null)
                    lock (decodeResults)
                        decodeResults.Add((item.thumb, item.path, decoded, fromCache));
            }
            finally
            {
                semaphore.Release();
            }
        });
        await Task.WhenAll(tasks);

        // Step 2: UI 线程分批创建 SoftwareBitmapSource（每 8 张 yield 一次，防止 UI 卡顿）
        int batchCount = 0;
        foreach (var item in decodeResults)
        {
            if (ct.IsCancellationRequested)
            {
                item.decoded?.Dispose();
                continue;
            }

            try
            {
                if (!item.fromCache)
                {
                    var cacheKey = ImageHelper.GetCacheKey(item.path);
                    if (!string.IsNullOrEmpty(cacheKey))
                    {
                        var cachePath = ImageHelper.GetCachePath(cacheDir, cacheKey);
                        ImageHelper.SaveThumbnailCache(item.decoded, cachePath);
                    }
                }

                var source = await Helpers.ImageHelper.CreateSourceAsync(item.decoded);
                if (source != null && !ct.IsCancellationRequested)
                {
                    _gridCache.Put(item.path, source);
                    item.thumb.Image = source;
                }
                else if (source != null)
                {
                    ScheduleDispose(source);
                }
            }
            catch
            {
                item.decoded?.Dispose();
            }

            // 每 8 张让出 UI 线程，允许处理输入事件
            if (++batchCount % 8 == 0)
                await Task.Delay(1);
        }
    }

    /// <summary>
    /// 仅加载缺失的网格缩略图（已有 Image 的不重新加载）。
    /// 用于续渲后首次切换到网格视图——跳过帧的占位符有 OutputPath 但无 Image。
    /// 不清空集合，不破坏已有缩略图。
    /// </summary>
    public async Task LoadMissingThumbnailsAsync()
    {
        // 取消上一次未完成的加载
        _thumbnailCts?.Cancel();
        _thumbnailCts = new CancellationTokenSource();
        var ct = _thumbnailCts.Token;

        var thumbsToLoad = new List<(FrameThumbnail thumb, string path)>();
        foreach (var thumb in GridThumbnails)
        {
            if (thumb.Image != null || string.IsNullOrEmpty(thumb.OutputPath)) continue;
            var path = thumb.OutputPath.Replace('/', '\\');
            var cached = _gridCache.Get(path);
            if (cached != null)
            {
                thumb.Image = cached;
            }
            else if (File.Exists(path))
            {
                thumbsToLoad.Add((thumb, path));
            }
        }

        if (thumbsToLoad.Count == 0) return;

        // 后台并行加载（优先磁盘缓存）
        var cacheDir = SettingsService.ThumbnailCacheDir;
        var decodeResults = new List<(FrameThumbnail thumb, string path, DecodedImage decoded, bool fromCache)>();
        int maxConcurrent = Math.Min(Environment.ProcessorCount, 16);
        var semaphore = new SemaphoreSlim(maxConcurrent);
        var tasks = thumbsToLoad.Select(async item =>
        {
            await semaphore.WaitAsync(CancellationToken.None);
            try
            {
                if (ct.IsCancellationRequested) return;

                DecodedImage? decoded = null;
                bool fromCache = false;

                var cacheKey = ImageHelper.GetCacheKey(item.path);
                if (!string.IsNullOrEmpty(cacheKey))
                {
                    var cachePath = ImageHelper.GetCachePath(cacheDir, cacheKey);
                    decoded = ImageHelper.LoadThumbnailCache(cachePath);
                    if (decoded != null) fromCache = true;
                }

                if (decoded == null && !ct.IsCancellationRequested)
                    decoded = await Helpers.ImageHelper.DecodeAsync(item.path, decodePixelWidth: 240);

                if (decoded != null)
                    lock (decodeResults)
                        decodeResults.Add((item.thumb, item.path, decoded, fromCache));
            }
            finally { semaphore.Release(); }
        });
        await Task.WhenAll(tasks);

        // UI 线程分批创建 SoftwareBitmapSource（每 8 张 yield 一次，防止 UI 卡顿）
        int batchCount = 0;
        foreach (var item in decodeResults)
        {
            // 视图已切走或页面已卸载 → 释放已解码数据，停止创建 D2D surface
            if (ct.IsCancellationRequested)
            {
                item.decoded?.Dispose();
                continue;
            }

            try
            {
                if (!item.fromCache)
                {
                    var cacheKey = ImageHelper.GetCacheKey(item.path);
                    if (!string.IsNullOrEmpty(cacheKey))
                    {
                        var cachePath = ImageHelper.GetCachePath(cacheDir, cacheKey);
                        ImageHelper.SaveThumbnailCache(item.decoded, cachePath);
                    }
                }

                var source = await Helpers.ImageHelper.CreateSourceAsync(item.decoded);
                if (source != null && !ct.IsCancellationRequested)
                {
                    _gridCache.Put(item.path, source);
                    item.thumb.Image = source;
                }
                else if (source != null)
                {
                    // 已取消但 source 已创建 → 必须 ScheduleDispose 避免 GC finalizer 崩溃
                    ScheduleDispose(source);
                }
            }
            catch
            {
                item.decoded?.Dispose();
            }

            if (++batchCount % 8 == 0)
                await Task.Delay(1);
        }
    }

    /// <summary>释放网格缩略图占用的内存（SoftwareBitmapSource 必须在 UI 线程 Dispose）</summary>
    public void ClearGridThumbnails()
    {
        foreach (var thumb in GridThumbnails)
        {
            // 必须通过 ScheduleDispose 在 UI 线程延迟释放 D2D 纹理
            // 直接设 null 会导致 GC finalizer 在非 UI 线程调用 Close → FailFast 0xC000027B
            ScheduleDispose(thumb.Image);
            thumb.Image = null;
        }
        GridThumbnails.Clear();
        _gridCache.Clear();
    }

    // ── 日志 ────────────────────────────────────────────────────────

    private void AddLog(string level, string message)
    {
        LogEntries.Add(new LogEntry(DateTime.Now, level, message));
        while (LogEntries.Count > 1000)
            LogEntries.RemoveAt(0);
    }

    // ── 帧查找 ──────────────────────────────────────────────────────

    private FrameResult GetOrCreateFrame(int frameNumber)
    {
        // 单帧模式偏移量为0
        int baseFrame = OutputType == RenderOutputType.SingleFrame ? SingleFrameNumber : StartFrame;
        int idx = frameNumber - baseFrame;
        if (idx >= 0 && idx < FrameResults.Count)
            return FrameResults[idx];

        var result = new FrameResult { FrameNumber = frameNumber };
        FrameResults.Add(result);
        return result;
    }

    // ── 清理旧渲染输出 ─────────────────────────────────────────────

    /// <summary>
    /// "从头开始"时删除输出目录下指定帧范围内的已有渲染文件。
    /// 仅删除可识别的帧文件，不影响其他内容。
    /// </summary>
    private void CleanOutputDirectory(string outputPattern, int startFrame, int endFrame)
    {
        if (string.IsNullOrEmpty(outputPattern)) return;
        // outputPattern 是含 ##### 的完整模式路径，取父目录检查
        string? dir = Path.GetDirectoryName(outputPattern);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        int cleaned = 0;
        for (int i = startFrame; i <= endFrame; i++)
        {
            var file = RenderConfig.FindFrameFile(outputPattern, i);
            if (file != null)
            {
                try { File.Delete(file); cleaned++; }
                catch { /* 文件被占用等，忽略 */ }
            }
        }
        if (cleaned > 0)
            AddLog("info", $"已清理 {cleaned} 个旧渲染帧文件");
    }

    // ── UI 线程调度 ─────────────────────────────────────────────────

    private void RunOnUI(Action action) => _safeDispatcher.Run(action);
    private void RunOnUI(Func<Task> asyncAction) => _safeDispatcher.RunAsync(asyncAction);

    private void RaiseCommands()
    {
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
    }

    // ── 延迟释放 D2D 纹理（防止 0xC000027B 闪退）─────────────────────
    // WinUI 合成渲染线程异步于 UI 线程，Dispose 时渲染线程可能仍在引用旧纹理。
    // 延迟 2 秒再释放，确保渲染线程完成当前帧后才回收。

    private static readonly Queue<(IDisposable resource, DateTime disposeAfter)> _pendingDispose = new();
    private static DispatcherTimer? _disposeTimer;

    private void ScheduleDispose(object? resource)
    {
        if (resource is not IDisposable disposable) return;

        System.Diagnostics.Trace.WriteLine($"[VM] ScheduleDispose: {resource.GetType().Name}, pendingCount={_pendingDispose.Count}");
        lock (_pendingDispose)
        {
            _pendingDispose.Enqueue((disposable, DateTime.UtcNow.AddSeconds(5)));
        }

        if (_disposeTimer == null)
        {
            _disposeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _disposeTimer.Tick += (_, _) => FlushPendingDispose();
            _disposeTimer.Start();
        }
    }

    private static void FlushPendingDispose(bool forceAll = false)
    {
        // 有 SetBitmapAsync 正在使用 D2D device 时，跳过本轮 Dispose，等下次 Tick 再处理。
        // 避免 Dispose 旧 SoftwareBitmapSource 与 SetBitmapAsync 竞争同一 D2D device → 0xC000027B。
        if (!forceAll && ImageHelper._activeSourceCreations > 0)
        {
            System.Diagnostics.Trace.WriteLine($"[VM] FlushDispose 跳过: activeSourceCreations={ImageHelper._activeSourceCreations}");
            return;
        }

        var now = DateTime.UtcNow;
        lock (_pendingDispose)
        {
            while (_pendingDispose.Count > 0 && (forceAll || _pendingDispose.Peek().disposeAfter <= now))
            {
                var item = _pendingDispose.Dequeue();
                System.Diagnostics.Trace.WriteLine($"[VM] FlushDispose: {item.resource.GetType().Name}, force={forceAll}");
                try { item.resource.Dispose(); }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[VM] !! FlushDispose 异常: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }
}

// ── 日志条目 ────────────────────────────────────────────────────────

public record LogEntry(DateTime Time, string Level, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");

    public string LevelIcon => Level switch
    {
        "saved" => "\u2705",
        "error" => "\u274C",
        "warn" => "\u26A0",
        "batch" => "\uD83D\uDCE6",
        "resume" => "\u267B",
        "done" => "\uD83C\uDFC1",
        "interrupt" => "\u23F8",
        "skip" => "\u23ED",
        _ => "\u2139"
    };
}
