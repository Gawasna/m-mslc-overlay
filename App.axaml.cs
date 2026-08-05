using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace m_mslc_overlay
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            // Load configuration first
            m_mslc_overlay.services.ConfigManager.Load();

            // Load saved language
            string savedLang = m_mslc_overlay.services.ConfigManager.Current.Language;
            if (string.IsNullOrEmpty(savedLang)) savedLang = "vi-VN";
            m_mslc_overlay.services.LanguageManager.LoadLanguage(savedLang);

            // Apply saved theme (System | Light | Dark)
            if (!Enum.TryParse<m_mslc_overlay.services.ThemeMode>(
                    m_mslc_overlay.services.ConfigManager.Current.ThemeMode,
                    ignoreCase: true,
                    out var savedTheme))
            {
                savedTheme = m_mslc_overlay.services.ThemeMode.System;
            }
            m_mslc_overlay.services.ThemeManager.Instance.Initialize(savedTheme);

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Set to true to always show Welcome window during testing
                bool needsOnboarding = true;
                
                if (needsOnboarding)
                {
                    // Show Welcome / Onboarding Carousel window first
                    var welcomeWindow = new m_mslc_overlay.views.dialogs.WelcomeWindow();
                    desktop.MainWindow = welcomeWindow;
                    
                    // When Welcome window closes, open main window
                    welcomeWindow.Closed += (s, e) => {
                        var mainWin = new MainWindow();
                        desktop.MainWindow = mainWin;
                        mainWin.Show();
                    };
                }
                else
                {
                    // Already completed onboarding - go straight to main window
                    desktop.MainWindow = new MainWindow();
                }
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}