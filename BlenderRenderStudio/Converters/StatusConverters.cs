using System;
using BlenderRenderStudio.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace BlenderRenderStudio.Converters;

public class FrameStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is FrameStatus status)
        {
            return status switch
            {
                FrameStatus.Pending => new SolidColorBrush(Colors.Gray),
                FrameStatus.Rendering => new SolidColorBrush(Colors.DodgerBlue),
                FrameStatus.Completed => new SolidColorBrush(Colors.LimeGreen),
                FrameStatus.BlackFrame => new SolidColorBrush(Colors.Orange),
                FrameStatus.Error => new SolidColorBrush(Colors.Red),
                _ => new SolidColorBrush(Colors.Gray),
            };
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>网格卡片背景色：BlackFrame→黄色半透明，Error→红色半透明，其他→默认卡片色</summary>
public class FrameStatusToCardBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is FrameStatus status)
        {
            return status switch
            {
                FrameStatus.BlackFrame => new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 165, 0)),
                FrameStatus.Error => new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 0, 0)),
                _ => new SolidColorBrush(Colors.Transparent),
            };
        }
        return new SolidColorBrush(Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>未渲染帧淡化：Pending→0.4，其他→1.0</summary>
public class FrameStatusToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is FrameStatus status)
            return status == FrameStatus.Pending ? 0.4 : 1.0;
        return 1.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>渲染中帧显示 ProgressRing</summary>
public class FrameStatusToRingVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is FrameStatus status)
            return status == FrameStatus.Rendering ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

public class BoolToInverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is bool b && !b;
}

public class PercentToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is double pct && parameter is string maxStr && double.TryParse(maxStr, out double max))
            return pct / 100.0 * max;
        return 0.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}

/// <summary>DeviceStatus → 圆点颜色</summary>
public class DeviceStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is Models.DeviceStatus status)
        {
            return status switch
            {
                Models.DeviceStatus.Online => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LimeGreen),
                Models.DeviceStatus.Busy => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange),
                Models.DeviceStatus.Error => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red),
                _ => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
            };
        }
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
