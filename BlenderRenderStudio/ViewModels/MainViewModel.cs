using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using BlenderRenderStudio.Helpers;
using BlenderRenderStudio.Models;
using BlenderRenderStudio.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BlenderRenderStudio.ViewModels;

public class MainViewModel : ObservableObject
{
    private readonly RenderEngine _engine = new();
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _cts;

    // ── 配置属性 ────────────────────────────────────────────────────

    private string _blenderPath = @"D:\SteamLibrary\steamapps\common\Blender\blender.exe";
    private string _blendFilePath = string.Empty;
    private string _outputPath = string.Empty;
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
    private BitmapImage? _previewImage;
    private string _statusText = "就绪";

    public bool IsRendering { get => _isRendering; set { SetProperty(ref _isRendering, value); OnPropertyChanged(nameof(IsNotRendering)); RaiseCommands(); } }
    public bool IsNotRendering => !IsRendering;
    public double OverallProgress { get => _overallProgress; set => SetProperty(ref _overallProgress, value); }
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
    public BitmapImage? PreviewImage { get => _previewImage; set => SetProperty(ref _previewImage, value); }
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    // ── 集合 ────────────────────────────────────────────────────────

    public ObservableCollection<FrameResult> FrameResults { get; } = [];
    public ObservableCollection<LogEntry> LogEntries { get; } = [];

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

    public MainViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;

        // 加载上次保存的配置
        LoadSettings();

        StartCommand = new RelayCommand(OnStart, () => !IsRendering && !string.IsNullOrEmpty(BlendFilePath));
        StopCommand = new RelayCommand(OnStop, () => IsRendering);
        ClearLogCommand = new RelayCommand(() => LogEntries.Clear());

        // 挂载引擎事件
        _engine.FrameStarted += frame => RunOnUI(() =>
        {
            CurrentFrame = frame;
            CurrentSample = 0;
            TotalSamples = 0;
            StatusText = $"渲染帧 {frame}...";
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
            CompletedFrames++;
            UpdateProgress();

            // 更新帧结果
            var result = GetOrCreateFrame(frame);
            result.OutputPath = path;
            result.Status = FrameStatus.Completed;

            // 加载预览
            await LoadPreviewAsync(path);

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
            IsRendering = false;
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
        if (OutputType == RenderOutputType.ImageSequence)
        {
            var progressFile = SettingsService.ProgressFilePath;
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
                                File.Delete(progressFile);
                            else
                                actualResumeFrame = savedFrame;
                        }
                    }
                }
                catch { /* ignore */ }
            }
        }

        IsRendering = true;
        BlackFrameCount = 0;
        ErrorFrameCount = 0;
        FrameResults.Clear();

        string modeLabel = OutputType switch
        {
            RenderOutputType.Video => "视频渲染",
            RenderOutputType.SingleFrame => $"单帧渲染 (帧 {SingleFrameNumber})",
            _ => "序列渲染",
        };
        StatusText = $"启动{modeLabel}...";

        // 预填充帧列表
        int alreadyDone = 0;
        for (int i = actualStartFrame; i <= actualEndFrame; i++)
        {
            var fr = new FrameResult { FrameNumber = i };
            if (i < actualResumeFrame)
            {
                fr.Status = FrameStatus.Completed;
                fr.OutputPath = RenderConfig.FindFrameFile(OutputPath, i) ?? string.Empty;
                alreadyDone++;
            }
            FrameResults.Add(fr);
        }
        CompletedFrames = alreadyDone;

        // 视频模式：批大小设为整个范围（不分批）
        int effectiveBatchSize = OutputType == RenderOutputType.Video
            ? actualEndFrame - actualStartFrame + 1
            : BatchSize;

        var config = new RenderConfig
        {
            BlenderPath = BlenderPath,
            BlendFilePath = BlendFilePath,
            OutputPath = OutputPath,
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
            ProgressFilePath = SettingsService.ProgressFilePath,
            OutputType = OutputType,
            SingleFrameNumber = SingleFrameNumber,
        };

        _cts = new CancellationTokenSource();

        try
        {
            await Task.Run(() => _engine.StartAsync(config, _cts.Token));
        }
        catch (Exception ex)
        {
            AddLog("error", $"渲染异常: {ex.Message}");
        }
        finally
        {
            IsRendering = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnStop()
    {
        _cts?.Cancel();
        _engine.Stop();
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
        _engine.Stop();
    }

    // ── 配置持久化 ──────────────────────────────────────────────────

    private void LoadSettings()
    {
        var s = SettingsService.Load();
        _blenderPath = s.BlenderPath;
        _blendFilePath = s.BlendFilePath;
        _outputPath = s.OutputPath;
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
    }

    private void SaveSettings()
    {
        SettingsService.Save(new UserSettings
        {
            BlenderPath = BlenderPath,
            BlendFilePath = BlendFilePath,
            OutputPath = OutputPath,
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
                AddLog("warn", $"帧 {frame.FrameNumber} 疑似黑图 (亮度={result.AverageBrightness:F1}, 标准差={result.StdDevBrightness:F1})");
            }
        }
    }

    // ── 预览加载 ────────────────────────────────────────────────────

    private async Task LoadPreviewAsync(string path)
    {
        try
        {
            // 规范化路径分隔符（Blender 可能输出正斜杠）
            path = path.Replace('/', '\\');

            if (!File.Exists(path))
            {
                AddLog("warn", $"预览文件不存在: {path}");
                return;
            }

            // 从磁盘读取字节到内存，避免 BitmapImage 的 URI 缓存问题
            var bytes = await File.ReadAllBytesAsync(path);
            var ms = new MemoryStream(bytes);
            var stream = ms.AsRandomAccessStream();
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            PreviewImage = bitmap;
        }
        catch (Exception ex)
        {
            AddLog("warn", $"预览加载失败: {ex.Message}");
        }
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
        double frameOffset = Math.Max(CurrentFrame - StartFrame, 0);
        OverallProgress = Math.Min((frameOffset + sampleRatio) / totalFrames * 100, 100);
    }

    private readonly Queue<double> _frameTimes = new();

    private void UpdateEstimatedRemaining(double frameSeconds)
    {
        _frameTimes.Enqueue(frameSeconds);
        if (_frameTimes.Count > 20) _frameTimes.Dequeue(); // 滑动窗口

        double avg = _frameTimes.Average();
        int remaining = EndFrame - CurrentFrame;
        double totalSeconds = avg * remaining;

        var ts = TimeSpan.FromSeconds(totalSeconds);
        EstimatedRemaining = ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    // ── 日志 ────────────────────────────────────────────────────────

    private void AddLog(string level, string message)
    {
        LogEntries.Add(new LogEntry(DateTime.Now, level, message));
        while (LogEntries.Count > 5000)
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

    // ── UI 线程调度 ─────────────────────────────────────────────────

    private void RunOnUI(Action action)
    {
        if (_dispatcher.HasThreadAccess) action();
        else _dispatcher.TryEnqueue(() => action());
    }

    private void RunOnUI(Func<Task> asyncAction)
    {
        if (_dispatcher.HasThreadAccess) _ = asyncAction();
        else _dispatcher.TryEnqueue(() => _ = asyncAction());
    }

    private void RaiseCommands()
    {
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
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
