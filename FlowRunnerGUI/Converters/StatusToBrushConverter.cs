using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using FlowEngine;

namespace FlowRunnerGUI.Converters;

public sealed class StatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush SuccessBrush = new(Color.FromRgb(46, 125, 50));
    private static readonly SolidColorBrush FailedBrush = new(Color.FromRgb(198, 40, 40));
    private static readonly SolidColorBrush TimeoutBrush = new(Color.FromRgb(245, 124, 0));
    private static readonly SolidColorBrush SkippedBrush = new(Color.FromRgb(158, 158, 158));
    private static readonly SolidColorBrush DefaultBrush = new(Color.FromRgb(117, 117, 117));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is StepStatus status)
        {
            return status switch
            {
                StepStatus.Success => SuccessBrush,
                StepStatus.Failed => FailedBrush,
                StepStatus.Timeout => TimeoutBrush,
                StepStatus.Skipped => SkippedBrush,
                _ => DefaultBrush
            };
        }

        return DefaultBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush TrueBrush = new(Color.FromRgb(46, 125, 50));
    private static readonly SolidColorBrush FalseBrush = new(Color.FromRgb(198, 40, 40));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? TrueBrush : FalseBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is false;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is false;
}
