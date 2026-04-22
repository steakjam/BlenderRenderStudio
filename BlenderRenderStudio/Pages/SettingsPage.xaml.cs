using System;
using System.IO;
using BlenderRenderStudio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace BlenderRenderStudio.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadSettings();
        Loaded += (_, _) => HideNumberBoxDeleteButtons(this);
    }

    /// <summary>隐藏 NumberBox 内 TextBox 的清除按钮(X)</summary>
    private static void HideNumberBoxDeleteButtons(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is NumberBox nb)
            {
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

    private void LoadSettings()
    {
        var s = SettingsService.Load();
        BlenderPathBox.Text = s.BlenderPath;
        ShowLogToggle.IsOn = s.ShowLogPanel;
        AutoStartToggle.IsOn = StartupService.IsEnabled();

        // 分布式渲染
        RemoteWorkerToggle.IsOn = s.EnableRemoteWorker;
        DeviceNameBox.Text = s.DeviceName;
        NetworkPortBox.Value = s.NetworkPort;

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

    private void RemoteWorker_Toggled(object sender, RoutedEventArgs e)
    {
        var s = SettingsService.Load();
        s.EnableRemoteWorker = RemoteWorkerToggle.IsOn;
        SettingsService.Save(s);
    }

    private void DeviceName_Changed(object sender, TextChangedEventArgs e)
    {
        var name = DeviceNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        var s = SettingsService.Load();
        s.DeviceName = name;
        SettingsService.Save(s);
    }

    private void NetworkPort_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue)) return;
        var port = (int)args.NewValue;
        if (port < 1024 || port > 65535) return;
        var s = SettingsService.Load();
        s.NetworkPort = port;
        SettingsService.Save(s);
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
