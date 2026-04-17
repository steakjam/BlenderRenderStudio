using System.Collections.Generic;
using System.IO;

namespace BlenderRenderStudio.Models;

/// <summary>渲染输出类型</summary>
public enum RenderOutputType
{
    /// <summary>图片序列（逐帧 PNG/EXR 等），支持批处理和续渲</summary>
    ImageSequence,
    /// <summary>视频文件（FFMPEG），整段一次性渲染</summary>
    Video,
    /// <summary>单帧图片，只渲染指定的一帧</summary>
    SingleFrame,
}

public class RenderConfig
{
    public string BlenderPath { get; set; } = @"D:\SteamLibrary\steamapps\common\Blender\blender.exe";
    public string BlendFilePath { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public int StartFrame { get; set; } = 1;
    public int EndFrame { get; set; } = 250;
    public int BatchSize { get; set; } = 50;
    public float MemoryThreshold { get; set; } = 85.0f;
    public float MemoryPollSeconds { get; set; } = 1.0f;
    public float RestartDelaySeconds { get; set; } = 3.0f;
    public int MaxAutoRestarts { get; set; } = 10;
    public bool AutoRestartOnCrash { get; set; } = true;
    public bool EnableBlackFrameDetection { get; set; } = true;
    public double BlackFrameBrightnessThreshold { get; set; } = 5.0;
    public string ProgressFilePath { get; set; } = "render_progress.txt";
    public RenderOutputType OutputType { get; set; } = RenderOutputType.ImageSequence;

    /// <summary>单帧模式下要渲染的帧号</summary>
    public int SingleFrameNumber { get; set; } = 1;

    public string[] BuildCommand(int startFrame, int endFrame)
    {
        var args = new List<string> { BlenderPath, "-b", BlendFilePath };

        if (!string.IsNullOrWhiteSpace(OutputPath))
            args.AddRange(["-o", OutputPath]);

        switch (OutputType)
        {
            case RenderOutputType.SingleFrame:
                args.AddRange(["-f", SingleFrameNumber.ToString()]);
                break;

            case RenderOutputType.Video:
                args.AddRange(["-F", "FFMPEG", "-s", startFrame.ToString(), "-e", endFrame.ToString(), "-a"]);
                break;

            default: // ImageSequence
                args.AddRange(["-s", startFrame.ToString(), "-e", endFrame.ToString(), "-a"]);
                break;
        }

        return args.ToArray();
    }

    /// <summary>
    /// 根据输出路径模式（含 # 占位符）查找磁盘上已存在的帧输出文件。
    /// Blender 将 ##### 替换为左补零的帧号，扩展名由场景输出格式决定。
    /// </summary>
    public static string? FindFrameFile(string outputPattern, int frame)
    {
        if (string.IsNullOrWhiteSpace(outputPattern)) return null;

        int hashStart = outputPattern.IndexOf('#');
        if (hashStart < 0) return null;

        int hashEnd = hashStart;
        while (hashEnd < outputPattern.Length && outputPattern[hashEnd] == '#') hashEnd++;
        int hashCount = hashEnd - hashStart;

        string frameStr = frame.ToString().PadLeft(hashCount, '0');
        string basePath = outputPattern[..hashStart] + frameStr + outputPattern[hashEnd..];

        string[] exts = [".png", ".jpg", ".jpeg", ".exr", ".tiff", ".tif", ".bmp", ".hdr"];
        foreach (var ext in exts)
        {
            string fullPath = basePath + ext;
            if (File.Exists(fullPath)) return fullPath;
        }

        return File.Exists(basePath) ? basePath : null;
    }
}
