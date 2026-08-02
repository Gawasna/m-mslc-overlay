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
                // FL1: Check if first run
                bool isFirstRun = !m_mslc_overlay.services.ConfigManager.Current.HasCompletedFirstRun;
                
                if (isFirstRun && !m_mslc_overlay.services.AppPathHelper.IsDevMode)
                {
                    // Production mode first run - show wizard
                    var wizardWindow = new m_mslc_overlay.views.dialogs.FirstRunWizardWindow();
                    desktop.MainWindow = wizardWindow;
                    
                    // When wizard closes, open main window
                    wizardWindow.Closed += (s, e) => {
                        desktop.MainWindow = new MainWindow();
                        desktop.MainWindow.Show();
                    };
                }
                else
                {
                    // Dev mode or already completed first run - go straight to main window
                    desktop.MainWindow = new MainWindow();
                }
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}