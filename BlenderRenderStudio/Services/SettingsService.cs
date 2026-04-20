using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlenderRenderStudio.Services;

/// <summary>
/// 将用户配置持久化到操作系统标准应用数据目录。
/// - MSIX 打包模式：Windows.Storage.ApplicationData.Current.LocalFolder（系统隔离）
/// - 未打包模式（Debug）：%LocalAppData%/BlenderRenderStudio
/// </summary>
public class SettingsService
{
    private static readonly string SettingsDir = GetAppLocalDir();
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    /// <summary>应用配置数据目录（供日志显示）</summary>
    public static string StorageDir => SettingsDir;

    /// <summary>进度文件固定在设置目录下，不受工作目录影响。</summary>
    public static string ProgressFilePath => Path.Combine(SettingsDir, "render_progress.txt");

    /// <summary>磁盘缩略图缓存目录（240px 原始像素，避免重复 WIC 解码）</summary>
    public static string ThumbnailCacheDir => Path.Combine(SettingsDir, "thumbcache");

    /// <summary>
    /// 获取应用本地数据目录。打包模式使用 ApplicationData API（系统自动隔离清理），
    /// 未打包模式回退到 %LocalAppData%。
    /// </summary>
    private static string GetAppLocalDir()
    {
        try
        {
            // MSIX 打包模式：ApplicationData 已按应用隔离，无需子目录
            return Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        }
        catch
        {
            // 未打包模式（WindowsPackageType=None）：手动在 %LocalAppData% 创建子目录
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BlenderRenderStudio");
        }
    }

    public static UserSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new UserSettings();
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<UserSettings>(json) ?? new UserSettings();
        }
        catch { return new UserSettings(); }
    }

    public static void Save(UserSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsService.Save] 保存失败: {ex.Message}  路径: {SettingsPath}");
        }
    }
}

public class UserSettings
{
    [JsonPropertyName("blenderPath")]
    public string BlenderPath { get; set; } = @"D:\SteamLibrary\steamapps\common\Blender\blender.exe";

    [JsonPropertyName("blendFilePath")]
    public string BlendFilePath { get; set; } = string.Empty;

    [JsonPropertyName("outputPath")]
    public string OutputPath { get; set; } = string.Empty;

    [JsonPropertyName("outputPrefix")]
    public string OutputPrefix { get; set; } = "frame_";

    [JsonPropertyName("startFrame")]
    public int StartFrame { get; set; } = 1;

    [JsonPropertyName("endFrame")]
    public int EndFrame { get; set; } = 250;

    [JsonPropertyName("batchSize")]
    public int BatchSize { get; set; } = 50;

    [JsonPropertyName("memoryThreshold")]
    public float MemoryThreshold { get; set; } = 85.0f;

    [JsonPropertyName("memoryPollSeconds")]
    public float MemoryPollSeconds { get; set; } = 1.0f;

    [JsonPropertyName("restartDelaySeconds")]
    public float RestartDelaySeconds { get; set; } = 3.0f;

    [JsonPropertyName("maxAutoRestarts")]
    public int MaxAutoRestarts { get; set; } = 10;

    [JsonPropertyName("autoRestartOnCrash")]
    public bool AutoRestartOnCrash { get; set; } = true;

    [JsonPropertyName("enableBlackFrameDetection")]
    public bool EnableBlackFrameDetection { get; set; } = true;

    [JsonPropertyName("blackFrameThreshold")]
    public double BlackFrameThreshold { get; set; } = 5.0;

    [JsonPropertyName("outputType")]
    public int OutputType { get; set; } = 0; // 0=ImageSequence, 1=Video, 2=SingleFrame

    [JsonPropertyName("singleFrameNumber")]
    public int SingleFrameNumber { get; set; } = 1;

    [JsonPropertyName("showLogPanel")]
    public bool ShowLogPanel { get; set; } = false;
}
