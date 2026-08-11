using System;
using System.Collections.Generic;
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

    // ─────────────────────────────────────────────────────────────────────────────
    // MultiValue Converters for typography combining Source default + Global settings + Segment overrides
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Values: [0] = SegmentSource, [1] = IsBoldOverride (bool?), [2] = GlobalBold (bool)
    /// </summary>
    public sealed class SegmentMultiFontWeightConverter : IMultiValueConverter
    {
        public static readonly SegmentMultiFontWeightConverter Instance = new();

        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count >= 3 && values[0] is SegmentSource src)
            {
                bool? isOverride = values[1] as bool?;
                if (isOverride.HasValue) return isOverride.Value ? FontWeight.Bold : FontWeight.Normal;

                bool globalBold = values[2] as bool? ?? false;
                if (globalBold) return FontWeight.Bold;

                return src == SegmentSource.Machine ? FontWeight.Bold : FontWeight.Normal;
            }
            return FontWeight.Normal;
        }
    }

    /// <summary>
    /// Values: [0] = SegmentSource, [1] = IsItalicOverride (bool?), [2] = GlobalItalic (bool)
    /// </summary>
    public sealed class SegmentMultiFontStyleConverter : IMultiValueConverter
    {
        public static readonly SegmentMultiFontStyleConverter Instance = new();

        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count >= 3 && values[0] is SegmentSource src)
            {
                bool? isOverride = values[1] as bool?;
                if (isOverride.HasValue) return isOverride.Value ? FontStyle.Italic : FontStyle.Normal;

                bool globalItalic = values[2] as bool? ?? false;
                if (globalItalic) return FontStyle.Italic;

                return src == SegmentSource.Human ? FontStyle.Italic : FontStyle.Normal;
            }
            return FontStyle.Normal;
        }
    }

    /// <summary>
    /// Values: [0] = SegmentSource, [1] = IsUnderlineOverride (bool?), [2] = GlobalUnderline (bool)
    /// </summary>
    public sealed class SegmentMultiUnderlineConverter : IMultiValueConverter
    {
        public static readonly SegmentMultiUnderlineConverter Instance = new();

        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count >= 3 && values[0] is SegmentSource src)
            {
                bool? isOverride = values[1] as bool?;
                if (isOverride.HasValue) return isOverride.Value ? TextDecorations.Underline : null;

                bool globalUnderline = values[2] as bool? ?? false;
                if (globalUnderline) return TextDecorations.Underline;

                return src == SegmentSource.Human ? TextDecorations.Underline : null;
            }
            return null;
        }
    }

    /// <summary>
    /// Values: [0] = FontSizeOverride (double?), [1] = GlobalFontSize (double)
    /// </summary>
    public sealed class SegmentMultiFontSizeConverter : IMultiValueConverter
    {
        public static readonly SegmentMultiFontSizeConverter Instance = new();

        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count >= 2)
            {
                double? isOverride = values[0] as double?;
                if (isOverride.HasValue) return isOverride.Value;

                double globalSize = values[1] as double? ?? 11.5;
                return globalSize;
            }
            return 11.5;
        }
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
