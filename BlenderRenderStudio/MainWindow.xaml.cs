using System;
using System.Diagnostics;
using BlenderRenderStudio.Helpers;
using BlenderRenderStudio.Models;
using BlenderRenderStudio.Pages;
using BlenderRenderStudio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace BlenderRenderStudio;

/// <summary>
/// 主窗口：NavigationView 导航外壳 + Frame 页面切换。
/// 项目列表、工作区、队列、设置为平级导航项。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly RenderQueueService _queueService = new();
    private readonly RenderRecovery _renderRecovery = new();
    private readonly NetworkDiscoveryService _networkService = new();

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

        Dispatcher = new SafeDispatcher(DispatcherQueue);

        var appWindow = this.AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));

        // 注入 DispatcherQueue + 导航回调
        _queueService.SetSafeDispatcher(Dispatcher);
        _queueService.NavigateToProject = project =>
        {
            Dispatcher.Run(() => OpenProject(project));
        };

        // 启动网络服务（根据设置决定 Master/Worker 模式）
        InitNetworkService();

        this.Closed += (_, _) =>
        {
            // 1. 先标记 SafeDispatcher 为已关闭，阻止后续所有 UI 调度
            Dispatcher.Shutdown();

            // 2. 停止后台服务
            _queueService.Stop();
            _networkService.Stop();
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
    }

    private void InitNetworkService()
    {
        var settings = SettingsService.Load();
        var port = settings.NetworkPort;

        // 总是以 Master 模式启动（监听设备注册 + UDP 发现）
        _networkService.StartMaster(port, settings.RemoteDevices);

        // 如果同时启用了 Worker 模式，也广播自身
        if (settings.EnableRemoteWorker)
            _networkService.StartWorker(settings.DeviceName, port);
    }

    // ── 导航逻辑 ────────────────────────────────────────────────

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
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
                case "devices":
                    NavigateToDevices();
                    break;
                case "settings":
                    NavigateToSettings();
                    break;
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
        var oldContent = ContentFrame.Content;
        Trace.WriteLine($"[NAV] SetContent: old={oldContent?.GetType().Name ?? "null"} → null → {content?.GetType().Name ?? "null"}");
        try
        {
            ContentFrame.Content = null;
            Trace.WriteLine($"[NAV] SetContent: Content=null 完成");
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

    private void NavigateToDevices()
    {
        Trace.WriteLine($"[NAV] NavigateToDevices 开始");
        var page = new DeviceManagementPage();
        page.SetNetworkService(_networkService);
        SetContent(page);
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
}
