using System;
using System.Runtime.InteropServices;

namespace BlenderRenderStudio.Services;

/// <summary>
/// 监控系统物理内存和提交内存使用率（仅 Windows）。
/// </summary>
public class MemoryMonitor
{
    private readonly float _threshold;
    private readonly float _pollSeconds;
    private double _lastCheckedAt;

    public MemoryMonitor(float threshold = 85.0f, float pollSeconds = 1.0f)
    {
        _threshold = threshold;
        _pollSeconds = pollSeconds;
    }

    public record MemoryStatus(float PhysicalUsed, float CommitUsed, bool IsOverThreshold);

    public MemoryStatus? Check()
    {
        var now = Environment.TickCount64 / 1000.0;
        if (now - _lastCheckedAt < _pollSeconds) return null;
        _lastCheckedAt = now;

        var (phys, commit) = ReadUsage();
        if (phys < 0) return null;

        bool over = phys >= _threshold || commit >= _threshold;
        return new MemoryStatus(phys, commit, over);
    }

    public static (float Physical, float Commit) ReadUsage()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return (-1, -1);

        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref status)) return (-1, -1);

            float phys = status.dwMemoryLoad;
            float commit = status.ullTotalPageFile > 0
                ? (1f - (float)status.ullAvailPageFile / status.ullTotalPageFile) * 100f
                : -1;
            return (phys, commit);
        }
        catch { return (-1, -1); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
