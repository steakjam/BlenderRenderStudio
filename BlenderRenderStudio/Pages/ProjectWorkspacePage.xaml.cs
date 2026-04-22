using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using IO = System.IO;
using BlenderRenderStudio.Helpers;
using BlenderRenderStudio.Models;
using BlenderRenderStudio.Services;
using BlenderRenderStudio.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage.Pickers;

namespace BlenderRenderStudio.Pages;

/// <summary>
/// 项目渲染工作区页面：左侧参数 + 右侧状态/预览/时间轴/日志。
/// 从 MainWindow 导航进入，携带 RenderProject 数据。
/// </summary>
public sealed partial class ProjectWorkspacePage : Page
{
    private readonly RenderProject _project;
    private readonly SafeDispatcher _safeDispatcher;
    private RenderQueueService? _queueService;
    private MainWindow? _mainWindow;

    public MainViewModel ViewModel { get; }

    // ── 时间轴绘制 ──────────────────────────────────────────────────
    private double _frameWidth = 24;
    private bool _isDraggingTimeline;
    private int _selectedFrameIndex = -1;

    // ── 拖拽防抖 ────────────────────────────────────────────────────
    private DispatcherTimer? _dragDebounceTimer;
    private int _pendingDragFrame = -1;

    // ── 播放 ────────────────────────────────────────────────────────
    private DispatcherTimer? _playbackTimer;

    // ── 预览缩放 ────────────────────────────────────────────────────
    private double _previewScaleFactor = 1.0;

    // ── 时间轴颜色同步节流 ──────────────────────────────────────────
    private DispatcherTimer? _timelineThrottleTimer;
    private bool _timelineDirty;

    // ── 网格视图列数 ────────────────────────────────────────────────
    private int _gridColumns = 4;

    // ── 网格虚拟化滚动 ──────────────────────────────────────────────
    private ScrollViewer? _gridScrollViewer;
    private DispatcherTimer? _scrollDebounceTimer;
    private bool _scrollDirty;
    private double _lastScrollOffset; // 滚动方向检测

    /// <param name="existingViewModel">传入已有 ViewModel 时复用（保留渲染状态），null 则创建新实例</param>
    public ProjectWorkspacePage(RenderProject project, SafeDispatcher safeDispatcher, MainViewModel? existingViewModel = null)
    {
        _project = project;
        _safeDispatcher = safeDispatcher;

        System.Diagnostics.Trace.WriteLine($"[WORKSPACE] 构造开始: project='{project.Name}', existingVM={existingViewModel != null}, hash={GetHashCode()}");

        // 复用或创建 ViewModel（在 InitializeComponent 之前，x:Bind 需要）
        if (existingViewModel != null)
        {
            ViewModel = existingViewModel;
            System.Diagnostics.Trace.WriteLine($"[WORKSPACE] 复用 ViewModel, PreviewImage={ViewModel.PreviewImage != null}, FrameResults={ViewModel.FrameResults.Count}, GridThumbnails={ViewModel.GridThumbnails.Count}");
        }
        else
        {
            ViewModel = new MainViewModel(safeDispatcher);
            ApplyProjectToViewModel();
            System.Diagnostics.Trace.WriteLine($"[WORKSPACE] 创建新 ViewModel");
        }

        System.Diagnostics.Trace.WriteLine($"[WORKSPACE] 调用 InitializeComponent");
        InitializeComponent();
        System.Diagnostics.Trace.WriteLine($"[WORKSPACE] InitializeComponent 完成");

        ProjectTitle.Text = _project.Name;

        // 同步按钮状态到 ViewModel（ViewModel 复用时 IsGridView 可能为 true，但 XAML 默认单帧选中）
        if (ViewModel.IsGridView)
        {
            BtnSingleView.IsChecked = false;
            BtnGridView.IsChecked = true;
        }

        // 恢复全窗口预览状态
        if (ViewModel.IsFullscreenPreview)
            SetFullWindowMode(true);

        // 注入续渲对话框（每次新建页面都要重新绑定，因为需要当前页面的 XamlRoot）
        ViewModel.AskResumeAsync = AskResumeDialogAsync;

        // 监听帧列表变化 → 更新时间轴
        ViewModel.FrameResults.CollectionChanged += FrameResults_CollectionChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        // 监听项目模型变化（RenderRecovery 轮询更新 project.Status/CompletedFrames）
        _project.PropertyChanged += Project_PropertyChanged;

        // 页面加载时恢复已渲染帧预览 + 隐藏 NumberBox 内部清除按钮
        Loaded += OnLoaded;
        Loaded += (_, _) => HideNumberBoxDeleteButtons(this);

        // 页面卸载时清理
        Unloaded += OnUnloaded;

        System.Diagnostics.Trace.WriteLine($"[WORKSPACE] 构造完成: hash={GetHashCode()}");
    }

    /// <summary>注入队列服务 + 主窗口引用（由 MainWindow 调用）</summary>
    public void SetQueueService(RenderQueueService service) => _queueService = service;

    /// <summary>注入主窗口引用（用于渲染中缓存页面）</summary>
    public void SetMainWindow(MainWindow window) => _mainWindow = window;

    private void AddToQueue_Click(object sender, RoutedEventArgs e)
    {
        // 先保存当前配置到项目
        SaveViewModelToProject();

        if (_queueService == null) return;
        _queueService.Enqueue(_project.Id);
    }

    // ── 项目 ↔ ViewModel 数据同步 ──────────────────────────────────

    private void ApplyProjectToViewModel()
    {
        // BlenderPath 始终从全局设置读取（用户在设置页统一配置），不使用项目快照
        ViewModel.BlenderPath = SettingsService.Load().BlenderPath;
        ViewModel.BlendFilePath = _project.BlendFilePath;
        ViewModel.OutputPath = _project.OutputDirectory;
        ViewModel.OutputPrefix = _project.OutputPrefix;
        ViewModel.StartFrame = _project.StartFrame;
        ViewModel.EndFrame = _project.EndFrame;
        ViewModel.BatchSize = _project.BatchSize;
        ViewModel.SelectedOutputTypeIndex = _project.OutputType;
        ViewModel.SingleFrameNumber = _project.SingleFrameNumber;
        ViewModel.MemoryThreshold = _project.MemoryThreshold;
        ViewModel.MemoryPollSeconds = _project.MemoryPollSeconds;
        ViewModel.RestartDelaySeconds = _project.RestartDelaySeconds;
        ViewModel.MaxAutoRestarts = _project.MaxAutoRestarts;
        ViewModel.AutoRestartOnCrash = _project.AutoRestartOnCrash;
        ViewModel.EnableBlackFrameDetection = _project.EnableBlackFrameDetection;
        ViewModel.BlackFrameThreshold = _project.BlackFrameThreshold;

        // 项目级进度文件（隔离不同项目的渲染进度）
        IO.Directory.CreateDirectory(_project.CacheDirectory);
        ViewModel.ProjectProgressFilePath = _project.ProgressFilePath;
        ViewModel.ProjectId = _project.Id;

        // 闪退恢复：若项目处于渲染中（由 RenderRecovery 恢复），
        // 同步到 ViewModel 使左侧参数栏禁用 + 开始按钮不可点击
        if (_project.Status == ProjectStatus.Rendering)
        {
            ViewModel.IsRendering = true;
            ViewModel.CompletedFrames = _project.CompletedFrames;
            ViewModel.StatusText = $"渲染恢复中 - 已完成 {_project.CompletedFrames} 帧（由外部 Blender 进程渲染）";
        }
    }

    /// <summary>将当前 ViewModel 状态保存回项目模型</summary>
    private void SaveViewModelToProject()
    {
        _project.BlenderPath = ViewModel.BlenderPath;
        _project.BlendFilePath = ViewModel.BlendFilePath;
        _project.OutputDirectory = ViewModel.OutputPath;
        _project.OutputPrefix = ViewModel.OutputPrefix;
        _project.StartFrame = ViewModel.StartFrame;
        _project.EndFrame = ViewModel.EndFrame;
        _project.BatchSize = ViewModel.BatchSize;
        _project.OutputType = ViewModel.SelectedOutputTypeIndex;
        _project.SingleFrameNumber = ViewModel.SingleFrameNumber;
        _project.MemoryThreshold = ViewModel.MemoryThreshold;
        _project.MemoryPollSeconds = ViewModel.MemoryPollSeconds;
        _project.RestartDelaySeconds = ViewModel.RestartDelaySeconds;
        _project.MaxAutoRestarts = ViewModel.MaxAutoRestarts;
        _project.AutoRestartOnCrash = ViewModel.AutoRestartOnCrash;
        _project.EnableBlackFrameDetection = ViewModel.EnableBlackFrameDetection;
        _project.BlackFrameThreshold = ViewModel.BlackFrameThreshold;
        _project.CompletedFrames = ViewModel.CompletedFrames;
        _project.LastRenderedFrame = ViewModel.CurrentFrame;

        // 持久化
        ProjectService.Update(_project);
    }

    // ── 页面加载：恢复已渲染帧 + 预览 ────────────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Trace.WriteLine($"[WORKSPACE] OnLoaded 开始: hash={GetHashCode()}, IsLoaded={IsLoaded}, XamlRoot={XamlRoot != null}");
        try
        {
            await RestoreExistingFramesAsync();
            System.Diagnostics.Trace.WriteLine($"[WORKSPACE] OnLoaded RestoreExistingFramesAsync 完成: hash={GetHashCode()}");

            // 如果处于网格视图，加载可见范围内的缩略图
            if (ViewModel.IsGridView && ViewModel.GridThumbnails.Count > 0)
            {
                int visibleCount = GetGridVisibleItemCount();
                ViewModel.LoadThumbnailRange(0, visibleCount - 1);
                HookGridScrollViewer();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[WORKSPACE] OnLoaded 异常: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// 递归查找页面中所有 NumberBox，隐藏其内部 TextBox 的清除按钮(X)。
    /// 使用 MaxWidth=0 而非 Visibility.Collapsed，确保不被 VisualStateManager 覆盖。
    /// </summary>
    private static void HideNumberBoxDeleteButtons(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is NumberBox nb)
            {
                // NumberBox 内部的 TextBox 模板在首次渲染后才生成
                // 通过 LayoutUpdated 确保模板已加载
                void handler(object? s, object ea)
                {
                    var textBox = FindChild<TextBox>(nb);
                    if (textBox == null) return;
                    textBox.ApplyTemplate();
                    var deleteBtn = FindChildByName(textBox, "DeleteButton");
                    if (deleteBtn != null)
                    {
                        deleteBtn.MaxWidth = 0;
                        deleteBtn.MaxHeight = 0;
                        nb.LayoutUpdated -= handler;
                    }
                }
                nb.LayoutUpdated += handler;
            }
            else
            {
                HideNumberBoxDeleteButtons(child);
            }
        }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) return result;
            var deeper = FindChild<T>(child);
            if (deeper != null) return deeper;
        }
        return null;
    }

    private static FrameworkElement? FindChildByName(DependencyObject parent, string name)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement fe && fe.Name == name) return fe;
            var deeper = FindChildByName(child, name);
            if (deeper != null) return deeper;
        }
        return null;
    }

    /// <summary>
    /// 进入项目时扫描输出目录，恢复已渲染帧列表、进度条、预览。
    /// 视频模式跳过（视频输出为单文件，不逐帧扫描）。
    /// </summary>
    private async Task RestoreExistingFramesAsync()
    {
        // 本地引擎渲染中 → 不清空帧数据，但需要重建缩略图（OnUnloaded 已清除 D2D surface）
        if (ViewModel.IsRendering && ViewModel.IsRenderingLocally)
        {
            System.Diagnostics.Trace.WriteLine($"[RestoreFrames] 渲染中重入，重建缩略图");
            ViewModel.RefreshGridThumbnails();
            HookGridScrollViewer();
            var currentResult = ViewModel.FrameResults.LastOrDefault(f => f.Status == FrameStatus.Completed);
            if (currentResult != null && !string.IsNullOrEmpty(currentResult.OutputPath))
                LoadPreviewForFrame(currentResult.FrameNumber);
            return;
        }

        var outputPath = ViewModel.OutputPattern;
        System.Diagnostics.Trace.WriteLine($"[RestoreFrames] OutputPattern='{outputPath}', OutputType={ViewModel.OutputType}, Start={ViewModel.StartFrame}, End={ViewModel.EndFrame}");
        if (string.IsNullOrEmpty(outputPath)) return;
        if (ViewModel.OutputType == RenderOutputType.Video) return;

        int start, end;
        if (ViewModel.OutputType == RenderOutputType.SingleFrame)
        {
            start = end = ViewModel.SingleFrameNumber;
        }
        else
        {
            start = ViewModel.StartFrame;
            end = ViewModel.EndFrame;
        }

        if (start > end) return;

        // 后台线程扫描磁盘，避免阻塞 UI
        var foundFrames = await Task.Run(() =>
        {
            // outputPath 是完整模式路径（含 #####），取父目录检查存在性
            string? dir = IO.Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(dir) || !IO.Directory.Exists(dir))
                return new List<(int frame, string path)>();

            var results = new List<(int frame, string path)>();
            for (int i = start; i <= end; i++)
            {
                var file = RenderConfig.FindFrameFile(outputPath, i);
                if (file != null)
                    results.Add((i, file));
            }
            return results;
        });

        System.Diagnostics.Trace.WriteLine($"[RestoreFrames] 扫描完成, 找到 {foundFrames.Count} 帧");

        if (foundFrames.Count == 0)
        {
            // 无已渲染帧 — 检查 blend 文件是否存在并提示
            if (!string.IsNullOrEmpty(ViewModel.BlendFilePath) && !IO.File.Exists(ViewModel.BlendFilePath))
                ViewModel.StatusText = "⚠ Blend 文件不存在，请重新选择";
            return;
        }

        // 填充帧列表 + 网格占位符（try-finally 保护 IsBulkInserting 标记）
        ViewModel.IsBulkInserting = true;
        try
        {
            ViewModel.FrameResults.Clear();
            ViewModel.GridThumbnails.Clear();

            for (int i = start; i <= end; i++)
            {
                var found = foundFrames.FirstOrDefault(f => f.frame == i);
                var fr = new FrameResult
                {
                    FrameNumber = i,
                    Status = found.path != null ? FrameStatus.Completed : FrameStatus.Pending,
                    OutputPath = found.path ?? string.Empty,
                };
                ViewModel.FrameResults.Add(fr);
                var thumbNew = new FrameThumbnail
                {
                    FrameNumber = i,
                    OutputPath = fr.OutputPath,
                    Status = fr.Status,
                };
                if (!string.IsNullOrEmpty(fr.OutputPath))
                    thumbNew.CacheKey = Helpers.ImageHelper.GetCacheKey(fr.OutputPath.Replace('/', '\\'));
                ViewModel.GridThumbnails.Add(thumbNew);
            }

            ViewModel.CompletedFrames = foundFrames.Count;
        }
        finally
        {
            ViewModel.IsBulkInserting = false;
        }

        // 更新进度显示
        int totalFrames = end - start + 1;
        if (foundFrames.Count >= totalFrames)
        {
            ViewModel.OverallProgress = 100;
            ViewModel.StatusText = $"已完成 - {foundFrames.Count} 帧";
        }
        else
        {
            ViewModel.OverallProgress = (double)foundFrames.Count / totalFrames * 100;
            ViewModel.StatusText = $"已渲染 {foundFrames.Count}/{totalFrames} 帧";
        }

        // 定位播放头到最后完成帧，并加载预览
        if (!IsLoaded) return; // 页面已卸载，跳过预览加载
        var lastCompleted = foundFrames[^1];
        _selectedFrameIndex = lastCompleted.frame - start;
        System.Diagnostics.Trace.WriteLine($"[RestoreFrames] 准备加载预览: frame={lastCompleted.frame}, pageHash={GetHashCode()}, IsLoaded={IsLoaded}");
        RedrawTimeline();
        LoadPreviewForFrame(lastCompleted.frame);
    }

    // ── 续渲对话框 ──────────────────────────────────────────────────

    private async Task<string> AskResumeDialogAsync(int lastCompletedFrame)
    {
        var dialog = new ContentDialog
        {
            Title = "检测到未完成渲染",
            Content = $"已渲染到第 {lastCompletedFrame} 帧（共 {ViewModel.EndFrame - ViewModel.StartFrame + 1} 帧），是否从此处继续？",
            PrimaryButtonText = "续渲",
            SecondaryButtonText = "从头开始",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => "resume",
            ContentDialogResult.Secondary => "restart",
            _ => "cancel",
        };
    }

    // ── 文件浏览 ────────────────────────────────────────────────────

    private async void BrowseBlendFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".blend");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                BlendFileBox.Text = file.Path;
                ViewModel.BlendFilePath = file.Path;

                // 自动填充输出目录（Blend 文件同级 Render 子目录）
                if (string.IsNullOrEmpty(ViewModel.OutputPath))
                {
                    var dir = IO.Path.GetDirectoryName(file.Path);
                    if (dir != null)
                        ViewModel.OutputPath = IO.Path.Combine(dir, "Render");
                }

                // 立即持久化，防止崩溃/强退丢失
                SaveViewModelToProject();
            }
        }
        catch { /* picker cancelled or error */ }
    }

    private async void BrowseOutputPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                OutputPathBox.Text = folder.Path;
                ViewModel.OutputPath = folder.Path;
                SaveViewModelToProject();
            }
        }
        catch { /* picker cancelled or error */ }
    }

    // ── 预览模式切换 ────────────────────────────────────────────────

    private void ToggleSingleView_Click(object sender, RoutedEventArgs e)
    {
        BtnSingleView.IsChecked = true;
        BtnGridView.IsChecked = false;
        ViewModel.IsGridView = false;
    }

    private void ToggleGridView_Click(object sender, RoutedEventArgs e)
    {
        BtnSingleView.IsChecked = false;
        BtnGridView.IsChecked = true;
        ViewModel.IsGridView = true;

        // 首次切换到网格视图时，加载当前可见范围的缩略图
        int visibleCount = GetGridVisibleItemCount();
        ViewModel.LoadThumbnailRange(0, visibleCount - 1);

        // 延迟挂载 ScrollViewer 事件（GridView 首次渲染后才有内部 ScrollViewer）
        HookGridScrollViewer();
    }

    private void ToggleFullWindow_Click(object sender, RoutedEventArgs e)
    {
        SetFullWindowMode(!ViewModel.IsFullscreenPreview);
    }

    private void EscKey_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.IsFullscreenPreview)
        {
            SetFullWindowMode(false);
            args.Handled = true;
        }
    }

    private void SetFullWindowMode(bool fullWindow)
    {
        ViewModel.IsFullscreenPreview = fullWindow;
        LeftColumnDef.Width = fullWindow ? new GridLength(0) : new GridLength(300);
        FullWindowIcon.Glyph = fullWindow ? "\uE73F" : "\uE740";
    }

    private void GridThumbnail_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FrameThumbnail thumb)
        {
            _selectedFrameIndex = thumb.FrameNumber - ViewModel.StartFrame;
            RedrawTimeline();
        }
    }

    private void GridView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // 双击网格缩略图切换到单帧预览
        if (PreviewGridView.SelectedItem is FrameThumbnail thumb
            && !string.IsNullOrEmpty(thumb.OutputPath))
        {
            _selectedFrameIndex = thumb.FrameNumber - ViewModel.StartFrame;
            ToggleSingleView_Click(sender, e);
            LoadPreviewForFrame(thumb.FrameNumber);
        }
    }

    private void GridView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateGridItemSize();
    }

    private void GridView_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateGridItemSize();
        HookGridScrollViewer();
    }

    private void GridView_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(PreviewGridView).Properties;
        if (!e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)) return;

        int delta = props.MouseWheelDelta;
        if (delta > 0 && _gridColumns > 2) _gridColumns--;
        else if (delta < 0 && _gridColumns < 12) _gridColumns++;
        else return;

        UpdateGridItemSize();
        e.Handled = true;
    }

    private void UpdateGridItemSize()
    {
        if (PreviewGridView.ActualWidth <= 0) return;
        double availableWidth = PreviewGridView.ActualWidth - 16; // GridView Padding="8" 左右共 16

        // 精确填满容器：ItemWidth = Floor(可用宽度 / 列数)
        // ItemsWrapGrid 以 ItemWidth 为槽宽，Border Margin="3" 在槽内部绘制，不额外占空间
        double itemWidth = Math.Max(80, Math.Floor(availableWidth / _gridColumns));
        double itemHeight = Math.Floor(itemWidth / ViewModel.RenderAspectRatio) + 24; // label height

        if (PreviewGridView.ItemsPanelRoot is ItemsWrapGrid wrapGrid)
        {
            wrapGrid.ItemWidth = itemWidth;
            wrapGrid.ItemHeight = itemHeight;
        }
    }

    // ── 分辨率选择 ──────────────────────────────────────────────────

    private void Resolution_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResolutionCombo.SelectedItem is ComboBoxItem item && item.Tag is string tagStr)
        {
            if (double.TryParse(tagStr, out double factor))
            {
                _previewScaleFactor = factor;

                // 重新加载当前预览
                if (_selectedFrameIndex >= 0 && _selectedFrameIndex < ViewModel.FrameResults.Count)
                {
                    var fr = ViewModel.FrameResults[_selectedFrameIndex];
                    if (!string.IsNullOrEmpty(fr.OutputPath))
                        _ = LoadPreviewWithScale(fr.OutputPath);
                }
            }
        }
    }

    private async Task LoadPreviewWithScale(string path)
    {
        ViewModel.IsLoadingPreview = true;
        try
        {
            int decodeWidth = _previewScaleFactor > 0
                ? (int)(ViewModel.RenderPixelWidth * _previewScaleFactor)
                : 0; // 原始分辨率
            decodeWidth = Math.Max(decodeWidth, 240);

            using var decoded = await ImageHelper.DecodeAsync(path, decodePixelWidth: decodeWidth);
            if (decoded != null)
            {
                ViewModel.SetRenderAspectRatio(decoded.PixelWidth, decoded.PixelHeight);
                var source = await ImageHelper.CreateSourceAsync(decoded);
                if (source != null)
                    ViewModel.PreviewImage = source;
            }
        }
        catch { /* ignore */ }
        finally
        {
            ViewModel.IsLoadingPreview = false;
        }
    }

    // ── 时间轴绘制 ──────────────────────────────────────────────────

    private void RedrawTimeline()
    {
        if (TimelineCanvas == null) return;

        TimelineCanvas.Children.Clear();
        int count = ViewModel.FrameResults.Count;
        if (count == 0) return;

        double totalWidth = count * _frameWidth;
        double minWidth = TimelineScrollViewer != null
            ? Math.Max(totalWidth, TimelineScrollViewer.ActualWidth - 12)
            : totalWidth;
        TimelineCanvas.Width = minWidth;

        int startFrame = ViewModel.StartFrame;

        for (int i = 0; i < count; i++)
        {
            var fr = ViewModel.FrameResults[i];
            double x = i * _frameWidth;

            // 帧条
            var bar = new Rectangle
            {
                Width = Math.Max(1, _frameWidth - 2),
                Height = 28,
                RadiusX = 2,
                RadiusY = 2,
                Fill = GetFrameBarBrush(fr.Status),
            };
            Canvas.SetLeft(bar, x + 1);
            Canvas.SetTop(bar, 18);
            TimelineCanvas.Children.Add(bar);

            // 刻度标签（每 N 帧显示一个）
            int step = _frameWidth >= 20 ? 5 : _frameWidth >= 12 ? 10 : 25;
            int frameNum = startFrame + i;
            if (i == 0 || frameNum % step == 0 || i == count - 1)
            {
                var label = new TextBlock
                {
                    Text = frameNum.ToString(),
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Colors.Gray),
                };
                Canvas.SetLeft(label, x);
                Canvas.SetTop(label, 4);
                TimelineCanvas.Children.Add(label);
            }
        }

        // 绘制播放头
        DrawPlayhead();
    }

    private void DrawPlayhead()
    {
        if (_selectedFrameIndex < 0 || _selectedFrameIndex >= ViewModel.FrameResults.Count) return;

        double x = _selectedFrameIndex * _frameWidth + _frameWidth / 2;

        // 播放头线
        var line = new Rectangle
        {
            Width = 2,
            Height = 48,
            Fill = new SolidColorBrush(Colors.DodgerBlue),
        };
        Canvas.SetLeft(line, x - 1);
        Canvas.SetTop(line, 2);
        TimelineCanvas.Children.Add(line);

        // 播放头三角
        var triangle = new Polygon
        {
            Points =
            {
                new Windows.Foundation.Point(x - 5, 0),
                new Windows.Foundation.Point(x + 5, 0),
                new Windows.Foundation.Point(x, 6),
            },
            Fill = new SolidColorBrush(Colors.DodgerBlue),
        };
        TimelineCanvas.Children.Add(triangle);
    }

    private static SolidColorBrush GetFrameBarBrush(FrameStatus status) => status switch
    {
        FrameStatus.Completed => new SolidColorBrush(Colors.LimeGreen),
        FrameStatus.Rendering => new SolidColorBrush(Colors.DodgerBlue),
        FrameStatus.BlackFrame => new SolidColorBrush(Colors.Orange),
        FrameStatus.Error => new SolidColorBrush(Colors.Red),
        _ => new SolidColorBrush(Colors.DimGray),
    };

    // ── 时间轴交互 ──────────────────────────────────────────────────

    private void TimelineCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(TimelineCanvas).Position;
        _isDraggingTimeline = true;
        TimelineCanvas.CapturePointer(e.Pointer);
        SelectFrameAtPosition(pos.X, immediate: true);
    }

    private void TimelineCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingTimeline) return;
        var pos = e.GetCurrentPoint(TimelineCanvas).Position;
        SelectFrameAtPosition(pos.X, immediate: false);
    }

    private void TimelineCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingTimeline)
        {
            _isDraggingTimeline = false;
            TimelineCanvas.ReleasePointerCapture(e.Pointer);

            // flush 防抖中的待加载帧
            if (_pendingDragFrame >= 0)
            {
                LoadPreviewForFrame(_pendingDragFrame);
                _pendingDragFrame = -1;
            }
            _dragDebounceTimer?.Stop();
        }
    }

    private void SelectFrameAtPosition(double x, bool immediate)
    {
        int count = ViewModel.FrameResults.Count;
        if (count == 0) return;

        int index = Math.Clamp((int)(x / _frameWidth), 0, count - 1);
        if (index == _selectedFrameIndex && !immediate) return;

        _selectedFrameIndex = index;
        RedrawTimeline();

        int frameNumber = ViewModel.StartFrame + index;

        if (immediate)
        {
            // 首次点击立即加载
            LoadPreviewForFrame(frameNumber);
        }
        else
        {
            // 拖拽中防抖 80ms
            _pendingDragFrame = frameNumber;
            _dragDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _dragDebounceTimer.Stop();
            _dragDebounceTimer.Tick -= DragDebounce_Tick;
            _dragDebounceTimer.Tick += DragDebounce_Tick;
            _dragDebounceTimer.Start();
        }
    }

    private void DragDebounce_Tick(object? sender, object e)
    {
        _dragDebounceTimer?.Stop();
        if (_pendingDragFrame >= 0)
        {
            LoadPreviewForFrame(_pendingDragFrame);
            _pendingDragFrame = -1;
        }
    }

    private async void LoadPreviewForFrame(int frameNumber)
    {
        int index = frameNumber - ViewModel.StartFrame;
        if (index < 0 || index >= ViewModel.FrameResults.Count) return;

        var fr = ViewModel.FrameResults[index];
        if (string.IsNullOrEmpty(fr.OutputPath) || !IO.File.Exists(fr.OutputPath)) return;

        System.Diagnostics.Trace.WriteLine($"[WORKSPACE] LoadPreviewForFrame: frame={frameNumber}, path={fr.OutputPath}, pageHash={GetHashCode()}, IsLoaded={IsLoaded}");
        ViewModel.IsLoadingPreview = true;
        try
        {
            int decodeWidth = _previewScaleFactor > 0
                ? Math.Max(240, (int)(ViewModel.RenderPixelWidth * _previewScaleFactor))
                : 0;

            using var decoded = await ImageHelper.DecodeAsync(fr.OutputPath, decodePixelWidth: decodeWidth == 0 ? 960 : decodeWidth);
            if (!IsLoaded) return; // 页面已卸载，放弃后续操作
            if (decoded != null)
            {
                ViewModel.SetRenderAspectRatio(decoded.PixelWidth, decoded.PixelHeight);
                System.Diagnostics.Trace.WriteLine($"[WORKSPACE] LoadPreviewForFrame: 解码完成, 准备 CreateSourceAsync, pageHash={GetHashCode()}, IsLoaded={IsLoaded}");
                var source = await ImageHelper.CreateSourceAsync(decoded);
                if (source != null && IsLoaded)
                {
                    System.Diagnostics.Trace.WriteLine($"[WORKSPACE] LoadPreviewForFrame: 设置 PreviewImage, pageHash={GetHashCode()}");
                    ViewModel.PreviewImage = source;
                }
                else if (source != null)
                {
                    // 页面已卸载，当前仍在 UI 线程，直接释放即可
                    ((IDisposable)source).Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[WORKSPACE] !! LoadPreviewForFrame 异常: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            ViewModel.IsLoadingPreview = false;
        }
    }

    // ── 时间轴缩放 ──────────────────────────────────────────────────

    private void TimelineZoom_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        _frameWidth = e.NewValue;
        RedrawTimeline();
    }

    // ── 播放控制 ────────────────────────────────────────────────────

    private void TogglePlayback_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsPlaying)
        {
            StopPlayback();
        }
        else
        {
            StartPlayback();
        }
    }

    private void StartPlayback()
    {
        if (ViewModel.FrameResults.Count == 0) return;

        ViewModel.IsPlaying = true;
        PlayPauseIcon.Glyph = "\uE769"; // pause icon

        _playbackTimer ??= new DispatcherTimer();
        _playbackTimer.Interval = TimeSpan.FromMilliseconds(42); // ~24fps
        _playbackTimer.Tick -= Playback_Tick;
        _playbackTimer.Tick += Playback_Tick;
        _playbackTimer.Start();
    }

    private void StopPlayback()
    {
        ViewModel.IsPlaying = false;
        PlayPauseIcon.Glyph = "\uE768"; // play icon
        _playbackTimer?.Stop();
    }

    private void Playback_Tick(object? sender, object e)
    {
        int count = ViewModel.FrameResults.Count;
        if (count == 0) { StopPlayback(); return; }

        // 仅在已完成的帧之间播放
        int next = _selectedFrameIndex + 1;
        if (next >= count) next = 0;

        var fr = ViewModel.FrameResults[next];
        if (fr.Status != FrameStatus.Completed)
        {
            // 循环回到第一个完成的帧
            next = 0;
            while (next < count && ViewModel.FrameResults[next].Status != FrameStatus.Completed)
                next++;
            if (next >= count) { StopPlayback(); return; }
        }

        _selectedFrameIndex = next;
        RedrawTimeline();
        LoadPreviewForFrame(ViewModel.StartFrame + next);
    }

    // ── ViewModel 属性变化 → 时间轴颜色同步 ────────────────────────

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CompletedFrames)
            or nameof(MainViewModel.CurrentFrame)
            or nameof(MainViewModel.IsRendering))
        {
            if (ViewModel.IsBulkInserting) return;
            ScheduleTimelineRedraw();
        }

        // 渲染开始/停止时同步项目状态 + 管理页面缓存
        if (e.PropertyName == nameof(MainViewModel.IsRendering))
        {
            SaveViewModelToProject();

            if (ViewModel.IsRendering)
            {
                // 渲染开始 → 同步项目状态到共享缓存（项目列表可见）
                _project.Status = ProjectStatus.Rendering;
                _project.LastRenderAt = DateTime.Now;
                ProjectService.Update(_project);

                // 通知 MainWindow 缓存此页面（导航离开时不销毁）
                _mainWindow?.SetActiveWorkspace(this, _project.Id);
            }
            else
            {
                // 渲染结束 → 更新项目状态
                _project.Status = ViewModel.CompletedFrames >= _project.TotalFrames
                    ? ProjectStatus.Completed
                    : ProjectStatus.Idle;
                ProjectService.Update(_project);

                // 清除 MainWindow 页面缓存
                _mainWindow?.SetActiveWorkspace(null, null);
            }
        }

        // 实时同步帧进度到共享项目对象（项目列表进度条可见）
        if (e.PropertyName == nameof(MainViewModel.CompletedFrames) && ViewModel.IsRendering)
        {
            _project.CompletedFrames = ViewModel.CompletedFrames;
        }
    }

    /// <summary>
    /// 监听项目模型变化（RenderRecovery 轮询更新 Status/CompletedFrames）。
    /// 当外部 Blender 进程结束后，解除 ViewModel 的渲染锁定状态。
    /// </summary>
    private void Project_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RenderProject.Status))
        {
            // 恢复模式下 Blender 退出 → project.Status 被 RenderRecovery 改为 Idle/Completed
            // 同步解除 ViewModel 的渲染状态，恢复 UI 可交互
            if (_project.Status != ProjectStatus.Rendering && ViewModel.IsRendering && !ViewModel.IsRenderingLocally)
            {
                ViewModel.IsRendering = false;
                ViewModel.StatusText = _project.Status == ProjectStatus.Completed
                    ? $"渲染完成 - {_project.CompletedFrames} 帧"
                    : $"渲染已停止 - 已完成 {_project.CompletedFrames} 帧";
            }
        }
        else if (e.PropertyName == nameof(RenderProject.CompletedFrames) && ViewModel.IsRendering && !ViewModel.IsRenderingLocally)
        {
            // 恢复模式下同步帧进度到 ViewModel
            ViewModel.CompletedFrames = _project.CompletedFrames;
            int total = _project.TotalFrames;
            if (total > 0)
            {
                ViewModel.OverallProgress = (double)_project.CompletedFrames / total * 100;
                ViewModel.StatusText = $"渲染恢复中 - {_project.CompletedFrames}/{total} 帧";
            }

            // 增量扫描：将磁盘上新出现的帧同步到 FrameResults + GridThumbnails + 时间轴
            _ = SyncNewFramesFromDiskAsync();
        }
    }

    /// <summary>
    /// 恢复模式下增量扫描磁盘，将新渲染完成的帧同步到 FrameResults/GridThumbnails。
    /// 仅更新已有条目中 Status=Pending 的帧，避免重复。
    /// </summary>
    private async Task SyncNewFramesFromDiskAsync()
    {
        var outputPattern = ViewModel.OutputPattern;
        if (string.IsNullOrEmpty(outputPattern)) return;

        int start = ViewModel.OutputType == RenderOutputType.SingleFrame
            ? ViewModel.SingleFrameNumber : ViewModel.StartFrame;
        int end = ViewModel.OutputType == RenderOutputType.SingleFrame
            ? ViewModel.SingleFrameNumber : ViewModel.EndFrame;

        // 后台扫描
        var newFrames = await Task.Run(() =>
        {
            var results = new List<(int frame, string path)>();
            string? dir = IO.Path.GetDirectoryName(outputPattern);
            if (string.IsNullOrEmpty(dir) || !IO.Directory.Exists(dir)) return results;

            for (int i = start; i <= end; i++)
            {
                var file = RenderConfig.FindFrameFile(outputPattern, i);
                if (file != null)
                    results.Add((i, file));
            }
            return results;
        });

        if (newFrames.Count == 0) return;

        // 如果 FrameResults 为空（首次进入恢复模式），初始化全部帧占位
        if (ViewModel.FrameResults.Count == 0)
        {
            ViewModel.IsBulkInserting = true;
            try
            {
                for (int i = start; i <= end; i++)
                {
                    var found = newFrames.FirstOrDefault(f => f.frame == i);
                    var fr = new FrameResult
                    {
                        FrameNumber = i,
                        Status = found.path != null ? FrameStatus.Completed : FrameStatus.Pending,
                        OutputPath = found.path ?? string.Empty,
                    };
                    ViewModel.FrameResults.Add(fr);
                    var thumbNew2 = new FrameThumbnail
                    {
                        FrameNumber = i,
                        OutputPath = fr.OutputPath,
                        Status = fr.Status,
                    };
                    if (!string.IsNullOrEmpty(fr.OutputPath))
                        thumbNew2.CacheKey = Helpers.ImageHelper.GetCacheKey(fr.OutputPath.Replace('/', '\\'));
                    ViewModel.GridThumbnails.Add(thumbNew2);
                }
            }
            finally { ViewModel.IsBulkInserting = false; }
            RedrawTimeline();
            return;
        }

        // 增量更新：仅更新 Pending → Completed 的帧
        bool changed = false;
        foreach (var (frame, path) in newFrames)
        {
            int idx = frame - start;
            if (idx < 0 || idx >= ViewModel.FrameResults.Count) continue;

            var fr = ViewModel.FrameResults[idx];
            if (fr.Status != FrameStatus.Pending) continue;

            fr.Status = FrameStatus.Completed;
            fr.OutputPath = path;

            if (idx < ViewModel.GridThumbnails.Count)
            {
                var thumb = ViewModel.GridThumbnails[idx];
                thumb.Status = FrameStatus.Completed;
                thumb.OutputPath = path;
            }
            changed = true;
        }

        if (changed)
        {
            // 加载最新完成帧的预览
            var lastCompleted = newFrames[^1];
            LoadPreviewForFrame(lastCompleted.frame);
            RedrawTimeline();
        }
    }

    private void FrameResults_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ViewModel.IsBulkInserting) return;
        ScheduleTimelineRedraw();
    }

    private void ScheduleTimelineRedraw()
    {
        _timelineDirty = true;
        if (_timelineThrottleTimer == null)
        {
            _timelineThrottleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _timelineThrottleTimer.Tick += (_, _) =>
            {
                if (_timelineDirty)
                {
                    _timelineDirty = false;
                    RedrawTimeline();
                }
            };
            _timelineThrottleTimer.Start();
        }
    }

    // ── 网格虚拟化滚动 ──────────────────────────────────────────────

    /// <summary>获取当前 GridView 可视区域能容纳的 item 数量</summary>
    private int GetGridVisibleItemCount()
    {
        if (PreviewGridView.ActualWidth <= 0 || PreviewGridView.ActualHeight <= 0)
            return 40; // 默认值

        double availableWidth = PreviewGridView.ActualWidth - 16;
        double itemWidth = Math.Max(80, Math.Floor(availableWidth / _gridColumns));
        double itemHeight = Math.Floor(itemWidth / ViewModel.RenderAspectRatio) + 24;

        int cols = _gridColumns;
        int visibleRows = (int)Math.Ceiling(PreviewGridView.ActualHeight / itemHeight) + 1;
        return cols * visibleRows;
    }

    /// <summary>挂载 GridView 内部 ScrollViewer 的 ViewChanged 事件</summary>
    private void HookGridScrollViewer()
    {
        if (_gridScrollViewer != null) return;

        _gridScrollViewer = FindChildOfType<ScrollViewer>(PreviewGridView);
        if (_gridScrollViewer != null)
        {
            _gridScrollViewer.ViewChanged += GridScrollViewer_ViewChanged;
            System.Diagnostics.Trace.WriteLine($"[WORKSPACE] GridView ScrollViewer 已挂载");
        }
    }

    private void GridScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // 每次滚动事件重置定时器，只在停止滚动 200ms 后才触发加载
        _scrollDirty = true;
        if (_scrollDebounceTimer == null)
        {
            _scrollDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _scrollDebounceTimer.Tick += ScrollDebounce_Tick;
        }
        _scrollDebounceTimer.Stop();
        _scrollDebounceTimer.Start();
    }

    private void ScrollDebounce_Tick(object? sender, object e)
    {
        _scrollDebounceTimer?.Stop();
        if (!_scrollDirty) return;
        _scrollDirty = false;

        if (_gridScrollViewer == null || !ViewModel.IsGridView) return;

        // 检测滚动方向
        double currentOffset = _gridScrollViewer.VerticalOffset;
        bool scrollingDown = currentOffset >= _lastScrollOffset;
        _lastScrollOffset = currentOffset;

        var (visibleStart, visibleEnd) = GetVisibleIndexRange();
        int bufferItems = _gridColumns * 3; // BUFFER_ROWS=3 行缓冲
        int visibleCount = visibleEnd - visibleStart + 1;

        // 预加载方向：沿滚动方向多加载 1 屏
        int prefetchItems = visibleCount;
        int loadStart, loadEnd;
        if (scrollingDown)
        {
            loadStart = Math.Max(0, visibleStart - bufferItems);
            loadEnd = Math.Min(ViewModel.GridThumbnails.Count - 1, visibleEnd + bufferItems + prefetchItems);
        }
        else
        {
            loadStart = Math.Max(0, visibleStart - bufferItems - prefetchItems);
            loadEnd = Math.Min(ViewModel.GridThumbnails.Count - 1, visibleEnd + bufferItems);
        }

        // 回收远离可视区域的缩略图（BitmapImage 由 GC 自动回收）
        int recycleStart = Math.Max(0, loadStart - visibleCount);
        int recycleEnd = Math.Min(ViewModel.GridThumbnails.Count - 1, loadEnd + visibleCount);
        ViewModel.RecycleThumbnailsOutsideRange(recycleStart, recycleEnd);

        // 加载新进入可视区域 + 预加载区域的缩略图
        ViewModel.LoadThumbnailRange(loadStart, loadEnd);
    }

    /// <summary>根据 ScrollViewer 偏移量计算当前可见的 GridView 项索引范围</summary>
    private (int start, int end) GetVisibleIndexRange()
    {
        if (_gridScrollViewer == null || PreviewGridView.ActualWidth <= 0)
            return (0, 39);

        double availableWidth = PreviewGridView.ActualWidth - 16;
        double itemWidth = Math.Max(80, Math.Floor(availableWidth / _gridColumns));
        double itemHeight = Math.Floor(itemWidth / ViewModel.RenderAspectRatio) + 24;
        if (itemHeight <= 0) itemHeight = 100;

        int cols = _gridColumns;
        double scrollOffset = _gridScrollViewer.VerticalOffset;
        double viewportHeight = _gridScrollViewer.ViewportHeight;

        int firstVisibleRow = (int)(scrollOffset / itemHeight);
        int lastVisibleRow = (int)((scrollOffset + viewportHeight) / itemHeight);

        int startIdx = firstVisibleRow * cols;
        int endIdx = Math.Min((lastVisibleRow + 1) * cols - 1, ViewModel.GridThumbnails.Count - 1);

        return (startIdx, endIdx);
    }

    /// <summary>在 VisualTree 中查找指定类型的子元素</summary>
    private static T? FindChildOfType<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            var result = FindChildOfType<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    // ── 清理 ────────────────────────────────────────────────────────

    /// <summary>
    /// 页面从视觉树移除时触发。
    /// 页面不再复用（每次导航创建新实例），因此做完整清理。
    /// 注意：不 Shutdown ViewModel — ViewModel 由 MainWindow 管理，可能正在渲染。
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Trace.WriteLine($"[WORKSPACE] OnUnloaded 开始: hash={GetHashCode()}, PreviewImage={ViewModel.PreviewImage != null}, GridThumbnails={ViewModel.GridThumbnails.Count}");

        SaveViewModelToProject();

        // 停止所有定时器
        _dragDebounceTimer?.Stop();
        _playbackTimer?.Stop();
        _timelineThrottleTimer?.Stop();
        _scrollDebounceTimer?.Stop();

        // 解除 ScrollViewer 事件
        if (_gridScrollViewer != null)
        {
            _gridScrollViewer.ViewChanged -= GridScrollViewer_ViewChanged;
            _gridScrollViewer = null;
        }

        // 取消正在进行的缩略图批量加载（避免后续 CreateSourceAsync 写入已卸载的 UI）
        ViewModel.CancelThumbnailLoading();

        // 取消事件订阅（防止已废弃的页面实例继续收到回调）
        ViewModel.FrameResults.CollectionChanged -= FrameResults_CollectionChanged;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _project.PropertyChanged -= Project_PropertyChanged;

        // 释放 SoftwareBitmapSource（D2D surface 绑定到当前 visual tree，
        // 下次新 Page 绑定时 surface 已失效 → FailFast 0xC000027B）
        // ViewModel 数据保留，PreviewImage/缩略图会在下次 OnLoaded 从磁盘重建
        ViewModel.PreviewImage = null;
        ViewModel.ClearGridThumbnails();

        System.Diagnostics.Trace.WriteLine($"[WORKSPACE] OnUnloaded 完成: hash={GetHashCode()}");
    }

    /// <summary>
    /// 页面真正被销毁时调用（MainWindow 替换工作区或关窗）。
    /// 执行完整清理：取消事件订阅 + Shutdown ViewModel。
    /// </summary>
    public void Cleanup()
    {
        SaveViewModelToProject();

        _dragDebounceTimer?.Stop();
        _playbackTimer?.Stop();
        _timelineThrottleTimer?.Stop();

        ViewModel.FrameResults.CollectionChanged -= FrameResults_CollectionChanged;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _project.PropertyChanged -= Project_PropertyChanged;

        ViewModel.Shutdown();
    }
}
