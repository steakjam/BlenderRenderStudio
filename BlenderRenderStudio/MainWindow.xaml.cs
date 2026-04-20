using System;
<<<<<<< HEAD
using System.Diagnostics;
using BlenderRenderStudio.Helpers;
using BlenderRenderStudio.Models;
using BlenderRenderStudio.Pages;
using BlenderRenderStudio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
=======
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using Path = System.IO.Path;

>>>>>>> 24b10e2407b584065c0922a9cd8684aebb0d1adc
namespace BlenderRenderStudio;

/// <summary>
/// 主窗口：NavigationView 导航外壳 + Frame 页面切换。
/// 项目列表、工作区、队列、设置为平级导航项。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly RenderQueueService _queueService = new();
    private readonly RenderRecovery _renderRecovery = new();

    /// <summary>全局安全调度器，窗口关闭后所有 UI 调度静默忽略</summary>
    public SafeDispatcher Dispatcher { get; private set; } = null!;

    // WinUI 3 的 Page 不支持 detach/reattach 到 visual tree，重复 Content= 同一实例会 FailFast
    // 因此所有页面均不缓存实例，每次导航创建新 Page

    // 工作区：缓存 ViewModel（保留渲染状态），Page 每次导航创建新实例
    private ViewModels.MainViewModel? _workspaceViewModel;
    private RenderProject? _workspaceProject;
    private string? _workspaceProjectId;

    public MainWindow()
    {
        InitializeComponent();

<<<<<<< HEAD
        Dispatcher = new SafeDispatcher(DispatcherQueue);

        var appWindow = this.AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));

        // 注入 DispatcherQueue + 导航回调
        _queueService.SetSafeDispatcher(Dispatcher);
        _queueService.NavigateToProject = project =>
=======
        var appWindow = this.AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));

        ViewModel = new MainViewModel(DispatcherQueue);

        this.Closed += (_, _) => ViewModel.Shutdown();

        // 注入续渲询问对话框
        ViewModel.AskResumeAsync = async (resumeFrame) =>
>>>>>>> 24b10e2407b584065c0922a9cd8684aebb0d1adc
        {
            Dispatcher.Run(() => OpenProject(project));
        };

<<<<<<< HEAD
        this.Closed += (_, _) =>
        {
            // 1. 先标记 SafeDispatcher 为已关闭，阻止后续所有 UI 调度
            Dispatcher.Shutdown();

            // 2. 停止后台服务
            _queueService.Stop();
            _renderRecovery.StopMonitoring();

            // 3. 关闭工作区 ViewModel（停止渲染引擎 + 保存设置）
            _workspaceViewModel?.Shutdown();
            _workspaceViewModel = null;
        };

        // 启动时尝试恢复残留的 Blender 渲染进程
        bool recovered = _renderRecovery.TryRecover(Dispatcher);
        if (!recovered)
        {
            ProjectService.ResetStaleRenderingStatus();
        }

        // 初始导航到项目列表
        NavigateToProjects();
=======
        // 空格键播放/停止
        this.Content.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Space && !IsTextInputFocused())
            {
                args.Handled = true;
                TogglePlayback();
            }
        };

        // 日志自动滚动到底部（节流：最多 5次/秒）
        double _lastLogScrollTime = 0;
        ViewModel.LogEntries.CollectionChanged += (_, _) =>
        {
            double now = Environment.TickCount64 / 1000.0;
            if (now - _lastLogScrollTime < 0.2) return;
            _lastLogScrollTime = now;
            if (LogListView.Items.Count > 0)
            {
                LogListView.ScrollIntoView(LogListView.Items[^1]);
            }
        };

        // FrameResults 变化时重绘时间轴（批量插入期间跳过）
        ViewModel.FrameResults.CollectionChanged += (_, _) =>
        {
            if (ViewModel.IsBulkInserting) return;
            DispatcherQueue.TryEnqueue(RedrawTimeline);
        };

        // 帧状态变化时节流重绘时间轴（CompletedFrames/CurrentFrame 更新 = 帧颜色需刷新）
        double _lastTimelineRedrawTime = 0;
        ViewModel.PropertyChanged += (_, args) =>
        {
            // 批量插入结束后一次性重绘
            if (args.PropertyName == "IsBulkInserting" && !ViewModel.IsBulkInserting)
            {
                DispatcherQueue.TryEnqueue(RedrawTimeline);
                return;
            }
            if (args.PropertyName is "CompletedFrames" or "CurrentFrame")
            {
                double now = Environment.TickCount64 / 1000.0;
                if (now - _lastTimelineRedrawTime < 0.3) return;
                _lastTimelineRedrawTime = now;
                DispatcherQueue.TryEnqueue(RedrawTimeline);
            }
        };

>>>>>>> 24b10e2407b584065c0922a9cd8684aebb0d1adc
    }

    // ── 导航逻辑 ────────────────────────────────────────────────

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
<<<<<<< HEAD
            Trace.WriteLine($"[NAV] SelectionChanged → tag='{tag}', currentContent={ContentFrame.Content?.GetType().Name ?? "null"}");
            switch (tag)
            {
                case "projects":
                    NavigateToProjects();
                    break;
                case "workspace":
                    NavigateToWorkspace();
                    break;
                case "queue":
                    NavigateToQueue();
                    break;
                case "settings":
                    NavigateToSettings();
                    break;
=======
            ViewModel.BlendFilePath = file.Path;
            if (string.IsNullOrWhiteSpace(ViewModel.OutputPath))
            {
                var blendDir = Path.GetDirectoryName(file.Path);
                if (!string.IsNullOrEmpty(blendDir))
                    ViewModel.OutputPath = Path.Combine(blendDir, "render", "frame_#####");
>>>>>>> 24b10e2407b584065c0922a9cd8684aebb0d1adc
            }
            Trace.WriteLine($"[NAV] SelectionChanged 完成 → newContent={ContentFrame.Content?.GetType().Name ?? "null"}");
        }
    }

    /// <summary>
    /// 先清空旧内容（触发旧页面 Unloaded → 释放 D2D 资源），再设置新内容。
    /// 避免 WinUI 3 内部在新旧 Page 同时存在时出现 visual tree 状态不一致。
    /// </summary>
    private void SetContent(object content)
    {
<<<<<<< HEAD
        var oldContent = ContentFrame.Content;
        Trace.WriteLine($"[NAV] SetContent: old={oldContent?.GetType().Name ?? "null"} → null → {content?.GetType().Name ?? "null"}");
        try
        {
            ContentFrame.Content = null;
            Trace.WriteLine($"[NAV] SetContent: Content=null 完成");
=======
        var picker = new FolderPicker();
        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
            ViewModel.OutputPath = Path.Combine(folder.Path, "frame_#####");
    }

    private async void BrowseBlenderPath_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add(".exe");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file != null)
            ViewModel.BlenderPath = file.Path;
    }

    // ── ToggleButton 视图切换 ────────────────────────────────────

    private void ToggleSingleView_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsGridView = false;
        SyncViewToggleButtons();
    }

    private async void ToggleGridView_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.IsGridView = true;
            SyncViewToggleButtons();
            if (ViewModel.GridThumbnails.Count == 0)
                await ViewModel.RefreshGridThumbnailsAsync();
            else if (ViewModel.GridThumbnails.Any(t => t.Image == null && !string.IsNullOrEmpty(t.OutputPath)))
                await ViewModel.LoadMissingThumbnailsAsync();
            RequestGridLayout();
        }
        catch { }
    }

    /// <summary>同步所有 ToggleButton 的 IsChecked 状态（互斥）</summary>
    private void SyncViewToggleButtons()
    {
        bool isGrid = ViewModel.IsGridView;
        BtnSingleView.IsChecked = !isGrid;
        BtnGridView.IsChecked = isGrid;
        BtnFullSingleView.IsChecked = !isGrid;
        BtnFullGridView.IsChecked = isGrid;
    }

    private void ToggleFullscreen_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsFullscreenPreview = !ViewModel.IsFullscreenPreview;
        if (ViewModel.IsGridView) RequestGridLayout();
    }

    private void GridThumbnail_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FrameThumbnail thumb) return;
        // 单击仅选中（同步时间轴播放头位置）
        int baseFrame = ViewModel.FrameResults.FirstOrDefault()?.FrameNumber ?? 0;
        int idx = thumb.FrameNumber - baseFrame;
        if (idx >= 0 && idx < ViewModel.FrameResults.Count)
        {
            _timelineSelectedIndex = idx;
            DrawPlayhead();
>>>>>>> 24b10e2407b584065c0922a9cd8684aebb0d1adc
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[NAV] !! SetContent Content=null 异常: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        try
        {
            ContentFrame.Content = content;
            Trace.WriteLine($"[NAV] SetContent: Content={content?.GetType().Name} 完成");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[NAV] !! SetContent Content={content?.GetType().Name} 异常: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private void NavigateToProjects()
    {
        Trace.WriteLine($"[NAV] NavigateToProjects 开始");
        var page = new ProjectListPage
        {
            NavigateToProject = OpenProject,
            AddToQueueAction = projectId =>
            {
                _queueService.Enqueue(projectId);
                NavView.SelectedItem = NavQueue;
            },
        };
        SetContent(page);
    }

    private void NavigateToWorkspace()
    {
        Trace.WriteLine($"[NAV] NavigateToWorkspace 开始, project={_workspaceProject?.Name ?? "null"}, hasVM={_workspaceViewModel != null}");
        if (_workspaceProject == null)
        {
            Trace.WriteLine($"[NAV] NavigateToWorkspace: 无项目，显示占位符");
            SetContent(CreateEmptyWorkspacePlaceholder());
            return;
        }

        Trace.WriteLine($"[NAV] NavigateToWorkspace: 创建新 ProjectWorkspacePage, existingVM={_workspaceViewModel != null}");
        var page = new ProjectWorkspacePage(_workspaceProject, Dispatcher, _workspaceViewModel);
        page.SetQueueService(_queueService);
        page.SetMainWindow(this);

        if (_workspaceViewModel == null)
        {
            _workspaceViewModel = page.ViewModel;
            Trace.WriteLine($"[NAV] NavigateToWorkspace: 缓存新 ViewModel");
        }

        Trace.WriteLine($"[NAV] NavigateToWorkspace: 调用 SetContent");
        SetContent(page);
        Trace.WriteLine($"[NAV] NavigateToWorkspace 完成");
    }

    private void NavigateToQueue()
    {
        Trace.WriteLine($"[NAV] NavigateToQueue 开始");
        var page = new BatchQueuePage();
        page.SetSafeDispatcher(Dispatcher);
        page.SetQueueService(_queueService);
        page.NavigateToProject = project => OpenProject(project);
        SetContent(page);
        Trace.WriteLine($"[NAV] NavigateToQueue 完成");
    }

    private void NavigateToSettings()
    {
        Trace.WriteLine($"[NAV] NavigateToSettings 开始");
        SetContent(new SettingsPage());
    }

    /// <summary>
    /// 打开指定项目到工作区并切换到工作区导航项。
    /// 由项目列表卡片点击、队列页双击等入口调用。
    /// </summary>
    public void OpenProject(RenderProject project)
    {
        Trace.WriteLine($"[NAV] OpenProject: name='{project.Name}', id='{project.Id}', sameProject={_workspaceProjectId == project.Id}, hasVM={_workspaceViewModel != null}");

        // 同一项目 → 直接切换到工作区（NavigateToWorkspace 会创建新 Page）
        if (_workspaceProjectId == project.Id && _workspaceViewModel != null)
        {
            Trace.WriteLine($"[NAV] OpenProject: 同一项目，直接切换到工作区");
            NavView.SelectedItem = NavWorkspace;
            return;
        }

        // 不同项目 → 关闭旧 ViewModel（非渲染中才 Shutdown）
        if (_workspaceViewModel != null && !_workspaceViewModel.IsRenderingLocally)
        {
            Trace.WriteLine($"[NAV] OpenProject: Shutdown 旧 ViewModel");
            _workspaceViewModel.Shutdown();
        }

        // 清除旧缓存（新 ViewModel 将在 NavigateToWorkspace → ProjectWorkspacePage 构造中创建）
        _workspaceViewModel = null;
        _workspaceProject = project;
        _workspaceProjectId = project.Id;

        NavWorkspace.Content = project.Name;

        Trace.WriteLine($"[NAV] OpenProject: 设置 NavWorkspace 为选中项");
        NavView.SelectedItem = NavWorkspace;
        Trace.WriteLine($"[NAV] OpenProject 完成");
    }

    /// <summary>由工作区页面调用：保持兼容（现在 ViewModel 由 MainWindow 管理）</summary>
    public void SetActiveWorkspace(ProjectWorkspacePage? page, string? projectId)
    {
        // ViewModel 已由 MainWindow 直接管理，此方法仅保持 API 兼容
    }

    private static Grid CreateEmptyWorkspacePlaceholder()
    {
        var grid = new Grid();
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8,
        };
        stack.Children.Add(new FontIcon
        {
            Glyph = "\uE7C4",
            FontSize = 48,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorTertiaryBrush"],
        });
        stack.Children.Add(new TextBlock
        {
            Text = "未打开项目",
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        stack.Children.Add(new TextBlock
        {
            Text = "在「项目」页选择一个项目开始",
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorTertiaryBrush"],
        });
        grid.Children.Add(stack);
        return grid;
    }

    private async void GridView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not GridView gv || gv.SelectedItem is not FrameThumbnail thumb) return;
        try
        {
            ViewModel.IsGridView = false;
            SyncViewToggleButtons();
            await LoadPreviewFromPath(thumb.OutputPath);
        }
        catch { }
    }

    // ── 帧选择 → 加载预览 ──────────────────────────────────────

    private int _selectionVersion;

    private async void FrameList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FrameResult frame) return;
        try
        {
            SyncGridSelection(frame.FrameNumber);
            await LoadPreviewFromPath(frame.OutputPath);
        }
        catch { }
    }

    private async Task LoadPreviewFromPath(string? outputPath)
    {
        int version = ++_selectionVersion;

        if (string.IsNullOrEmpty(outputPath))
        {
            ViewModel.PreviewImage = null;
            ViewModel.IsLoadingPreview = false;
            return;
        }

        var path = outputPath.Replace('/', '\\');
        if (!File.Exists(path))
        {
            ViewModel.PreviewImage = null;
            ViewModel.IsLoadingPreview = false;
            return;
        }

        ViewModel.PreviewImage = null;
        ViewModel.IsLoadingPreview = true;

        try
        {
            using var decoded = await Helpers.ImageHelper.DecodeAsync(path, decodePixelWidth: GetPreviewDecodeWidth());
            if (version != _selectionVersion || decoded == null) return;

            var source = await Helpers.ImageHelper.CreateSourceAsync(decoded);
            if (version != _selectionVersion || source == null) return;

            ViewModel.PreviewImage = source;
            ViewModel.SetRenderAspectRatio(decoded.PixelWidth, decoded.PixelHeight);
        }
        finally
        {
            if (version == _selectionVersion)
                ViewModel.IsLoadingPreview = false;
        }
    }

    // ── 网格缩放（Ctrl+滚轮）─────────────────────────────────────

    private double _gridZoomFactor = 1.0;

    private void GridView_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var kbMods = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        if ((kbMods & Windows.UI.Core.CoreVirtualKeyStates.Down) == 0) return;

        e.Handled = true;
        int delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
        double step = delta > 0 ? 0.15 : -0.15;
        _gridZoomFactor = Math.Clamp(_gridZoomFactor + step, 0.5, 3.0);

        if (sender is GridView gv && gv.ActualWidth > 0)
            UpdateGridItemSize(gv, gv.ActualWidth);
    }

    private bool IsTextInputFocused()
    {
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(this.Content.XamlRoot);
        return focused is TextBox or NumberBox or PasswordBox or ComboBox;
    }

    private void GridView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is GridView gv && e.NewSize.Width > 0)
            UpdateGridItemSize(gv, e.NewSize.Width);
    }

    private void GridView_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is GridView gv && gv.ActualWidth > 0)
            UpdateGridItemSize(gv, gv.ActualWidth);
    }

    private void RequestGridLayout()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (PreviewGridView is { ActualWidth: > 0 } pv) UpdateGridItemSize(pv, pv.ActualWidth);
            if (FullscreenGridView is { ActualWidth: > 0 } fv) UpdateGridItemSize(fv, fv.ActualWidth);
        });
    }

    private void UpdateGridItemSize(GridView gv, double totalWidth, int retry = 0)
    {
        if (gv.ItemsPanelRoot is not Microsoft.UI.Xaml.Controls.ItemsWrapGrid panel)
        {
            if (retry < 8) DispatcherQueue.TryEnqueue(() => UpdateGridItemSize(gv, gv.ActualWidth, retry + 1));
            return;
        }

        const double baseMinWidth = 130;
        const double itemPadding = 6;
        const double labelHeight = 20;
        const double gridPadding = 16;
        const double scrollBarReserve = 2;

        double minItemWidth = baseMinWidth * _gridZoomFactor;
        double usable = totalWidth - gridPadding - scrollBarReserve;
        if (usable < minItemWidth) minItemWidth = usable;

        int columns = Math.Max(1, (int)(usable / minItemWidth));
        double itemWidth = usable / columns;

        double ratio = ViewModel.RenderAspectRatio;
        double imageWidth = itemWidth - itemPadding;
        double imageHeight = imageWidth / ratio;

        panel.ItemWidth = itemWidth;
        panel.ItemHeight = imageHeight + labelHeight + itemPadding;
    }

    private void SyncGridSelection(int frameNumber)
    {
        var thumb = ViewModel.GridThumbnails.FirstOrDefault(t => t.FrameNumber == frameNumber);
        if (thumb == null) return;

        if (PreviewGridView != null)
        {
            PreviewGridView.SelectedItem = thumb;
            PreviewGridView.ScrollIntoView(thumb, ScrollIntoViewAlignment.Default);
        }
        if (FullscreenGridView != null)
        {
            FullscreenGridView.SelectedItem = thumb;
            FullscreenGridView.ScrollIntoView(thumb, ScrollIntoViewAlignment.Default);
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  帧时间轴（Canvas 绘制：刻度尺 + 帧条带 + 蓝色播放头）
    // ══════════════════════════════════════════════════════════════

    private int _timelineSelectedIndex = -1;
    private bool _timelineDragging;
    private double _frameWidth = 24; // 每帧宽度，由 Slider 控制

    private void TimelineZoom_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        _frameWidth = e.NewValue;
        RedrawTimeline();
    }

    private void RedrawTimeline()
    {
        if (TimelineCanvas == null) return;
        TimelineCanvas.Children.Clear();

        int count = ViewModel.FrameResults.Count;
        if (count == 0) return;

        double totalWidth = count * _frameWidth;
        double minWidth = TimelineScrollViewer?.ActualWidth > 0 ? TimelineScrollViewer.ActualWidth - 12 : 0;
        TimelineCanvas.Width = Math.Max(totalWidth, minWidth);

        // 刻度尺行高（24px 预留，防止缩小时标签裁切）
        const double rulerHeight = 24;
        const double barTop = rulerHeight + 2;
        const double barHeight = 24;

        // 计算刻度间隔：至少 40px 一个刻度
        int tickInterval = Math.Max(1, (int)Math.Ceiling(40.0 / _frameWidth));

        // 绘制帧条带 + 刻度
        for (int i = 0; i < count; i++)
        {
            var fr = ViewModel.FrameResults[i];
            double x = i * _frameWidth;

            // 帧条带
            var bar = new Rectangle
            {
                Width = Math.Max(1, _frameWidth - 1),
                Height = barHeight,
                RadiusX = 2,
                RadiusY = 2,
                Fill = GetFrameBarBrush(fr.Status),
                Opacity = 0.7,
            };
            Canvas.SetLeft(bar, x + 0.5);
            Canvas.SetTop(bar, barTop);
            TimelineCanvas.Children.Add(bar);

            // 刻度线 + 帧号
            if (i % tickInterval == 0)
            {
                var tick = new Rectangle
                {
                    Width = 1,
                    Height = 6,
                    Fill = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
                };
                Canvas.SetLeft(tick, x);
                Canvas.SetTop(tick, rulerHeight - 6);
                TimelineCanvas.Children.Add(tick);

                var label = new TextBlock
                {
                    Text = fr.FrameNumber.ToString(),
                    FontSize = 9,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
                };
                Canvas.SetLeft(label, x + 2);
                Canvas.SetTop(label, 0);
                TimelineCanvas.Children.Add(label);
            }
        }

        // 绘制播放头
        DrawPlayhead();
    }

    private void DrawPlayhead()
    {
        // 移除旧播放头
        for (int i = TimelineCanvas.Children.Count - 1; i >= 0; i--)
        {
            if (TimelineCanvas.Children[i] is FrameworkElement fe && fe.Tag is string s && s == "playhead")
                TimelineCanvas.Children.RemoveAt(i);
        }

        if (_timelineSelectedIndex < 0 || _timelineSelectedIndex >= ViewModel.FrameResults.Count) return;

        double x = _timelineSelectedIndex * _frameWidth + _frameWidth / 2;

        // 播放头竖线
        var line = new Rectangle
        {
            Width = 2,
            Height = 48,
            Fill = new SolidColorBrush(Colors.DodgerBlue),
            Tag = "playhead",
        };
        Canvas.SetLeft(line, x - 1);
        Canvas.SetTop(line, 2);
        TimelineCanvas.Children.Add(line);

        // 播放头顶部三角（用小矩形近似）
        var head = new Rectangle
        {
            Width = 10,
            Height = 6,
            RadiusX = 2,
            RadiusY = 2,
            Fill = new SolidColorBrush(Colors.DodgerBlue),
            Tag = "playhead",
        };
        Canvas.SetLeft(head, x - 5);
        Canvas.SetTop(head, 0);
        TimelineCanvas.Children.Add(head);
    }

    private static SolidColorBrush GetFrameBarBrush(FrameStatus status) => status switch
    {
        FrameStatus.Completed => new SolidColorBrush(Color.FromArgb(255, 76, 175, 80)),   // 绿色
        FrameStatus.Rendering => new SolidColorBrush(Color.FromArgb(255, 33, 150, 243)),   // 蓝色
        FrameStatus.BlackFrame => new SolidColorBrush(Color.FromArgb(255, 255, 165, 0)),   // 橙色
        FrameStatus.Error => new SolidColorBrush(Color.FromArgb(255, 244, 67, 54)),        // 红色
        _ => new SolidColorBrush(Color.FromArgb(80, 128, 128, 128)),                       // 灰色半透明
    };

    // ── 时间轴指针交互 ─────────────────────────────────────────

    private void TimelineCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.FrameResults.Count == 0) return;
        _timelineDragging = true;
        TimelineCanvas.CapturePointer(e.Pointer);
        SelectFrameAtPosition(e.GetCurrentPoint(TimelineCanvas).Position.X);
    }

    private void TimelineCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_timelineDragging) return;
        SelectFrameAtPosition(e.GetCurrentPoint(TimelineCanvas).Position.X);
    }

    private void TimelineCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _timelineDragging = false;
        TimelineCanvas.ReleasePointerCapture(e.Pointer);

        // 释放时立即加载最终选中帧的预览
        _dragDebounceTimer?.Stop();
        if (_pendingDragIndex >= 0 && _pendingDragIndex < ViewModel.FrameResults.Count)
        {
            _ = LoadPreviewFromPath(ViewModel.FrameResults[_pendingDragIndex].OutputPath);
            _pendingDragIndex = -1;
        }
    }

    private DispatcherTimer? _dragDebounceTimer;
    private int _pendingDragIndex = -1;

    private void SelectFrameAtPosition(double x)
    {
        int count = ViewModel.FrameResults.Count;
        if (count == 0) return;

        int idx = (int)(x / _frameWidth);
        idx = Math.Clamp(idx, 0, count - 1);

        if (idx == _timelineSelectedIndex) return;
        _timelineSelectedIndex = idx;
        DrawPlayhead();

        var frame = ViewModel.FrameResults[idx];
        SyncGridSelection(frame.FrameNumber);

        // 拖动中防抖加载预览（80ms 延迟，避免快速拖动时大量解码请求）
        if (_timelineDragging)
        {
            _pendingDragIndex = idx;
            if (_dragDebounceTimer == null)
            {
                _dragDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
                _dragDebounceTimer.Tick += DragDebounce_Tick;
            }
            _dragDebounceTimer.Stop();
            _dragDebounceTimer.Start();
        }
        else
        {
            // 非拖动（首次点击）立即加载
            _ = LoadPreviewFromPath(frame.OutputPath);
        }
    }

    private async void DragDebounce_Tick(object? sender, object e)
    {
        _dragDebounceTimer?.Stop();
        if (_pendingDragIndex >= 0 && _pendingDragIndex < ViewModel.FrameResults.Count)
        {
            try { await LoadPreviewFromPath(ViewModel.FrameResults[_pendingDragIndex].OutputPath); }
            catch { }
        }
    }

    // ── 播放/停止 ────────────────────────────────────────────────

    private DispatcherTimer? _playbackTimer;

    private void TogglePlayback_Click(object sender, RoutedEventArgs e) => TogglePlayback();

    private void TogglePlayback()
    {
        if (ViewModel.IsPlaying) StopPlayback();
        else StartPlayback();
    }

    private void StartPlayback()
    {
        if (ViewModel.FrameResults.Count == 0) return;

        ViewModel.IsPlaying = true;
        ViewModel.IsGridView = false;
        SyncViewToggleButtons();
        PlayPauseIcon.Glyph = "\uE769";

        if (_timelineSelectedIndex < 0) _timelineSelectedIndex = 0;

        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();
    }

    private void StopPlayback()
    {
        ViewModel.IsPlaying = false;
        PlayPauseIcon.Glyph = "\uE768";
        _playbackTimer?.Stop();
        _playbackTimer = null;
    }

    private async void PlaybackTimer_Tick(object? sender, object e)
    {
        try
        {
            int next = _timelineSelectedIndex + 1;
            if (next >= ViewModel.FrameResults.Count)
            {
                StopPlayback();
                return;
            }
            _timelineSelectedIndex = next;
            DrawPlayhead();

            // 确保播放头可见
            double x = next * _frameWidth;
            TimelineScrollViewer?.ChangeView(Math.Max(0, x - 100), null, null);

            var frame = ViewModel.FrameResults[next];
            await LoadPreviewFromPath(frame.OutputPath);
        }
        catch { StopPlayback(); }
    }

    // ── 日志复制 ────────────────────────────────────────────────

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var entry in ViewModel.LogEntries)
                sb.AppendLine($"[{entry.TimeText}] {entry.LevelIcon} {entry.Message}");

            var dp = new DataPackage();
            dp.SetText(sb.ToString());
            Clipboard.SetContent(dp);
        }
        catch { }
    }

    // ══════════════════════════════════════════════════════════════
    //  NavigationView 分页切换
    // ══════════════════════════════════════════════════════════════

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            if (tag == "settings")
            {
                HomePage.Visibility = Visibility.Collapsed;
                SettingsPage.Visibility = Visibility.Visible;
                AutoStartToggle.IsOn = StartupService.IsEnabled();
                UpdateCacheSize();
            }
            else
            {
                HomePage.Visibility = Visibility.Visible;
                SettingsPage.Visibility = Visibility.Collapsed;
            }
        }
    }

    // ── 预览分辨率切换 ────────────────────────────────────────────

    private double _previewScaleFactor = 1.0;

    private int GetPreviewDecodeWidth()
    {
        if (_previewScaleFactor <= 0) return 0; // 原始分辨率
        int renderW = ViewModel.RenderPixelWidth;
        if (renderW <= 0) renderW = 1920; // 未知时按 1920 估算
        return Math.Max(240, (int)(renderW * _previewScaleFactor));
    }

    private void Resolution_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResolutionCombo?.SelectedItem is ComboBoxItem item && item.Tag is string tagStr
            && double.TryParse(tagStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double scale))
        {
            _previewScaleFactor = scale;
            // 重新加载当前预览（如果有）
            if (_timelineSelectedIndex >= 0 && _timelineSelectedIndex < ViewModel.FrameResults.Count)
            {
                var frame = ViewModel.FrameResults[_timelineSelectedIndex];
                _ = LoadPreviewFromPath(frame.OutputPath);
            }
        }
    }

    private async void SettingsBrowseBlender_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add(".exe");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                ViewModel.BlenderPath = file.Path;
                DetectStatusText.Text = "";
            }
        }
        catch { }
    }

    private void SettingsAutoDetect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DetectStatusText.Text = "正在检测…";
            var path = BlenderDetector.Detect();
            if (path != null)
            {
                ViewModel.BlenderPath = path;
                DetectStatusText.Text = $"已找到：{path}";
            }
            else
            {
                DetectStatusText.Text = "未找到 Blender 安装路径，请手动选择";
            }
        }
        catch (Exception ex) { DetectStatusText.Text = $"检测失败：{ex.Message}"; }
    }

    private void AutoStart_Toggled(object sender, RoutedEventArgs e)
    {
        try
        {
            if (AutoStartToggle.IsOn) StartupService.Enable();
            else StartupService.Disable();
        }
        catch { }
    }

    private void UpdateCacheSize()
    {
        try
        {
            var dir = SettingsService.ThumbnailCacheDir;
            if (Directory.Exists(dir))
            {
                long totalBytes = 0;
                foreach (var f in Directory.EnumerateFiles(dir))
                    totalBytes += new FileInfo(f).Length;
                CacheSizeText.Text = totalBytes < 1024 * 1024
                    ? $"{totalBytes / 1024.0:F1} KB"
                    : $"{totalBytes / (1024.0 * 1024):F1} MB";
            }
            else CacheSizeText.Text = "0 KB";
        }
        catch { CacheSizeText.Text = "无法读取"; }
    }

    private async void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "清除缓存",
                Content = "确定要清除所有缩略图缓存吗？下次查看网格视图时需要重新生成。",
                PrimaryButtonText = "清除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.Content.XamlRoot,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var dir = SettingsService.ThumbnailCacheDir;
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                UpdateCacheSize();
            }
        }
        catch { }
    }
}
