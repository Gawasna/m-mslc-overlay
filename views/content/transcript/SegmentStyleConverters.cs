using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using m_mslc_overlay.core;

namespace m_mslc_overlay.views.content.transcript
{
    // ─────────────────────────────────────────────────────────────────────────────
    // Converters used by PaperSheet.axaml to drive per-segment visual styling
    // based on SegmentSource (Machine vs Human).
    //
    // Machine → bold, orange left-border (#E87B35)
    // Human   → italic + underlined, blue left-border (#4A90D9)
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps SegmentSource → left-border IBrush.
    /// Machine = app Primary orange, Human = blue, Unknown = neutral border.
    /// </summary>
    public sealed class SegmentSourceToBorderConverter : IValueConverter
    {
        public static readonly SegmentSourceToBorderConverter Instance = new();

        private static readonly SolidColorBrush MachineBrush = new(Color.Parse("#E87B35")); // orange
        private static readonly SolidColorBrush HumanBrush   = new(Color.Parse("#4A90D9")); // blue
        private static readonly SolidColorBrush NeutralBrush = new(Color.Parse("#E5E5E5")); // inactive

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is SegmentSource src)
            {
                return src switch
                {
                    SegmentSource.Machine => MachineBrush,
                    SegmentSource.Human   => HumanBrush,
                    _                     => NeutralBrush
                };
            }
            return NeutralBrush;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Maps SegmentSource → FontWeight.
    /// Machine → Bold, Human → Normal.
    /// </summary>
    public sealed class SegmentSourceFontWeightConverter : IValueConverter
    {
        public static readonly SegmentSourceFontWeightConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is SegmentSource.Machine ? FontWeight.Bold : FontWeight.Normal;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Maps SegmentSource → FontStyle.
    /// Human → Italic, Machine → Normal.
    /// </summary>
    public sealed class SegmentSourceFontStyleConverter : IValueConverter
    {
        public static readonly SegmentSourceFontStyleConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is SegmentSource.Human ? FontStyle.Italic : FontStyle.Normal;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Maps SegmentSource → TextDecorations.
    /// Human → Underline, Machine → null (no decoration).
    /// </summary>
    public sealed class SegmentSourceUnderlineConverter : IValueConverter
    {
        public static readonly SegmentSourceUnderlineConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is SegmentSource.Human ? TextDecorations.Underline : null;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Maps SegmentSource → subtle background tint to reinforce the type grouping.
    /// Machine → very faint orange wash, Human → very faint blue wash, other → white.
    /// </summary>
    public sealed class SegmentSourceToBackgroundConverter : IValueConverter
    {
        public static readonly SegmentSourceToBackgroundConverter Instance = new();

        private static readonly SolidColorBrush MachineBg = new(Color.Parse("#FFF8F4")); // faint orange
        private static readonly SolidColorBrush HumanBg   = new(Color.Parse("#F4F8FF")); // faint blue
        private static readonly SolidColorBrush DefaultBg = new(Color.Parse("#FFFFFF")); // white

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is SegmentSource src)
            {
                return src switch
                {
                    SegmentSource.Machine => MachineBg,
                    SegmentSource.Human   => HumanBg,
                    _                     => DefaultBg
                };
            }
            return DefaultBg;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
