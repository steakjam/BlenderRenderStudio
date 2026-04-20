using System;
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

    /// <summary>是否跳过磁盘上已存在的帧文件（仅续渲时启用）</summary>
    public bool SkipExistingFrames { get; set; }

<<<<<<< HEAD
    /// <summary>所属项目 ID（用于 PID 恢复跟踪）</summary>
    public string? ProjectId { get; set; }

=======
>>>>>>> 24b10e2407b584065c0922a9cd8684aebb0d1adc
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
    /// 根据输出路径模式查找磁盘上已存在的帧输出文件。
    /// 支持两种模式：
    /// 1. 含 # 占位符（如 frame_#####）→ 替换为左补零帧号
    /// 2. 无 # 的目录/前缀路径 → 用 Blender 默认命名（4位补零）查找
    /// </summary>
    public static string? FindFrameFile(string outputPattern, int frame)
    {
        if (string.IsNullOrWhiteSpace(outputPattern)) return null;

        string[] exts = [".png", ".jpg", ".jpeg", ".exr", ".tiff", ".tif", ".bmp", ".hdr"];

        int hashStart = outputPattern.IndexOf('#');
        if (hashStart >= 0)
        {
            // 模式1：含 # 占位符
            int hashEnd = hashStart;
            while (hashEnd < outputPattern.Length && outputPattern[hashEnd] == '#') hashEnd++;
            int hashCount = hashEnd - hashStart;

            string frameStr = frame.ToString().PadLeft(hashCount, '0');
            string basePath = outputPattern[..hashStart] + frameStr + outputPattern[hashEnd..];

            foreach (var ext in exts)
            {
                string fullPath = basePath + ext;
                if (File.Exists(fullPath)) return fullPath;
            }

            return File.Exists(basePath) ? basePath : null;
        }
        else
        {
            // 模式2：无 # 占位符，Blender 默认在路径末尾追加帧号
            // 确保路径以目录分隔符结尾（如果是目录路径）
            string prefix = outputPattern;
            if (Directory.Exists(prefix) && !prefix.EndsWith('\\') && !prefix.EndsWith('/'))
                prefix += Path.DirectorySeparatorChar;

            // 尝试常见的补零宽度（Blender 默认4位，也试其他宽度）
            foreach (int pad in new[] { 4, 1, 2, 3, 5, 6 })
            {
                string frameStr = frame.ToString().PadLeft(pad, '0');
                string basePath = prefix + frameStr;
                foreach (var ext in exts)
                {
                    if (File.Exists(basePath + ext)) return basePath + ext;
                }
                if (File.Exists(basePath)) return basePath;
            }

            return null;
        }
    }
}
