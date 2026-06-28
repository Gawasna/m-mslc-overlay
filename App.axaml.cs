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

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindow();
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}