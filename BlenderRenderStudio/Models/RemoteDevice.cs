using System;
using System.Text.Json.Serialization;
using BlenderRenderStudio.Helpers;

namespace BlenderRenderStudio.Models;

/// <summary>
/// 远程渲染设备数据模型。
/// 主机维护设备列表，持久化到 settings；从机广播自身信息。
/// </summary>
public class RemoteDevice : ObservableObject
{
    private DeviceStatus _status = DeviceStatus.Offline;
    private int _performanceWeight = 1;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "未命名设备";
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; } = 19821;

    public DeviceStatus Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    // ── 硬件信息（注册时由 Worker 上报）──
    public int CpuCores { get; set; }
    public string GpuName { get; set; } = string.Empty;
    public long RamMB { get; set; }

    /// <summary>性能权重（用于帧分配比例），用户可手动调整</summary>
    public int PerformanceWeight
    {
        get => _performanceWeight;
        set => SetProperty(ref _performanceWeight, Math.Max(1, value));
    }

    [JsonIgnore]
    public DateTime LastHeartbeat { get; set; }

    /// <summary>是否为本机（不参与网络通信）</summary>
    [JsonIgnore]
    public bool IsLocal { get; set; }

    // ── UI 绑定属性 ──
    [JsonIgnore]
    public string StatusText => Status switch
    {
        DeviceStatus.Online => "在线",
        DeviceStatus.Busy => "渲染中",
        DeviceStatus.Error => "错误",
        _ => "离线",
    };

    [JsonIgnore]
    public string DisplayAddress => IsLocal ? "本机" : $"{IpAddress}:{Port}";

    [JsonIgnore]
    public string HardwareInfo => string.IsNullOrEmpty(GpuName)
        ? $"CPU {CpuCores}核 | RAM {RamMB / 1024}GB"
        : $"CPU {CpuCores}核 | {GpuName} | RAM {RamMB / 1024}GB";
}

public enum DeviceStatus
{
    Offline,
    Online,
    Busy,
    Error
}
