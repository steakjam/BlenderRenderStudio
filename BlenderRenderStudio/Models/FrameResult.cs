using System;
using BlenderRenderStudio.Helpers;
using Microsoft.UI.Xaml.Media;

namespace BlenderRenderStudio.Models;

public enum FrameStatus
{
    Pending,
    Rendering,
    Completed,
    BlackFrame,
    Error
}

public class FrameResult : ObservableObject
{
    private FrameStatus _status = FrameStatus.Pending;
    private double _brightness = -1;
    private string _outputPath = string.Empty;
    private string _errorMessage = string.Empty;
    private double _renderTimeSeconds;

    public int FrameNumber { get; init; }

    public FrameStatus Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public double Brightness
    {
        get => _brightness;
        set => SetProperty(ref _brightness, value);
    }

    public string OutputPath
    {
        get => _outputPath;
        set => SetProperty(ref _outputPath, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public double RenderTimeSeconds
    {
        get => _renderTimeSeconds;
        set => SetProperty(ref _renderTimeSeconds, value);
    }

    public string StatusIcon => Status switch
    {
        FrameStatus.Pending => "\uE768",     // Clock
        FrameStatus.Rendering => "\uE769",   // Processing
        FrameStatus.Completed => "\uE73E",   // Checkmark
        FrameStatus.BlackFrame => "\uE7BA",  // Warning
        FrameStatus.Error => "\uE783",       // Error
        _ => "\uE768"
    };

    public string BrightnessText => Brightness >= 0 ? $"{Brightness:F1}" : "-";
}

/// <summary>网格视图中的帧缩略图</summary>
public class FrameThumbnail : ObservableObject
{
    private ImageSource? _image;
    private FrameStatus _status;

    public int FrameNumber { get; init; }
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>预计算的磁盘缓存键（MD5），避免 LoadRange 时重复调用 GetCacheKey</summary>
    public string? CacheKey { get; set; }

    public FrameStatus Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// 缩略图图源（SoftwareBitmapSource）。
    /// 生命周期由 ThumbnailCache 统一管理（淘汰时 Dispose）。
    /// 此 setter 不自动 Dispose 旧值——避免缓存中仍活跃的 source 被误释放。
    /// </summary>
    public ImageSource? Image
    {
        get => _image;
        set => SetProperty(ref _image, value);
    }

    public string Label => $"#{FrameNumber}";
}
