using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using BlenderRenderStudio.Models;
using BlenderRenderStudio.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

namespace BlenderRenderStudio;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();

        // 设置窗口尺寸
        var appWindow = this.AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));

        ViewModel = new MainViewModel(DispatcherQueue);

        // 窗口关闭时杀掉 Blender 进程
        this.Closed += (_, _) => ViewModel.Shutdown();

        // 注入续渲询问对话框
        ViewModel.AskResumeAsync = async (resumeFrame) =>
        {
            var dialog = new ContentDialog
            {
                Title = "检测到上次渲染进度",
                Content = $"上次渲染中断于帧 {resumeFrame}，是否从该帧继续？\n选择「从头开始」将清除进度并重新渲染。",
                PrimaryButtonText = "继续渲染",
                SecondaryButtonText = "从头开始",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot,
            };
            var result = await dialog.ShowAsync();
            return result switch
            {
                ContentDialogResult.Primary => "resume",
                ContentDialogResult.Secondary => "restart",
                _ => "cancel",
            };
        };

        // 日志自动滚动到底部
        ViewModel.LogEntries.CollectionChanged += (_, _) =>
        {
            if (LogListView.Items.Count > 0)
            {
                LogListView.ScrollIntoView(LogListView.Items[^1]);
            }
        };
    }

    // ── 文件选择器 ──────────────────────────────────────────────────

    private async void BrowseBlendFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".blend");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            ViewModel.BlendFilePath = file.Path;

            // 自动填充输出路径：.blend 文件同目录下的 render/frame_#####
            if (string.IsNullOrWhiteSpace(ViewModel.OutputPath))
            {
                var blendDir = Path.GetDirectoryName(file.Path);
                if (!string.IsNullOrEmpty(blendDir))
                {
                    ViewModel.OutputPath = Path.Combine(blendDir, "render", "frame_#####");
                }
            }
        }
    }

    private async void BrowseOutputPath_Click(object sender, RoutedEventArgs e)
    {
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

    // ── 帧列表选择 → 加载预览 ──────────────────────────────────────

    private async void FrameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView lv && lv.SelectedItem is FrameResult frame && !string.IsNullOrEmpty(frame.OutputPath))
        {
            try
            {
                var path = frame.OutputPath.Replace('/', '\\');
                if (!File.Exists(path)) return;

                var bytes = await File.ReadAllBytesAsync(path);
                var ms = new MemoryStream(bytes);
                var stream = ms.AsRandomAccessStream();
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                ViewModel.PreviewImage = bitmap;
            }
            catch { /* 部分格式无法加载 */ }
        }
    }
}
