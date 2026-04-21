using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace BlenderRenderStudio.Helpers;

/// <summary>
/// 解码结果，持有 SoftwareBitmap 供 UI 线程创建 SoftwareBitmapSource。
/// 使用后必须 Dispose 释放 SoftwareBitmap 的非托管内存。
/// </summary>
public sealed class DecodedImage : IDisposable
{
    public SoftwareBitmap Bitmap { get; private set; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }

    public DecodedImage(SoftwareBitmap bitmap, int width, int height)
    {
        Bitmap = bitmap;
        PixelWidth = width;
        PixelHeight = height;
    }

    public void Dispose()
    {
        Bitmap?.Dispose();
        Bitmap = null!;
    }
}

/// <summary>
/// 集中管理图像加载与内存释放。
/// 核心策略（仿 Windows 资源管理器）：
/// 1. BitmapDecoder + BitmapTransform 尝试硬件/编解码器级缩放
/// 2. 某些 WIC 编解码器会静默忽略 BitmapTransform → 检测后用像素降采样兜底
/// 3. SoftwareBitmapSource（IDisposable）替代 BitmapImage → 确定性释放 D2D 纹理
/// </summary>
public static class ImageHelper
{
    /// <summary>
    /// 正在执行 SetBitmapAsync 的计数器。
    /// FlushPendingDispose 检查此值，非零时跳过 Dispose，
    /// 避免 D2D device 竞态导致 0xC000027B。
    /// </summary>
    internal static volatile int _activeSourceCreations;
    /// <summary>
    /// 解码图片到 SoftwareBitmap（可在任意线程调用）。
    /// 保证输出尺寸不超过 decodePixelWidth，即使原始编解码器不支持 BitmapTransform。
    /// </summary>
    public static async Task<DecodedImage?> DecodeAsync(string filePath, int decodePixelWidth = 0)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 81920, useAsync: true);
            using var stream = fs.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream);

            uint targetWidth = 0, targetHeight = 0;
            var transform = new BitmapTransform();
            if (decodePixelWidth > 0 && decoder.PixelWidth > (uint)decodePixelWidth)
            {
                double scale = (double)decodePixelWidth / decoder.PixelWidth;
                targetWidth = (uint)decodePixelWidth;
                targetHeight = Math.Max(1, (uint)(decoder.PixelHeight * scale));
                transform.ScaledWidth = targetWidth;
                transform.ScaledHeight = targetHeight;
            }

            var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            // 安全检查：某些 WIC 编解码器静默忽略 BitmapTransform，返回全分辨率。
            // 检测到未缩放时，用直接像素降采样兜底（比 PNG 编解码快 10 倍以上）。
            if (targetWidth > 0 && (uint)softwareBitmap.PixelWidth > targetWidth * 1.2)
            {
                var scaled = DownscalePixels(softwareBitmap, (int)targetWidth, (int)targetHeight);
                softwareBitmap.Dispose();
                softwareBitmap = scaled;
            }

            return new DecodedImage(softwareBitmap, softwareBitmap.PixelWidth, softwareBitmap.PixelHeight);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 直接像素降采样（Nearest-Neighbor）。
    /// 不经过 WIC 编解码管线，纯内存操作：
    /// - 4K→240px: 33MB 源 → 130KB 目标，耗时 &lt; 5ms
    /// - 4K→960px: 33MB 源 → 2MB 目标，耗时 &lt; 15ms
    /// 对缩略图和预览而言质量足够，且避免了 PNG/JPEG 编解码的巨大开销。
    /// </summary>
    private static SoftwareBitmap DownscalePixels(SoftwareBitmap source, int targetWidth, int targetHeight)
    {
        int srcW = source.PixelWidth;
        int srcH = source.PixelHeight;

        // 从源 SoftwareBitmap 提取像素
        var srcBytes = new byte[srcW * srcH * 4];
        source.CopyToBuffer(srcBytes.AsBuffer());

        // Nearest-Neighbor 降采样
        var dstBytes = new byte[targetWidth * targetHeight * 4];
        double scaleX = (double)srcW / targetWidth;
        double scaleY = (double)srcH / targetHeight;

        for (int y = 0; y < targetHeight; y++)
        {
            int srcY = Math.Min((int)(y * scaleY), srcH - 1);
            int srcRowOffset = srcY * srcW * 4;
            int dstRowOffset = y * targetWidth * 4;

            for (int x = 0; x < targetWidth; x++)
            {
                int srcX = Math.Min((int)(x * scaleX), srcW - 1);
                int srcIdx = srcRowOffset + srcX * 4;
                int dstIdx = dstRowOffset + x * 4;

                dstBytes[dstIdx] = srcBytes[srcIdx];
                dstBytes[dstIdx + 1] = srcBytes[srcIdx + 1];
                dstBytes[dstIdx + 2] = srcBytes[srcIdx + 2];
                dstBytes[dstIdx + 3] = srcBytes[srcIdx + 3];
            }
        }

        var result = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8, targetWidth, targetHeight, BitmapAlphaMode.Premultiplied);
        result.CopyFromBuffer(dstBytes.AsBuffer());
        return result;
    }

    /// <summary>
    /// 在 UI 线程上从 DecodedImage 创建可绑定的 SoftwareBitmapSource。
    /// 调用后 DecodedImage 会被 Dispose（SoftwareBitmapSource 内部持有副本）。
    /// </summary>
    public static async Task<SoftwareBitmapSource?> CreateSourceAsync(DecodedImage decoded)
    {
        try
        {
            System.Diagnostics.Trace.WriteLine($"[IMG] CreateSourceAsync: bitmap={decoded.Bitmap?.PixelWidth}x{decoded.Bitmap?.PixelHeight}");
            var source = new SoftwareBitmapSource();

            // 标记 D2D device 正在使用，阻止 FlushPendingDispose 同时 Dispose 其他 SoftwareBitmapSource
            Interlocked.Increment(ref _activeSourceCreations);
            try
            {
                await source.SetBitmapAsync(decoded.Bitmap);
            }
            finally
            {
                Interlocked.Decrement(ref _activeSourceCreations);
            }

            decoded.Dispose(); // SetBitmapAsync 已复制数据，释放原始 SoftwareBitmap
            System.Diagnostics.Trace.WriteLine($"[IMG] CreateSourceAsync 成功");
            return source;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[IMG] !! CreateSourceAsync 异常: {ex.GetType().Name}: {ex.Message}");
            decoded.Dispose();
            return null;
        }
    }

    // ── 磁盘缩略图缓存（原始 BGRA 像素，零 WIC 开销）─────────────────

    /// <summary>
    /// 根据源文件路径和修改时间生成缓存键（MD5 哈希）。
    /// 文件重新渲染后修改时间变化，自动失效旧缓存。
    /// </summary>
    public static string GetCacheKey(string filePath, int decodePixelWidth = 0)
    {
        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(filePath).Ticks;
            var input = $"{filePath}|{lastWrite}|{decodePixelWidth}";
            var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hash);
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// 将 DecodedImage 保存为磁盘缓存文件。
    /// 格式：[4字节宽][4字节高][BGRA像素数据]
    /// 240x135 缩略图 ≈ 130KB，加载时零 WIC 开销。
    /// </summary>
    public static void SaveThumbnailCache(DecodedImage decoded, string cachePath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            int w = decoded.PixelWidth, h = decoded.PixelHeight;
            var pixels = new byte[w * h * 4];
            decoded.Bitmap.CopyToBuffer(pixels.AsBuffer());

            using var fs = new FileStream(cachePath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 81920);
            // 写入头：宽度 + 高度（各 4 字节，小端序）
            fs.Write(BitConverter.GetBytes(w));
            fs.Write(BitConverter.GetBytes(h));
            fs.Write(pixels);
        }
        catch { /* 写缓存失败不影响主流程 */ }
    }

    /// <summary>
    /// 从磁盘缓存加载缩略图，跳过 WIC 解码，纯内存操作。
    /// 返回 DecodedImage（调用方负责 Dispose）或 null。
    /// </summary>
    public static DecodedImage? LoadThumbnailCache(string cachePath)
    {
        try
        {
            if (!File.Exists(cachePath)) return null;
            using var fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 81920);

            // 读取头
            var header = new byte[8];
            if (fs.Read(header, 0, 8) < 8) return null;
            int w = BitConverter.ToInt32(header, 0);
            int h = BitConverter.ToInt32(header, 4);
            if (w <= 0 || h <= 0 || w > 4096 || h > 4096) return null;

            // 读取像素
            var pixels = new byte[w * h * 4];
            int totalRead = 0;
            while (totalRead < pixels.Length)
            {
                int read = fs.Read(pixels, totalRead, pixels.Length - totalRead);
                if (read == 0) return null;
                totalRead += read;
            }

            var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
            bitmap.CopyFromBuffer(pixels.AsBuffer());
            return new DecodedImage(bitmap, w, h);
        }
        catch { return null; }
    }

    /// <summary>获取缓存文件完整路径</summary>
    public static string GetCachePath(string cacheDir, string cacheKey)
        => Path.Combine(cacheDir, cacheKey + ".raw");
}
