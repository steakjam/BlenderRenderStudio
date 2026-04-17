using BlenderRenderStudio.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;

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
