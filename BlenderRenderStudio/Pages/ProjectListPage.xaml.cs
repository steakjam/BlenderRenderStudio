using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using BlenderRenderStudio.Models;
using BlenderRenderStudio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace BlenderRenderStudio.Pages;

public sealed partial class ProjectListPage : Page
{
    public ObservableCollection<RenderProject> Projects { get; } = [];
    public bool IsEmpty => Projects.Count == 0;

    /// <summary>导航到项目工作区的回调（由 MainWindow 注入）</summary>
    public Action<RenderProject>? NavigateToProject { get; set; }

    /// <summary>添加到渲染队列的回调</summary>
    public Action<string>? AddToQueueAction { get; set; }

    public ProjectListPage()
    {
        System.Diagnostics.Trace.WriteLine($"[PROJECTS] 构造: hash={GetHashCode()}");
        InitializeComponent();
        RefreshProjects();
        this.Loaded += (_, _) => System.Diagnostics.Trace.WriteLine($"[PROJECTS] OnLoaded: hash={GetHashCode()}");
        this.Unloaded += (_, _) => System.Diagnostics.Trace.WriteLine($"[PROJECTS] OnUnloaded: hash={GetHashCode()}");
    }

    public void RefreshProjects()
    {
        Projects.Clear();
        foreach (var p in ProjectService.LoadAll())
            Projects.Add(p);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void OnPropertyChanged(string name)
    {
        // Manual notification since Page doesn't implement INotifyPropertyChanged by default
        // IsEmpty is bound via x:Bind which re-evaluates on collection change
    }

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "新建项目",
                PrimaryButtonText = "创建",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
            };

            var nameBox = new TextBox
            {
                PlaceholderText = "项目名称",
                Text = "我的渲染项目",
                Margin = new Thickness(0, 8, 0, 0),
            };
            dialog.Content = nameBox;

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var name = string.IsNullOrWhiteSpace(nameBox.Text) ? "未命名项目" : nameBox.Text.Trim();
                var project = ProjectService.Create(name, string.Empty);
                RefreshProjects();
                NavigateToProject?.Invoke(project);
            }
        }
        catch { }
    }

    private async void ImportProject_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".brsproj");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                var project = ProjectService.Import(file.Path);
                if (project != null)
                {
                    RefreshProjects();
                    NavigateToProject?.Invoke(project);
                }
                else
                {
                    await new ContentDialog
                    {
                        Title = "导入失败",
                        Content = "文件格式无效或已损坏。",
                        CloseButtonText = "确定",
                        XamlRoot = this.XamlRoot,
                    }.ShowAsync();
                }
            }
        }
        catch { }
    }

    private async void ExportProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
        var project = ProjectService.GetById(id);
        if (project == null) return;

        try
        {
            // 询问是否包含渲染进度
            var includeProgress = false;
            var dialog = new ContentDialog
            {
                Title = "导出项目",
                Content = new CheckBox { Content = "包含渲染进度和缩略图缓存", IsChecked = false },
                PrimaryButtonText = "导出",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
            };
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            if (dialog.Content is CheckBox cb)
                includeProgress = cb.IsChecked == true;

            // 选择保存位置
            var picker = new FileSavePicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("BlenderRenderStudio 项目", [".brsproj"]);
            picker.SuggestedFileName = project.Name;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                ProjectService.Export(id, file.Path, includeProgress);
            }
        }
        catch { }
    }

    private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
        var project = ProjectService.GetById(id);
        if (project == null) return;

        var dir = !string.IsNullOrEmpty(project.OutputDirectory) && Directory.Exists(project.OutputDirectory)
            ? project.OutputDirectory
            : project.CacheDirectory;

        if (Directory.Exists(dir))
            System.Diagnostics.Process.Start("explorer.exe", dir);
    }

    private void ProjectCard_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RenderProject project)
            NavigateToProject?.Invoke(project);
    }

    private void AddToQueue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string id)
            AddToQueueAction?.Invoke(id);
    }

    private async void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
        var project = ProjectService.GetById(id);
        if (project == null) return;

        try
        {
            var dialog = new ContentDialog
            {
                Title = "删除项目",
                Content = $"确定要删除「{project.Name}」及其所有缓存吗？\n此操作不会删除渲染输出文件。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                ProjectService.Delete(id);
                RefreshProjects();
            }
        }
        catch { }
    }
}
