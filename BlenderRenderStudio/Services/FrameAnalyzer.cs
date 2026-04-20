using System;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace BlenderRenderStudio.Services;

/// <summary>
/// 对渲染输出图片做亮度分析，检测黑帧/问题帧。
/// 采用分步采样 + 标准差计算，兼顾性能和准确度。
/// </summary>
public static class FrameAnalyzer
{
    public record AnalysisResult(
        double AverageBrightness,
        double StdDevBrightness,
        bool IsBlackFrame,
        int Width,
        int Height);

    /// <summary>
    /// 分析图片亮度。
    /// </summary>
    /// <param name="imagePath">渲染输出图片绝对路径</param>
    /// <param name="brightnessThreshold">黑帧判定阈值（0~255），低于此值视为黑帧</param>
    /// <param name="maxSamples">最大采样像素数，0 = 全量</param>
    /// <summary>分析用缩放上限：480px 宽足够做亮度统计，4K→480 = 33MB→0.5MB</summary>
    private const uint AnalysisMaxWidth = 480;

    public static async Task<AnalysisResult?> AnalyzeAsync(
        string imagePath,
        double brightnessThreshold = 5.0,
        int maxSamples = 50000)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(imagePath);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);

            // 缩放解码：亮度分析不需要全分辨率，480px 宽足够
            // 4K(3840×2160) 全量解码 = 33MB byte[]，缩放到 480×270 仅 0.5MB
            var transform = new BitmapTransform();
            if (decoder.PixelWidth > AnalysisMaxWidth)
            {
                double scale = (double)AnalysisMaxWidth / decoder.PixelWidth;
                transform.ScaledWidth = AnalysisMaxWidth;
                transform.ScaledHeight = (uint)(decoder.PixelHeight * scale);
            }

            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            var pixels = pixelData.DetachPixelData();
            int scaledWidth = transform.ScaledWidth > 0 ? (int)transform.ScaledWidth : (int)decoder.PixelWidth;
            int scaledHeight = transform.ScaledHeight > 0 ? (int)transform.ScaledHeight : (int)decoder.PixelHeight;
            int totalPixels = scaledWidth * scaledHeight;
            int stride = 4; // BGRA8

            // 确定采样步长
            int step = maxSamples > 0 && totalPixels > maxSamples
                ? totalPixels / maxSamples
                : 1;

            double sum = 0;
            double sumSq = 0;
            int count = 0;

            for (int i = 0; i < totalPixels; i += step)
            {
                int offset = i * stride;
                if (offset + 2 >= pixels.Length) break;

                double b = pixels[offset];
                double g = pixels[offset + 1];
                double r = pixels[offset + 2];

                // ITU-R BT.601 亮度公式
                double lum = 0.299 * r + 0.587 * g + 0.114 * b;
                sum += lum;
                sumSq += lum * lum;
                count++;
            }

            if (count == 0) return null;

            double avg = sum / count;
            double variance = (sumSq / count) - (avg * avg);
            double stdDev = Math.Sqrt(Math.Max(0, variance));

            // 黑帧判定：平均亮度极低，且标准差也很小（排除暗场景中有高光点的情况）
            bool isBlack = avg < brightnessThreshold && stdDev < brightnessThreshold * 2;

            return new AnalysisResult(avg, stdDev, isBlack, (int)decoder.PixelWidth, (int)decoder.PixelHeight);
        }
        catch
        {
            return null;
        }
    }
}
