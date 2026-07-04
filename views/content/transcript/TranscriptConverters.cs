using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace m_mslc_overlay.views.content.transcript
{
    /// <summary>
    /// Converts bool (IsRecording) to a SolidColorBrush for recording dot fill.
    /// true  -> #EF5350 (red)
    /// false -> #9E9E9E (grey)
    /// </summary>
    public sealed class BoolToRecordingColorConverter : IValueConverter
    {
        public static readonly BoolToRecordingColorConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true
                ? new SolidColorBrush(Color.Parse("#EF5350"))
                : new SolidColorBrush(Color.Parse("#9E9E9E"));

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Converts MicLevel (0.0–1.0) to pixel width for the mic meter bar.
    /// ConverterParameter supplies the maximum width string (e.g., "80").
    /// </summary>
    public sealed class DoubleToMeterWidthConverter : IValueConverter
    {
        public static readonly DoubleToMeterWidthConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            double level = value is double d ? d : 0.0;
            double maxWidth = parameter is string s && double.TryParse(s, out double p) ? p : 80.0;
            return Math.Clamp(level * maxWidth, 0, maxWidth);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Converts bool (IsActive segment) to left-border IBrush.
    /// Active -> #3B82F6 (blue), Inactive -> #E5E7EB (light grey)
    /// </summary>
    public sealed class BoolToActiveBorderColorConverter : IValueConverter
    {
        public static readonly BoolToActiveBorderColorConverter Instance = new();

        // Active -> app Primary orange (#FF8400), Inactive -> light border
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true
                ? new SolidColorBrush(Color.Parse("#FF8400"))
                : new SolidColorBrush(Color.Parse("#E5E5E5"));

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Converts bool (IsActive segment) to segment background IBrush.
    /// Active -> #EFF6FF (blue tint), Inactive -> White
    /// </summary>
    public sealed class BoolToActiveBackgroundConverter : IValueConverter
    {
        public static readonly BoolToActiveBackgroundConverter Instance = new();

        // Active -> very light orange wash (#FFF4E6), Inactive -> white
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true
                ? new SolidColorBrush(Color.Parse("#FFF4E6"))
                : new SolidColorBrush(Color.Parse("#FFFFFF"));

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
