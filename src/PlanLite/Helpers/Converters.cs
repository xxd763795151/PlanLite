using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PlanLite.Core.Models;

namespace PlanLite.Helpers;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        var invert = parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
        if (invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hasValue = value switch
        {
            null => false,
            string str => !string.IsNullOrWhiteSpace(str),
            int count => count > 0,
            _ => true
        };

        var invert = parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
        if (invert)
        {
            hasValue = !hasValue;
        }

        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class StatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is PlanTaskStatus status ? status.ToChineseLabel() : "未开始";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var resources = Application.Current?.Resources;
        if (value is not PlanTaskStatus status || resources is null)
        {
            return Brushes.Gray;
        }

        var key = status switch
        {
            PlanTaskStatus.Completed => "PlanSuccessBrush",
            PlanTaskStatus.InProgress => "PlanAccentBrush",
            _ => "PlanMutedTextBrush"
        };

        return resources[key] as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class StatusToBadgeBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var resources = Application.Current?.Resources;
        if (value is not PlanTaskStatus status || resources is null)
        {
            return Brushes.Transparent;
        }

        var key = status switch
        {
            PlanTaskStatus.Completed => "PlanSuccessSoftBrush",
            PlanTaskStatus.InProgress => "PlanAccentSoftBrush",
            _ => "PlanProgressTrackBrush"
        };

        return resources[key] as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class CompletedOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool isCompleted && isCompleted ? 0.64d : 1.0d;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class CompletedTextDecorationsConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool isCompleted && isCompleted ? TextDecorations.Strikethrough : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
        {
            return false;
        }

        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter is not null && targetType.IsEnum)
        {
            return Enum.Parse(targetType, parameter.ToString()!, ignoreCase: true);
        }

        return Binding.DoNothing;
    }
}
