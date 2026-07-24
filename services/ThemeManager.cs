using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace m_mslc_overlay.services
{
    // ── ThemeMode ────────────────────────────────────────────────────────────
    public enum ThemeMode
    {
        System,
        Light,
        Dark
    }

    // ── ThemeManager ─────────────────────────────────────────────────────────
    // Singleton. Call ThemeManager.Instance.Apply(mode) from anywhere.
    // Listens to OS dark-mode changes when mode == System.
    public sealed class ThemeManager : INotifyPropertyChanged, IDisposable
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        public static ThemeManager Instance { get; } = new ThemeManager();
        private ThemeManager() { }

        // ── State ─────────────────────────────────────────────────────────────
        private ThemeMode _mode = ThemeMode.System;
        private bool _isDark = false;
        private IDisposable? _osWatcher;

        public ThemeMode Mode
        {
            get => _mode;
            private set { _mode = value; OnPropertyChanged(); }
        }

        public bool IsDark
        {
            get => _isDark;
            private set { _isDark = value; OnPropertyChanged(); }
        }

        // ── Init ──────────────────────────────────────────────────────────────
        // Call once from App.OnFrameworkInitializationCompleted AFTER window exists.
        public void Initialize(ThemeMode savedMode)
        {
            // Subscribe to OS theme changes
            _osWatcher = PlatformSettings.Subscribe(OnOsThemeChanged);
            Apply(savedMode);
        }

        // ── Public API ────────────────────────────────────────────────────────
        public void Apply(ThemeMode mode)
        {
            Mode = mode;

            bool dark = mode switch
            {
                ThemeMode.Dark   => true,
                ThemeMode.Light  => false,
                ThemeMode.System => IsOsDark(),
                _                => false
            };

            ApplyVariant(dark);

            // Persist
            if (ConfigManager.Current.ThemeMode != mode.ToString())
            {
                ConfigManager.Current.ThemeMode = mode.ToString();
                ConfigManager.Save();
            }
        }

        // ── Internals ─────────────────────────────────────────────────────────
        private static bool IsOsDark()
        {
            // Avalonia exposes this via Application.PlatformSettings
            var settings = Application.Current?.PlatformSettings;
            if (settings == null) return false;
            return settings.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;
        }

        private void OnOsThemeChanged(PlatformColorValues values)
        {
            if (_mode != ThemeMode.System) return;
            Dispatcher.UIThread.Post(() => ApplyVariant(values.ThemeVariant == PlatformThemeVariant.Dark));
        }

        private void ApplyVariant(bool dark)
        {
            IsDark = dark;
            var app = Application.Current;
            if (app == null) return;

            app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            UpdateCustomTokens(dark);
        }

        // Override our custom SolidColorBrush tokens in Application.Resources
        // to match the active variant. This is needed because Fluent handles its
        // own internal tokens, but our semantic tokens in Colors.axaml are static
        // by default — we swap them here at runtime.
        private static void UpdateCustomTokens(bool dark)
        {
            var res = Application.Current?.Resources;
            if (res == null) return;

            if (dark)
            {
                Set(res, "BgWindowBrush",          "#1C1C1E");
                Set(res, "BgCardBrush",            "#2C2C2E");
                Set(res, "BorderBrush",            "#444446");
                Set(res, "BorderLightBrush",       "#38383A");
                Set(res, "TextPrimaryBrush",       "#F2F2F7");
                Set(res, "TextSecondaryBrush",     "#AEAEB2");
                Set(res, "SecondaryBrush",         "#3A3A3C");
                Set(res, "SecondaryHoverBrush",    "#48484A");
                Set(res, "SecondaryPressedBrush",  "#58585A");
                Set(res, "AccentHoverBrush",       "#2C2C2E");
                Set(res, "StatusInactiveBrush",    "#48484A");

                // Transcript tokens — dark variants (blue accent)
                Set(res, "TranscriptActiveRowBrush",       "#0F2942");
                Set(res, "TranscriptPendingBrush",         "#0B1E33");
                Set(res, "TranscriptTranslationTextBrush", "#60A5FA");
                Set(res, "TranscriptTranslationBgBrush",   "#0F2942");
                Set(res, "TranscriptLiveCursorRowBrush",   "#0F2942");
                Set(res, "StatusBarBgBrush",               "#0C0C0E");
                Set(res, "StatusBarFgBrush",               "#AEAEB2");

                // TitleBar button hover dark
                Set(res, "TitleBarBtnHoverBrush",  "#3A3A3C");
            }
            else
            {
                Set(res, "BgWindowBrush",          "#F3F3F3");
                Set(res, "BgCardBrush",            "#FFFFFF");
                Set(res, "BorderBrush",            "#CBCCC9");
                Set(res, "BorderLightBrush",       "#E5E5E5");
                Set(res, "TextPrimaryBrush",       "#111111");
                Set(res, "TextSecondaryBrush",     "#666666");
                Set(res, "SecondaryBrush",         "#E7E8E5");
                Set(res, "SecondaryHoverBrush",    "#D7D8D5");
                Set(res, "SecondaryPressedBrush",  "#C7C8C5");
                Set(res, "AccentHoverBrush",       "#F2F3F0");
                Set(res, "StatusInactiveBrush",    "#CBCCC9");

                // Transcript tokens — light variants (blue accent)
                Set(res, "TranscriptActiveRowBrush",       "#EFF6FF");
                Set(res, "TranscriptPendingBrush",         "#F0F7FF");
                Set(res, "TranscriptTranslationTextBrush", "#1D4ED8");
                Set(res, "TranscriptTranslationBgBrush",   "#EFF6FF");
                Set(res, "TranscriptLiveCursorRowBrush",   "#EFF6FF");
                Set(res, "StatusBarBgBrush",               "#1C1C1E");
                Set(res, "StatusBarFgBrush",               "#E5E5E5");

                Set(res, "TitleBarBtnHoverBrush",  "#E5E5E5");
            }
        }

        private static void Set(IResourceDictionary res, string key, string hex)
        {
            var color = Color.Parse(hex);
            if (res.TryGetResource(key, null, out var existing) && existing is SolidColorBrush brush)
            {
                brush.Color = color;
            }
            else
            {
                res[key] = new SolidColorBrush(color);
            }
        }

        // ── IDisposable ───────────────────────────────────────────────────────
        public void Dispose()
        {
            _osWatcher?.Dispose();
        }

        // ── INotifyPropertyChanged ────────────────────────────────────────────
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ── PlatformSettings subscription helper ─────────────────────────────────
    // Wraps IPlatformSettings.ColorValuesChanged to IDisposable pattern.
    file sealed class PlatformSettings
    {
        public static IDisposable Subscribe(Action<PlatformColorValues> callback)
        {
            var settings = Application.Current?.PlatformSettings;
            if (settings == null) return Disposable.Empty;

            void Handler(object? _, PlatformColorValues v) => callback(v);
            settings.ColorValuesChanged += Handler;
            return new ActionDisposable(() => settings.ColorValuesChanged -= Handler);
        }
    }

    file sealed class ActionDisposable(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    file static class Disposable
    {
        public static readonly IDisposable Empty = new ActionDisposable(() => { });
    }
}
