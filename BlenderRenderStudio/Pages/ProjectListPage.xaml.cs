using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
            if (file == null) return;

            // 预读归档信息
            var (preview, blendFileName, frameCount) = ProjectService.PreviewImport(file.Path);
            if (preview == null)
            {
                await new ContentDialog
                {
                    Title = "导入失败",
                    Content = "文件格式无效或已损坏。",
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot,
                }.ShowAsync();
                return;
            }

            var settings = SettingsService.Load();

            // 归档内容摘要
            var summary = $"项目：{preview.Name}\n"
                + $"帧范围：{preview.StartFrame} ~ {preview.EndFrame}\n"
                + (blendFileName != null ? $"Blend 工程：{blendFileName}\n" : "Blend 工程：未包含\n")
                + $"已渲染帧：{frameCount} 个";
            var summaryBlock = new TextBlock
            {
                Text = summary,
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                Opacity = 0.8,
                Margin = new Thickness(0, 0, 0, 8),
            };

            // Blend 文件存放目录
            var blendDirBox = new TextBox
            {
                Header = "Blend 工程存放目录",
                PlaceholderText = "手动输入或点击下方按钮选择目录",
            };
            var blendDirBtn = new Button { Content = "选择目录", Margin = new Thickness(0, 4, 0, 0) };
            blendDirBtn.Click += async (_, _) =>
            {
                var fp = new FolderPicker();
                fp.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                fp.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(fp, hwnd);
                var folder = await fp.PickSingleFolderAsync();
                if (folder != null) blendDirBox.Text = folder.Path;
            };

            // 渲染输出目录
            var outputDirBox = new TextBox
            {
                Header = "渲染输出目录",
                PlaceholderText = "手动输入或点击下方按钮选择目录",
            };
            var outputDirBtn = new Button { Content = "选择目录", Margin = new Thickness(0, 4, 0, 0) };
            outputDirBtn.Click += async (_, _) =>
            {
                var fp = new FolderPicker();
                fp.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                fp.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(fp, hwnd);
                var folder = await fp.PickSingleFolderAsync();
                if (folder != null) outputDirBox.Text = folder.Path;
            };

            // Blender 路径（本机已安装）
            var blenderPathBox = new TextBox
            {
                Header = "Blender 可执行文件路径",
                Text = settings.BlenderPath,
                PlaceholderText = "手动输入或点击下方按钮浏览",
            };
            var blenderPathBtn = new Button { Content = "浏览...", Margin = new Thickness(0, 4, 0, 0) };
            blenderPathBtn.Click += async (_, _) =>
            {
                var bp = new FileOpenPicker();
                bp.SuggestedStartLocation = PickerLocationId.ComputerFolder;
                bp.FileTypeFilter.Add(".exe");
                WinRT.Interop.InitializeWithWindow.Initialize(bp, hwnd);
                var bf = await bp.PickSingleFileAsync();
                if (bf != null) blenderPathBox.Text = bf.Path;
            };

            var panel = new StackPanel { Spacing = 6, MinWidth = 420 };
            panel.Children.Add(summaryBlock);
            panel.Children.Add(blendDirBox);
            panel.Children.Add(blendDirBtn);
            panel.Children.Add(outputDirBox);
            panel.Children.Add(outputDirBtn);
            panel.Children.Add(blenderPathBox);
            panel.Children.Add(blenderPathBtn);

            var configDialog = new ContentDialog
            {
                Title = "导入项目",
                Content = panel,
                PrimaryButtonText = "导入",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
            };

            var configResult = await configDialog.ShowAsync();
            if (configResult != ContentDialogResult.Primary) return;

            var blendDir = blendDirBox.Text.Trim();
            var outputDir = outputDirBox.Text.Trim();
            var blenderPath = blenderPathBox.Text.Trim();

            if (string.IsNullOrEmpty(blendDir) || string.IsNullOrEmpty(outputDir))
            {
                await new ContentDialog
                {
                    Title = "配置不完整",
                    Content = "Blend 工程目录和渲染输出目录不能为空。",
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot,
                }.ShowAsync();
                return;
            }

            // 显示导入进度对话框
            var progressDialog = CreateProgressDialog("导入项目", $"正在导入「{preview.Name}」…");
            var dialogTask = progressDialog.ShowAsync();
            // 确保对话框已渲染再开始后台任务
            await Task.Yield();

            RenderProject? project = null;
            await Task.Run(() =>
            {
                try { project = ProjectService.Import(file.Path, blendDir, outputDir, blenderPath); }
                catch { }
            });

            progressDialog.Hide();

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
                    Content = "导入过程中出现错误，请检查路径是否正确。",
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot,
                }.ShowAsync();
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
            // 选择保存位置
            var picker = new FileSavePicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("BlenderRenderStudio 项目", [".brsproj"]);
            picker.SuggestedFileName = project.Name;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            // 显示导出进度对话框
            var progressDialog = CreateProgressDialog("导出项目", $"正在导出「{project.Name}」…");
            var dialogTask = progressDialog.ShowAsync();
            // 确保对话框已渲染再开始后台任务
            await Task.Yield();

            bool success = false;
            await Task.Run(() =>
            {
                try { ProjectService.Export(id, file.Path); success = true; }
                catch { }
            });

            progressDialog.Hide();

            await new ContentDialog
            {
                Title = success ? "导出完成" : "导出失败",
                Content = success ? $"项目已导出到：\n{file.Path}" : "导出过程中出现错误，请检查磁盘空间。",
                CloseButtonText = "确定",
                XamlRoot = this.XamlRoot,
            }.ShowAsync();
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

    /// <summary>创建带 ProgressRing 的进度对话框（无按钮，需代码 Hide）</summary>
    private ContentDialog CreateProgressDialog(string title, string message)
    {
        var panel = new StackPanel { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new ProgressRing { IsActive = true, Width = 40, Height = 40 });
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        return new ContentDialog
        {
            Title = title,
            Content = panel,
            XamlRoot = this.XamlRoot,
        };
    }
}
