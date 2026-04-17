using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlenderRenderStudio.Services;

/// <summary>
/// 将用户配置持久化到 %LocalAppData%/BlenderRenderStudio/settings.json。
/// </summary>
public class SettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BlenderRenderStudio");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    /// <summary>进度文件固定在设置目录下，不受工作目录影响。</summary>
    public static string ProgressFilePath => Path.Combine(SettingsDir, "render_progress.txt");

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
        catch { /* ignore */ }
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
}
