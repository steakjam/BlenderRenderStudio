using System;
using Microsoft.Win32;

namespace BlenderRenderStudio.Services;

/// <summary>
/// 开机自启动管理，通过注册表 HKCU\...\Run 键控制。
/// </summary>
public static class StartupService
{
    private const string AppName = "BlenderRenderStudio";
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void Enable()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.SetValue(AppName, $"\"{exePath}\"");
        }
        catch { }
    }

    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(AppName, throwOnMissingValue: false);
        }
        catch { }
    }
}
