using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using m_mslc_overlay.services;
using System;

namespace m_mslc_overlay.views
{
    public partial class WelcomeWindow : Window
    {
        private int _currentSlideIndex = 0;
        private readonly Border[] _dots;
        private readonly Grid[] _slides;

        private readonly IBrush _activeDotBrush = new SolidColorBrush(Color.Parse("#2563EB"));
        private readonly IBrush _inactiveDotBrush = new SolidColorBrush(Color.Parse("#D1D5DB"));

        public WelcomeWindow()
        {
            InitializeComponent();

            _dots = new[] { Dot0, Dot1, Dot2, Dot3 };
            _slides = new[] { Slide1, Slide2, Slide3, Slide4 };

            // Initialize Language Combo box state
            string currentLang = ConfigManager.Current.Language;
            if (currentLang.Equals("en-US", StringComparison.OrdinalIgnoreCase))
            {
                LanguageComboBox.SelectedIndex = 1;
            }
            else
            {
                LanguageComboBox.SelectedIndex = 0;
            }

            UpdateSlide(0);
        }

        private void UpdateSlide(int index)
        {
            if (index < 0 || index >= _slides.Length) return;

            _currentSlideIndex = index;

            // Show current slide, hide others
            for (int i = 0; i < _slides.Length; i++)
            {
                _slides[i].IsVisible = (i == _currentSlideIndex);
            }

            // Update dot indicator styles (● ○ ○ ○)
            for (int i = 0; i < _dots.Length; i++)
            {
                if (i == _currentSlideIndex)
                {
                    _dots[i].Width = 20;
                    _dots[i].Background = _activeDotBrush;
                }
                else
                {
                    _dots[i].Width = 8;
                    _dots[i].Background = _inactiveDotBrush;
                }
            }

            // Update Back button state
            BackButton.IsEnabled = (_currentSlideIndex > 0);

            // Update Next button label
            if (_currentSlideIndex == _slides.Length - 1)
            {
                NextButton.Content = LanguageManager.GetString("Welcome_GetStarted");
            }
            else
            {
                NextButton.Content = LanguageManager.GetString("Welcome_Next");
            }
        }

        private void BackButton_Click(object? sender, RoutedEventArgs e)
        {
            if (_currentSlideIndex > 0)
            {
                UpdateSlide(_currentSlideIndex - 1);
            }
        }

        private void NextButton_Click(object? sender, RoutedEventArgs e)
        {
            if (_currentSlideIndex < _slides.Length - 1)
            {
                UpdateSlide(_currentSlideIndex + 1);
            }
            else
            {
                CompleteOnboarding();
            }
        }

        private void SkipButton_Click(object? sender, RoutedEventArgs e)
        {
            CompleteOnboarding();
        }

        private void Dot_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Border border && border.Tag is string tagStr && int.TryParse(tagStr, out int targetIndex))
            {
                UpdateSlide(targetIndex);
            }
        }

        private void LanguageComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (LanguageComboBox == null) return;

            string selectedLang = LanguageComboBox.SelectedIndex == 1 ? "en-US" : "vi-VN";
            if (LanguageManager.CurrentLanguage != selectedLang)
            {
                LanguageManager.LoadLanguage(selectedLang);
                ConfigManager.Current.Language = selectedLang;
                UpdateSlide(_currentSlideIndex);
            }
        }

        private void CompleteOnboarding()
        {
            ConfigManager.Current.HasCompletedOnboarding = true;
            ConfigManager.Save();

            var mainWindow = new MainWindow();
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = mainWindow;
            }
            mainWindow.Show();
            this.Close();
        }
    }
}
