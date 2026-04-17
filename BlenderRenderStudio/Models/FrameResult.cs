using BlenderRenderStudio.Helpers;

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
