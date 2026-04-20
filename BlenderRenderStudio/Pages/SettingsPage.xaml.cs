using System;
using System.IO;
using BlenderRenderStudio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace BlenderRenderStudio.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadSettings();
    }

    private void LoadSettings()
    {
        var s = SettingsService.Load();
        BlenderPathBox.Text = s.BlenderPath;
        ShowLogToggle.IsOn = s.ShowLogPanel;
        AutoStartToggle.IsOn = StartupService.IsEnabled();
        UpdateCacheSize();
    }

    private async void BrowseBlender_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            picker.FileTypeFilter.Add(".exe");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                BlenderPathBox.Text = file.Path;
                SaveBlenderPath(file.Path);
                DetectStatusText.Text = "";
            }
        }
        catch { }
    }

    private void AutoDetect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DetectStatusText.Text = "正在检测...";
            var path = BlenderDetector.Detect();
            if (path != null)
            {
                BlenderPathBox.Text = path;
                SaveBlenderPath(path);
                DetectStatusText.Text = $"已找到：{path}";
            }
            else
            {
                DetectStatusText.Text = "未找到 Blender 安装路径，请手动选择";
            }
        }
        catch (Exception ex) { DetectStatusText.Text = $"检测失败：{ex.Message}"; }
    }

    private static void SaveBlenderPath(string path)
    {
        var s = SettingsService.Load();
        s.BlenderPath = path;
        SettingsService.Save(s);
    }

    private void ShowLog_Toggled(object sender, RoutedEventArgs e)
    {
        var s = SettingsService.Load();
        s.ShowLogPanel = ShowLogToggle.IsOn;
        SettingsService.Save(s);
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
            long totalBytes = 0;
            var cacheBase = Path.Combine(SettingsService.StorageDir, "ProjectCache");
            if (Directory.Exists(cacheBase))
            {
                foreach (var f in Directory.EnumerateFiles(cacheBase, "*", SearchOption.AllDirectories))
                    totalBytes += new FileInfo(f).Length;
            }
            // 也包含旧的全局缓存
            var oldDir = SettingsService.ThumbnailCacheDir;
            if (Directory.Exists(oldDir))
            {
                foreach (var f in Directory.EnumerateFiles(oldDir))
                    totalBytes += new FileInfo(f).Length;
            }

            CacheSizeText.Text = totalBytes < 1024 * 1024
                ? $"{totalBytes / 1024.0:F1} KB"
                : $"{totalBytes / (1024.0 * 1024):F1} MB";
        }
        catch { CacheSizeText.Text = "无法读取"; }
    }

    private async void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "清除全部缓存",
                Content = "确定要清除所有项目的缩略图缓存吗？",
                PrimaryButtonText = "清除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var cacheBase = Path.Combine(SettingsService.StorageDir, "ProjectCache");
                if (Directory.Exists(cacheBase))
                    Directory.Delete(cacheBase, recursive: true);
                var oldDir = SettingsService.ThumbnailCacheDir;
                if (Directory.Exists(oldDir))
                    Directory.Delete(oldDir, recursive: true);
                UpdateCacheSize();
            }
        }
        catch { }
    }
}
