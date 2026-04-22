using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using BlenderRenderStudio.Models;
using BlenderRenderStudio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BlenderRenderStudio.Pages;

/// <summary>设备管理页面：显示/添加/扫描局域网渲染设备</summary>
public sealed partial class DeviceManagementPage : Page
{
    public ObservableCollection<RemoteDevice> Devices { get; } = [];

    private NetworkDiscoveryService? _networkService;

    public DeviceManagementPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void SetNetworkService(NetworkDiscoveryService service)
    {
        _networkService = service;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshDeviceList();

        if (_networkService != null)
            _networkService.DevicesChanged += OnDevicesChanged;

        var settings = SettingsService.Load();
        SubtitleText.Text = settings.EnableRemoteWorker
            ? $"本机已启用远程任务接收 (端口 {settings.NetworkPort})"
            : "管理局域网内的渲染设备，分布式协作渲染";
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_networkService != null)
            _networkService.DevicesChanged -= OnDevicesChanged;
    }

    private void OnDevicesChanged()
    {
        DispatcherQueue.TryEnqueue(RefreshDeviceList);
    }

    private void RefreshDeviceList()
    {
        Devices.Clear();

        // 添加本机
        var settings = SettingsService.Load();
        var localDevice = new RemoteDevice
        {
            Name = $"{settings.DeviceName} (本机)",
            IsLocal = true,
            Status = DeviceStatus.Online,
            CpuCores = Environment.ProcessorCount,
            GpuName = Helpers.GpuDetector.IsWarp() ? "WARP (无独显)" : "硬件 GPU",
            RamMB = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024,
            PerformanceWeight = settings.RemoteDevices
                .FirstOrDefault(d => d.IsLocal)?.PerformanceWeight ?? 3,
        };
        Devices.Add(localDevice);

        // 添加远程设备
        if (_networkService != null)
        {
            foreach (var device in _networkService.GetDevices())
                Devices.Add(device);
        }
        else
        {
            // 无网络服务时从设置加载
            foreach (var device in settings.RemoteDevices.Where(d => !d.IsLocal))
                Devices.Add(device);
        }

        DeviceListView.ItemsSource = Devices;
    }

    private bool _isScanning;

    private async void ScanLan_Click(object sender, RoutedEventArgs e)
    {
        if (_networkService == null || _isScanning) return;
        _isScanning = true;

        int beforeCount = _networkService.GetDevices().Count;

        // 按钮禁用 + 文字变化
        ScanButton.IsEnabled = false;
        ScanIcon.Opacity = 0;
        ScanText.Text = "正在扫描局域网…";

        // InfoBar 显示扫描中（带进度条）
        ScanInfoBar.Title = "扫描中";
        ScanInfoBar.Message = "正在广播搜索局域网内的渲染设备，请稍候…";
        ScanInfoBar.Severity = InfoBarSeverity.Informational;
        ScanInfoBar.IsOpen = true;

        _networkService.ScanLan();

        // 等待 3 秒让设备响应 UDP 广播
        await Task.Delay(3000);

        // 恢复按钮
        ScanButton.IsEnabled = true;
        ScanIcon.Opacity = 1;
        ScanText.Text = "扫描局域网";
        _isScanning = false;

        RefreshDeviceList();

        int afterCount = _networkService.GetDevices().Count;
        int found = afterCount - beforeCount;

        // InfoBar 显示结果
        if (found > 0)
        {
            ScanInfoBar.Title = "扫描完成";
            ScanInfoBar.Message = $"发现 {found} 台新设备";
            ScanInfoBar.Severity = InfoBarSeverity.Success;
        }
        else
        {
            ScanInfoBar.Title = "扫描完成";
            ScanInfoBar.Message = "未发现新设备。请确认目标设备已启动并开启了远程任务接收。";
            ScanInfoBar.Severity = InfoBarSeverity.Warning;
        }
    }

    private async void AddDevice_Click(object sender, RoutedEventArgs e)
    {
        var ip = ManualIpBox.Text.Trim();
        if (string.IsNullOrEmpty(ip)) return;

        if (!int.TryParse(ManualPortBox.Text.Trim(), out int port))
            port = 19821;

        if (_networkService != null)
        {
            var device = await _networkService.AddDeviceManuallyAsync(ip, port);
            if (device == null)
            {
                // 连接失败，仍然添加为离线设备
                var offline = new RemoteDevice
                {
                    Name = ip,
                    IpAddress = ip,
                    Port = port,
                    Status = DeviceStatus.Offline,
                };
                Devices.Add(offline);
            }

            SaveDevicesToSettings();
            RefreshDeviceList();
        }

        ManualIpBox.Text = string.Empty;
    }

    private async void AdjustWeight_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
        var device = Devices.FirstOrDefault(d => d.Id == id);
        if (device == null) return;

        var box = new NumberBox
        {
            Value = device.PerformanceWeight,
            Minimum = 1,
            Maximum = 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Header = $"设备: {device.Name}",
        };

        var dialog = new ContentDialog
        {
            Title = "调整性能权重",
            Content = box,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            XamlRoot = this.XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            device.PerformanceWeight = (int)box.Value;
            SaveDevicesToSettings();
            RefreshDeviceList();
        }
    }

    private void RemoveDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
        var device = Devices.FirstOrDefault(d => d.Id == id);
        if (device == null || device.IsLocal) return;

        Devices.Remove(device);
        SaveDevicesToSettings();
    }

    private void SaveDevicesToSettings()
    {
        var settings = SettingsService.Load();
        settings.RemoteDevices = Devices.ToList();
        SettingsService.Save(settings);
    }
}
