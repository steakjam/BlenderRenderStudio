using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace BlenderRenderStudio.Helpers;

/// <summary>
/// 通过 DXGI 检测可用 GPU 并确定 D2D 并发纹理上限。
/// 策略：枚举所有适配器 → 排除 WARP → 选显存空闲率最高的 → 占用率过高则回退 WARP 模式。
/// </summary>
internal static class GpuDetector
{
    private static readonly Guid IID_IDXGIFactory4 = new("1bc6ea02-ef36-464f-bf0c-21ca39e5168a");

    [DllImport("dxgi.dll", PreserveSig = false)]
    private static extern void CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    /// <summary>
    /// 计算适合当前设备的网格缩略图最大 D2D 纹理数。
    /// - 有空闲硬件 GPU（占用率 &lt; 90%）→ 120
    /// - 所有硬件 GPU 占用率 ≥ 90% 或仅 WARP → 20
    /// </summary>
    public static int GetMaxAliveGridSources()
    {
        // WinUI 3 的 SoftwareBitmapSource 使用 composition 层 D2D 设备（WARP），
        // 与用户 GPU 无关。实测硬件 GPU 机器上 ~32 个 source 就会触发 0xC000027B。
        const int WARP_LIMIT = 20;
        const int HW_LIMIT = 30;
        const double BUSY_THRESHOLD = 0.90; // 占用率超过 90% 视为不可用

        try
        {
            var iid = IID_IDXGIFactory4;
            CreateDXGIFactory1(ref iid, out var factoryPtr);
            if (factoryPtr == IntPtr.Zero) return WARP_LIMIT;

            try
            {
                var enumAdapters = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(
                    GetVTableEntry(factoryPtr, 12));

                var adapters = new List<AdapterInfo>();
                for (uint idx = 0; ; idx++)
                {
                    int hr = enumAdapters(factoryPtr, idx, out var adapterPtr);
                    if (hr != 0 || adapterPtr == IntPtr.Zero) break;

                    try
                    {
                        var info = GetAdapterInfo(adapterPtr);
                        if (info != null) adapters.Add(info);
                    }
                    finally { Marshal.Release(adapterPtr); }
                }

                // 过滤掉 WARP 适配器
                var hwAdapters = adapters.FindAll(a => !a.IsWarp);
                if (hwAdapters.Count == 0)
                {
                    System.Diagnostics.Trace.WriteLine("[GPU] 无硬件 GPU，使用 WARP 模式 (limit=20)");
                    return WARP_LIMIT;
                }

                // 选空闲率最高的硬件 GPU
                hwAdapters.Sort((a, b) => a.UsageRatio.CompareTo(b.UsageRatio)); // 升序=最空闲的在前
                var best = hwAdapters[0];

                System.Diagnostics.Trace.WriteLine(
                    $"[GPU] 最佳适配器: {best.Description} (VRAM={best.TotalVramMB}MB, 占用={best.UsageRatio:P0})");

                if (best.UsageRatio >= BUSY_THRESHOLD)
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"[GPU] 所有硬件 GPU 占用率 ≥ {BUSY_THRESHOLD:P0}，回退 WARP 模式 (limit=20)");
                    return WARP_LIMIT;
                }

                return HW_LIMIT;
            }
            finally { Marshal.Release(factoryPtr); }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[GPU] 检测失败，默认 WARP 模式: {ex.Message}");
            return WARP_LIMIT;
        }
    }

    /// <summary>兼容旧接口</summary>
    public static bool IsWarp() => GetMaxAliveGridSources() <= 20;

    private static AdapterInfo? GetAdapterInfo(IntPtr adapterPtr)
    {
        // GetDesc1 (vtable slot 10)
        var getDesc = Marshal.GetDelegateForFunctionPointer<GetDesc1Delegate>(
            GetVTableEntry(adapterPtr, 10));

        var desc = new DXGI_ADAPTER_DESC1();
        if (getDesc(adapterPtr, ref desc) != 0) return null;

        var description = new string(desc.Description).TrimEnd('\0');
        bool isWarp = (desc.VendorId == 0x1414 && desc.DeviceId == 0x8C)
            || description.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)
            || description.Contains("WARP", StringComparison.OrdinalIgnoreCase)
            || description.Contains("Software", StringComparison.OrdinalIgnoreCase);

        long totalVram = (long)(nuint)desc.DedicatedVideoMemory;
        double usageRatio = 0;

        // 尝试通过 IDXGIAdapter3::QueryVideoMemoryInfo 获取实时显存占用
        // IDXGIAdapter3 vtable: IUnknown(3) + IDXGIObject(4) + IDXGIAdapter(4) + IDXGIAdapter1(1) + IDXGIAdapter2(1) + IDXGIAdapter3(4)
        // QueryVideoMemoryInfo = slot 3+4+4+1+1+0 = 13...
        // Actually: IDXGIAdapter3 inherits IDXGIAdapter2 which inherits IDXGIAdapter1
        // IDXGIAdapter3::QueryVideoMemoryInfo is the first method of IDXGIAdapter3
        // Full vtable: IUnknown(3) + IDXGIObject(1:SetPrivateData, 2:SetPrivateDataInterface, 3:GetPrivateData, 4:GetParent)
        //              + IDXGIAdapter(1:EnumOutputs, 2:GetDesc, 3:CheckInterfaceSupport)
        //              + IDXGIAdapter1(1:GetDesc1)
        //              + IDXGIAdapter2(1:GetDesc2)
        //              + IDXGIAdapter3(1:RegisterHardwareContentProtectionTeardownStatusEvent, 2:UnregisterHardwareContentProtectionTeardownStatus, 3:QueryVideoMemoryInfo, 4:SetVideoMemoryReservation, 5:RegisterVideoMemoryBudgetChangeNotificationEvent, 6:UnregisterVideoMemoryBudgetChangeNotification)
        // So QueryVideoMemoryInfo = 3+4+3+1+1+3 = 15
        try
        {
            var queryMemInfo = Marshal.GetDelegateForFunctionPointer<QueryVideoMemoryInfoDelegate>(
                GetVTableEntry(adapterPtr, 15));

            var memInfo = new DXGI_QUERY_VIDEO_MEMORY_INFO();
            // nodeIndex=0, memorySegmentGroup=0 (DXGI_MEMORY_SEGMENT_GROUP_LOCAL = GPU 本地显存)
            int hr = queryMemInfo(adapterPtr, 0, 0, ref memInfo);
            if (hr == 0 && memInfo.Budget > 0)
            {
                usageRatio = (double)memInfo.CurrentUsage / memInfo.Budget;
                totalVram = (long)memInfo.Budget / 1024 / 1024;
                System.Diagnostics.Trace.WriteLine(
                    $"[GPU]   {description}: Budget={memInfo.Budget / 1024 / 1024}MB, Used={memInfo.CurrentUsage / 1024 / 1024}MB, Ratio={usageRatio:P0}");
            }
            else if (totalVram > 0)
            {
                // QueryVideoMemoryInfo 不可用时用 DedicatedVideoMemory 作为上限参考
                // 无法获取实际占用，假设不忙
                usageRatio = 0;
            }
        }
        catch
        {
            // IDXGIAdapter3 接口不可用（Win10 以前），假设不忙
            usageRatio = 0;
        }

        // totalVram: 从 DedicatedVideoMemory 取值时单位为 bytes；从 Budget 取值时已转为 MB
        long vramMB = totalVram > 1024 * 1024 ? totalVram / 1024 / 1024 : totalVram;
        return new AdapterInfo
        {
            Description = description,
            IsWarp = isWarp,
            TotalVramMB = vramMB,
            UsageRatio = usageRatio
        };
    }

    private static IntPtr GetVTableEntry(IntPtr comObj, int slot)
    {
        var vtable = Marshal.ReadIntPtr(comObj);
        return Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
    }

    private class AdapterInfo
    {
        public string Description { get; init; } = "";
        public bool IsWarp { get; init; }
        public long TotalVramMB { get; init; }
        public double UsageRatio { get; init; }
    }

    // ── COM Delegates ──

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Delegate(IntPtr factory, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Delegate(IntPtr adapter, ref DXGI_ADAPTER_DESC1 desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryVideoMemoryInfoDelegate(IntPtr adapter, uint nodeIndex, uint memorySegmentGroup, ref DXGI_QUERY_VIDEO_MEMORY_INFO info);

    // ── Structs ──

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
        public char[] Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_QUERY_VIDEO_MEMORY_INFO
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
    }
}
