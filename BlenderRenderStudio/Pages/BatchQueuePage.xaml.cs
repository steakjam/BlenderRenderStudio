using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using BlenderRenderStudio.Helpers;
using BlenderRenderStudio.Models;
using BlenderRenderStudio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace BlenderRenderStudio.Pages;

public sealed partial class BatchQueuePage : Page
{
    public RenderQueueService QueueService { get; set; } = null!;
    public bool IsQueueEmpty => QueueService?.Jobs.Count == 0;

    /// <summary>双击队列项导航到工作区（由 MainWindow 注入）</summary>
    public Action<RenderProject>? NavigateToProject { get; set; }

    /// <summary>安全调度器（由 MainWindow 通过 SetSafeDispatcher 注入）</summary>
    private SafeDispatcher? _safeDispatcher;

    // 事件处理器引用（用于 Unloaded 时取消订阅，避免泄漏）
    private NotifyCollectionChangedEventHandler? _collectionChangedHandler;
    private PropertyChangedEventHandler? _propertyChangedHandler;

    public BatchQueuePage()
    {
        System.Diagnostics.Trace.WriteLine($"[QUEUE] 构造: hash={GetHashCode()}");
        InitializeComponent();
        this.Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Trace.WriteLine($"[QUEUE] OnUnloaded 开始: hash={GetHashCode()}");
        if (QueueService != null)
        {
            if (_collectionChangedHandler != null)
                QueueService.Jobs.CollectionChanged -= _collectionChangedHandler;
            if (_propertyChangedHandler != null)
                QueueService.PropertyChanged -= _propertyChangedHandler;
        }
        System.Diagnostics.Trace.WriteLine($"[QUEUE] OnUnloaded 完成: hash={GetHashCode()}");
    }

    public void SetSafeDispatcher(SafeDispatcher dispatcher) => _safeDispatcher = dispatcher;

    public void SetQueueService(RenderQueueService service)
    {
        QueueService = service;
        Bindings.Update();

        _collectionChangedHandler = (_, _) =>
        {
            Bindings.Update();
            UpdateButtonStates();
        };
        service.Jobs.CollectionChanged += _collectionChangedHandler;

        _propertyChangedHandler = (_, args) =>
        {
            if (args.PropertyName is "IsRunning" or "IsPaused")
            {
                if (_safeDispatcher != null)
                    _safeDispatcher.Run(UpdateButtonStates);
                else
                    DispatcherQueue.TryEnqueue(UpdateButtonStates);
            }
        };
        service.PropertyChanged += _propertyChangedHandler;
    }

    private void UpdateButtonStates()
    {
        bool running = QueueService.IsRunning;
        BtnStartQueue.IsEnabled = !running && QueueService.Jobs.Count > 0;
        BtnPauseQueue.IsEnabled = running;
        BtnStopQueue.IsEnabled = running;
        BtnSkip.IsEnabled = running;

        if (running)
        {
            BtnPauseQueue.Content = QueueService.IsPaused
                ? CreateButtonContent("\uE768", "恢复")
                : CreateButtonContent("\uE769", "暂停");
        }

        var currentJob = QueueService.Jobs.FirstOrDefault(j => j.Status == RenderJobStatus.Running);
        var currentName = currentJob?.ProjectName ?? QueueService.CurrentProjectId ?? "";
        QueueStatusText.Text = running
            ? QueueService.IsPaused ? "已暂停" : $"渲染中 — {currentName}"
            : "就绪";
    }

    private static StackPanel CreateButtonContent(string glyph, string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        sp.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        sp.Children.Add(new TextBlock { Text = text });
        return sp;
    }

    private async void StartQueue_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            UpdateButtonStates();
            await QueueService.StartAsync();
            UpdateButtonStates();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[BatchQueue] StartQueue 异常: {ex.Message}");
            UpdateButtonStates();
        }
    }

    private void PauseQueue_Click(object sender, RoutedEventArgs e)
    {
        if (QueueService.IsPaused)
            QueueService.Resume();
        else
            QueueService.Pause();
        UpdateButtonStates();
    }

    private void StopQueue_Click(object sender, RoutedEventArgs e)
    {
        QueueService.Stop();
        UpdateButtonStates();
    }

    private void SkipCurrent_Click(object sender, RoutedEventArgs e)
    {
        QueueService.SkipCurrent();
    }

    private void RemoveJob_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string projectId)
            QueueService.Remove(projectId);
    }

    private void QueueList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // 从 DataContext 查找双击的 RenderJob
        if (e.OriginalSource is FrameworkElement fe)
        {
            var job = FindParentDataContext<RenderJob>(fe);
            if (job != null)
            {
                var project = ProjectService.GetById(job.ProjectId);
                if (project != null)
                    NavigateToProject?.Invoke(project);
            }
        }
    }

    private static T? FindParentDataContext<T>(FrameworkElement? element) where T : class
    {
        while (element != null)
        {
            if (element.DataContext is T target) return target;
            element = element.Parent as FrameworkElement;
        }
        return null;
    }
}
