using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlenderRenderStudio.Models;

namespace BlenderRenderStudio.Services;

/// <summary>
/// 局域网设备发现 + HTTP 控制服务。
/// - UDP 广播（端口 19820）：Worker 广播存在，Master 监听发现
/// - HTTP（端口 19821）：Worker→Master 注册/心跳/轮询任务/上传帧
/// </summary>
public sealed class NetworkDiscoveryService : IDisposable
{
    private const int UDP_PORT = 19820;
    private const string BROADCAST_PREFIX = "BRST_WORKER:";

    private UdpClient? _udpListener;
    private HttpListener? _httpListener;
    private CancellationTokenSource? _cts;
    private Timer? _broadcastTimer;
    private Timer? _heartbeatCheckTimer;

    private readonly List<RemoteDevice> _devices = [];
    private readonly object _lock = new();

    /// <summary>设备状态变化事件（UI 线程需要 Dispatch）</summary>
    public event Action? DevicesChanged;

    /// <summary>当前已知设备列表（只读快照）</summary>
    public List<RemoteDevice> GetDevices()
    {
        lock (_lock) return [.. _devices];
    }

    // ════════════════════════════════════════════════════════════════
    // Master 模式：监听 UDP 广播 + 启动 HTTP 服务
    // ════════════════════════════════════════════════════════════════

    /// <summary>以 Master 模式启动：监听设备广播 + HTTP API</summary>
    public void StartMaster(int httpPort, List<RemoteDevice>? savedDevices = null)
    {
        _cts = new CancellationTokenSource();

        if (savedDevices != null)
        {
            lock (_lock)
            {
                _devices.Clear();
                _devices.AddRange(savedDevices);
            }
        }

        StartUdpListener();
        StartHttpServer(httpPort);
        StartHeartbeatCheck();

        System.Diagnostics.Trace.WriteLine($"[Network] Master 启动: UDP={UDP_PORT}, HTTP={httpPort}");
    }

    /// <summary>以 Worker 模式启动：UDP 广播自身 + 定期心跳</summary>
    public void StartWorker(string deviceName, int httpPort)
    {
        _cts = new CancellationTokenSource();

        // 每 3 秒广播一次
        _broadcastTimer = new Timer(_ => BroadcastPresence(deviceName, httpPort),
            null, TimeSpan.Zero, TimeSpan.FromSeconds(3));

        System.Diagnostics.Trace.WriteLine($"[Network] Worker 启动: 广播 {deviceName}:{httpPort}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _broadcastTimer?.Dispose();
        _broadcastTimer = null;
        _heartbeatCheckTimer?.Dispose();
        _heartbeatCheckTimer = null;

        try { _udpListener?.Close(); } catch { }
        _udpListener = null;

        try { _httpListener?.Stop(); } catch { }
        _httpListener = null;

        System.Diagnostics.Trace.WriteLine("[Network] 服务已停止");
    }

    // ── UDP 广播/监听 ─────────────────────────────────────────────

    private void BroadcastPresence(string name, int port)
    {
        try
        {
            using var client = new UdpClient { EnableBroadcast = true };
            var localIp = GetLocalIPv4();
            var message = $"{BROADCAST_PREFIX}{localIp}:{port}:{name}";
            var bytes = Encoding.UTF8.GetBytes(message);
            client.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, UDP_PORT));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Network] UDP 广播失败: {ex.Message}");
        }
    }

    private void StartUdpListener()
    {
        _udpListener = new UdpClient(new IPEndPoint(IPAddress.Any, UDP_PORT));
        _ = Task.Run(async () =>
        {
            try
            {
                while (_cts is { IsCancellationRequested: false })
                {
                    var result = await _udpListener.ReceiveAsync();
                    var message = Encoding.UTF8.GetString(result.Buffer);
                    if (message.StartsWith(BROADCAST_PREFIX))
                        ProcessDiscovery(message[BROADCAST_PREFIX.Length..]);
                }
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[Network] UDP 监听异常: {ex.Message}");
            }
        });
    }

    private void ProcessDiscovery(string payload)
    {
        // 格式: "192.168.1.102:19821:工作站B"
        var parts = payload.Split(':', 3);
        if (parts.Length < 3) return;

        var ip = parts[0];
        if (!int.TryParse(parts[1], out int port)) return;
        var name = parts[2];

        // 忽略自身
        var localIp = GetLocalIPv4();
        if (ip == localIp) return;

        lock (_lock)
        {
            var existing = _devices.FirstOrDefault(d => d.IpAddress == ip && d.Port == port);
            if (existing != null)
            {
                existing.Name = name;
                existing.Status = DeviceStatus.Online;
                existing.LastHeartbeat = DateTime.UtcNow;
            }
            else
            {
                _devices.Add(new RemoteDevice
                {
                    Name = name,
                    IpAddress = ip,
                    Port = port,
                    Status = DeviceStatus.Online,
                    LastHeartbeat = DateTime.UtcNow,
                });
            }
        }

        DevicesChanged?.Invoke();
    }

    // ── HTTP 服务端（Master） ──────────────────────────────────────

    private void StartHttpServer(int port)
    {
        _httpListener = new HttpListener();
        _httpListener.Prefixes.Add($"http://+:{port}/");

        try
        {
            _httpListener.Start();
        }
        catch (HttpListenerException)
        {
            // 没有管理员权限时回退到 localhost
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add($"http://localhost:{port}/");
            _httpListener.Start();
            System.Diagnostics.Trace.WriteLine($"[Network] HTTP 回退到 localhost:{port}（需管理员权限监听所有接口）");
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (_httpListener.IsListening && _cts is { IsCancellationRequested: false })
                {
                    var ctx = await _httpListener.GetContextAsync();
                    _ = Task.Run(() => HandleHttpRequest(ctx));
                }
            }
            catch (ObjectDisposedException) { }
            catch (HttpListenerException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[Network] HTTP 服务异常: {ex.Message}");
            }
        });
    }

    private void HandleHttpRequest(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "";
        var method = ctx.Request.HttpMethod;

        try
        {
            switch (path)
            {
                case "/api/register" when method == "POST":
                    HandleRegister(ctx);
                    break;
                case "/api/heartbeat" when method == "POST":
                    HandleHeartbeat(ctx);
                    break;
                case "/api/tasks/poll" when method == "GET":
                    HandleTaskPoll(ctx);
                    break;
                default:
                    Respond(ctx, 404, "Not Found");
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Network] HTTP 处理异常: {path} → {ex.Message}");
            try { Respond(ctx, 500, ex.Message); } catch { }
        }
    }

    private void HandleRegister(HttpListenerContext ctx)
    {
        using var reader = new System.IO.StreamReader(ctx.Request.InputStream);
        var body = reader.ReadToEnd();
        var info = JsonSerializer.Deserialize<WorkerRegistration>(body);
        if (info == null) { Respond(ctx, 400, "Invalid body"); return; }

        var remoteIp = ctx.Request.RemoteEndPoint?.Address.ToString() ?? info.IpAddress;

        lock (_lock)
        {
            var existing = _devices.FirstOrDefault(d => d.IpAddress == remoteIp && d.Port == info.Port);
            if (existing != null)
            {
                existing.Name = info.Name;
                existing.CpuCores = info.CpuCores;
                existing.GpuName = info.GpuName;
                existing.RamMB = info.RamMB;
                existing.Status = DeviceStatus.Online;
                existing.LastHeartbeat = DateTime.UtcNow;
            }
            else
            {
                _devices.Add(new RemoteDevice
                {
                    Name = info.Name,
                    IpAddress = remoteIp,
                    Port = info.Port,
                    CpuCores = info.CpuCores,
                    GpuName = info.GpuName,
                    RamMB = info.RamMB,
                    Status = DeviceStatus.Online,
                    LastHeartbeat = DateTime.UtcNow,
                });
            }
        }

        DevicesChanged?.Invoke();
        Respond(ctx, 200, "{\"status\":\"ok\"}");
    }

    private void HandleHeartbeat(HttpListenerContext ctx)
    {
        var remoteIp = ctx.Request.RemoteEndPoint?.Address.ToString() ?? "";

        lock (_lock)
        {
            var device = _devices.FirstOrDefault(d => d.IpAddress == remoteIp);
            if (device != null)
            {
                device.Status = DeviceStatus.Online;
                device.LastHeartbeat = DateTime.UtcNow;
            }
        }

        Respond(ctx, 200, "{\"status\":\"ok\"}");
    }

    private void HandleTaskPoll(HttpListenerContext ctx)
    {
        // Phase 2: 返回分配给该 Worker 的待执行任务
        Respond(ctx, 200, "{\"task\":null}");
    }

    // ── 心跳超时检测 ─────────────────────────────────────────────

    private void StartHeartbeatCheck()
    {
        _heartbeatCheckTimer = new Timer(_ =>
        {
            bool changed = false;
            lock (_lock)
            {
                var timeout = DateTime.UtcNow.AddSeconds(-15);
                foreach (var d in _devices)
                {
                    if (d.IsLocal) continue;
                    if (d.Status != DeviceStatus.Offline && d.LastHeartbeat < timeout)
                    {
                        d.Status = DeviceStatus.Offline;
                        changed = true;
                    }
                }
            }
            if (changed) DevicesChanged?.Invoke();
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    // ── 手动扫描 ──────────────────────────────────────────────────

    /// <summary>主动发送 UDP 广播请求，触发 Worker 响应</summary>
    public void ScanLan()
    {
        try
        {
            using var client = new UdpClient { EnableBroadcast = true };
            var message = "BRST_SCAN";
            var bytes = Encoding.UTF8.GetBytes(message);
            client.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, UDP_PORT));
            System.Diagnostics.Trace.WriteLine("[Network] LAN 扫描广播已发送");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Network] 扫描失败: {ex.Message}");
        }
    }

    /// <summary>手动添加设备（尝试连接验证）</summary>
    public async Task<RemoteDevice?> AddDeviceManuallyAsync(string ip, int port)
    {
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = await client.GetAsync($"http://{ip}:{port}/api/status");
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            var info = JsonSerializer.Deserialize<WorkerRegistration>(json);

            var device = new RemoteDevice
            {
                Name = info?.Name ?? ip,
                IpAddress = ip,
                Port = port,
                CpuCores = info?.CpuCores ?? 0,
                GpuName = info?.GpuName ?? "",
                RamMB = info?.RamMB ?? 0,
                Status = DeviceStatus.Online,
                LastHeartbeat = DateTime.UtcNow,
            };

            lock (_lock)
            {
                var existing = _devices.FirstOrDefault(d => d.IpAddress == ip && d.Port == port);
                if (existing != null)
                {
                    existing.Name = device.Name;
                    existing.Status = DeviceStatus.Online;
                    existing.LastHeartbeat = DateTime.UtcNow;
                    device = existing;
                }
                else
                {
                    _devices.Add(device);
                }
            }

            DevicesChanged?.Invoke();
            return device;
        }
        catch
        {
            return null;
        }
    }

    // ── 工具方法 ──────────────────────────────────────────────────

    private static string GetLocalIPv4()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        return addr.Address.ToString();
                }
            }
        }
        catch { }
        return "127.0.0.1";
    }

    private static void Respond(HttpListenerContext ctx, int statusCode, string body)
    {
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}

/// <summary>Worker 注册时上报的硬件信息</summary>
public class WorkerRegistration
{
    public string Name { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public int Port { get; set; }
    public int CpuCores { get; set; }
    public string GpuName { get; set; } = "";
    public long RamMB { get; set; }
}
