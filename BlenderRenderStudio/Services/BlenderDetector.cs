using System;
using System.IO;
using Microsoft.Win32;

namespace BlenderRenderStudio.Services;

/// <summary>
/// 自动检测 Blender 安装路径。
/// 策略顺序：Steam 注册表 → 默认安装路径 → PATH 环境变量
/// </summary>
public static class BlenderDetector
{
    public static string? Detect()
    {
        return FromSteamRegistry()
            ?? FromDefaultPaths()
            ?? FromPathEnv();
    }

    private static string? FromSteamRegistry()
    {
        try
        {
            // Steam 通过 Uninstall 注册表记录安装路径
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 365670");
            var installLocation = key?.GetValue("InstallLocation") as string;
            if (!string.IsNullOrEmpty(installLocation))
            {
                var exe = Path.Combine(installLocation, "blender.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch { }

        // 遍历 Steam 库目录
        try
        {
            using var steamKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
            var steamPath = steamKey?.GetValue("SteamPath") as string;
            if (!string.IsNullOrEmpty(steamPath))
            {
                var candidate = Path.Combine(steamPath, "steamapps", "common", "Blender", "blender.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { }

        return null;
    }

    private static string? FromDefaultPaths()
    {
        string[] defaultPaths =
        [
            @"C:\Program Files\Blender Foundation",
            @"C:\Program Files (x86)\Blender Foundation",
            @"D:\Program Files\Blender Foundation",
            @"D:\SteamLibrary\steamapps\common\Blender",
            @"E:\SteamLibrary\steamapps\common\Blender",
        ];

        foreach (var basePath in defaultPaths)
        {
            if (!Directory.Exists(basePath)) continue;

            // 直接在目录下检查
            var direct = Path.Combine(basePath, "blender.exe");
            if (File.Exists(direct)) return direct;

            // 检查子目录（如 Blender 4.0/blender.exe）
            try
            {
                foreach (var subDir in Directory.GetDirectories(basePath))
                {
                    var exe = Path.Combine(subDir, "blender.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
            catch { }
        }

        return null;
    }

    private static string? FromPathEnv()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var exe = Path.Combine(dir.Trim(), "blender.exe");
                if (File.Exists(exe)) return exe;
            }
            catch { }
        }

        return null;
    }
}
