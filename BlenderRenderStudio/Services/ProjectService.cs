using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using BlenderRenderStudio.Models;

namespace BlenderRenderStudio.Services;

/// <summary>
/// 项目持久化服务。JSON 存储，每个项目独立缓存目录。
/// </summary>
public static class ProjectService
{
    private static readonly string _projectsFile = Path.Combine(SettingsService.StorageDir, "projects.json");
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static List<RenderProject>? _cache;

    /// <summary>加载所有项目（带内存缓存 + 旧格式迁移）</summary>
    public static List<RenderProject> LoadAll()
    {
        if (_cache != null) return _cache;

        try
        {
            if (File.Exists(_projectsFile))
            {
                var json = File.ReadAllText(_projectsFile);
                _cache = JsonSerializer.Deserialize<List<RenderProject>>(json, _jsonOpts) ?? [];
            }
        }
        catch { /* ignore */ }

        _cache ??= [];

        // 旧数据迁移：OutputDirectory 含 # 的模式路径拆分为目录 + 前缀
        MigrateOutputPaths(_cache);

        return _cache;
    }

    /// <summary>保存所有项目到磁盘</summary>
    public static void SaveAll()
    {
        try
        {
            var dir = Path.GetDirectoryName(_projectsFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_cache ?? [], _jsonOpts);
            File.WriteAllText(_projectsFile, json);
        }
        catch { /* ignore */ }
    }

    /// <summary>创建新项目</summary>
    public static RenderProject Create(string name, string blendFilePath)
    {
        var projects = LoadAll();

        // 从全局设置继承 BlenderPath
        var settings = SettingsService.Load();

        var project = new RenderProject
        {
            Name = name,
            BlendFilePath = blendFilePath,
            BlenderPath = settings.BlenderPath,
            OutputDirectory = GetDefaultOutputDir(blendFilePath),
        };

        // 创建项目缓存目录
        Directory.CreateDirectory(project.CacheDirectory);
        Directory.CreateDirectory(project.ThumbnailCacheDir);

        projects.Insert(0, project);
        SaveAll();
        return project;
    }

    /// <summary>删除项目及其缓存</summary>
    public static void Delete(string projectId)
    {
        var projects = LoadAll();
        var project = projects.FirstOrDefault(p => p.Id == projectId);
        if (project == null) return;

        projects.Remove(project);
        SaveAll();

        // 清理项目缓存目录
        try
        {
            if (Directory.Exists(project.CacheDirectory))
                Directory.Delete(project.CacheDirectory, recursive: true);
        }
        catch { /* ignore */ }
    }

    /// <summary>更新项目并保存</summary>
    public static void Update(RenderProject project)
    {
        var projects = LoadAll();
        var idx = projects.FindIndex(p => p.Id == project.Id);
        if (idx >= 0)
            projects[idx] = project;
        else
            projects.Insert(0, project);
        SaveAll();
    }

    /// <summary>按 ID 查找项目</summary>
    public static RenderProject? GetById(string id)
        => LoadAll().FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// 应用启动时调用：将闪退残留的 Rendering/Queued 状态重置为 Idle，
    /// 并根据磁盘上实际输出文件刷新 CompletedFrames。
    /// </summary>
    public static void ResetStaleRenderingStatus()
    {
        var projects = LoadAll();
        bool changed = false;
        foreach (var p in projects)
        {
            if (p.Status is ProjectStatus.Rendering or ProjectStatus.Queued)
            {
                p.Status = ProjectStatus.Idle;

                // 根据磁盘文件刷新已完成帧数
                if (!string.IsNullOrEmpty(p.OutputDirectory) && Directory.Exists(p.OutputDirectory))
                {
                    var pattern = p.OutputPattern;
                    int start = p.OutputType == 2 ? p.SingleFrameNumber : p.StartFrame;
                    int end = p.OutputType == 2 ? p.SingleFrameNumber : p.EndFrame;
                    int found = 0;
                    for (int i = start; i <= end; i++)
                    {
                        if (RenderConfig.FindFrameFile(pattern, i) != null)
                            found++;
                    }
                    p.CompletedFrames = found;
                }

                changed = true;
            }
        }
        if (changed) SaveAll();
    }

    /// <summary>清除内存缓存，强制下次从磁盘重新加载</summary>
    public static void InvalidateCache() => _cache = null;

    /// <summary>
    /// 导出项目为 .brsproj 归档（ZIP 格式）。
    /// 包含 project.json + 可选的 progress.txt 和缩略图缓存。
    /// </summary>
    public static void Export(string projectId, string outputPath, bool includeProgress)
    {
        var project = GetById(projectId);
        if (project == null) return;

        if (File.Exists(outputPath)) File.Delete(outputPath);

        using var zip = ZipFile.Open(outputPath, ZipArchiveMode.Create);

        // 写入项目 JSON
        var json = JsonSerializer.Serialize(project, _jsonOpts);
        var entry = zip.CreateEntry("project.json");
        using (var stream = entry.Open())
        using (var writer = new StreamWriter(stream))
            writer.Write(json);

        if (includeProgress)
        {
            // 进度文件
            if (File.Exists(project.ProgressFilePath))
            {
                zip.CreateEntryFromFile(project.ProgressFilePath, "cache/progress.txt");
            }

            // 缩略图缓存
            if (Directory.Exists(project.ThumbnailCacheDir))
            {
                foreach (var file in Directory.GetFiles(project.ThumbnailCacheDir, "*.raw"))
                {
                    zip.CreateEntryFromFile(file, "cache/thumbnails/" + Path.GetFileName(file));
                }
            }
        }
    }

    /// <summary>
    /// 从 .brsproj 归档导入项目。
    /// 返回导入后的项目对象；失败返回 null。
    /// </summary>
    public static RenderProject? Import(string archivePath)
    {
        if (!File.Exists(archivePath)) return null;

        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            var projectEntry = zip.GetEntry("project.json");
            if (projectEntry == null) return null;

            RenderProject? project;
            using (var stream = projectEntry.Open())
            using (var reader = new StreamReader(stream))
            {
                var json = reader.ReadToEnd();
                project = JsonSerializer.Deserialize<RenderProject>(json, _jsonOpts);
            }
            if (project == null) return null;

            // 分配新 ID 避免冲突
            project.Id = Guid.NewGuid().ToString("N");
            project.Status = ProjectStatus.Idle;

            // 创建缓存目录
            Directory.CreateDirectory(project.CacheDirectory);
            Directory.CreateDirectory(project.ThumbnailCacheDir);

            // 还原进度文件
            var progressEntry = zip.GetEntry("cache/progress.txt");
            if (progressEntry != null)
            {
                progressEntry.ExtractToFile(project.ProgressFilePath, overwrite: true);
            }

            // 还原缩略图缓存
            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.StartsWith("cache/thumbnails/")) continue;
                if (entry.Length == 0) continue;
                var destPath = Path.Combine(project.ThumbnailCacheDir, entry.Name);
                entry.ExtractToFile(destPath, overwrite: true);
            }

            // 添加到项目列表
            var projects = LoadAll();
            projects.Insert(0, project);
            SaveAll();
            return project;
        }
        catch
        {
            return null;
        }
    }

    private static string GetDefaultOutputDir(string blendFilePath)
    {
        if (string.IsNullOrEmpty(blendFilePath)) return string.Empty;
        var dir = Path.GetDirectoryName(blendFilePath);
        return !string.IsNullOrEmpty(dir) ? Path.Combine(dir, "Render") : string.Empty;
    }

    /// <summary>
    /// 旧数据迁移：OutputDirectory 含 # 占位符时，拆分为纯目录 + 前缀。
    /// 例如 "D:\project\render\frame_#####" → OutputDirectory="D:\project\render", OutputPrefix="frame_"
    /// </summary>
    private static void MigrateOutputPaths(List<RenderProject> projects)
    {
        bool migrated = false;
        foreach (var p in projects)
        {
            int hashIdx = p.OutputDirectory.IndexOf('#');
            if (hashIdx < 0) continue;

            // 取 # 前面的部分，拆分为目录和前缀
            string beforeHash = p.OutputDirectory[..hashIdx];
            string dir = Path.GetDirectoryName(beforeHash) ?? string.Empty;
            string prefix = Path.GetFileName(beforeHash); // 如 "frame_"

            p.OutputDirectory = dir;
            if (!string.IsNullOrEmpty(prefix))
                p.OutputPrefix = prefix;
            migrated = true;
        }
        if (migrated) SaveAll();
    }
}
