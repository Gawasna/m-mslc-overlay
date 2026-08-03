using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using m_mslc_overlay.services;

namespace m_mslc_overlay.views.dialogs
{
    public partial class FirstRunWizardWindow : Window
    {
        private int _currentStep = 0;
        private const int TOTAL_STEPS = 6;
        
        // Environment check results
        private EnvironmentCheckResult? _envResult;
        
        // Plugin installation selections
        private bool _installAtom26 = false;
        private bool _installAtom32 = false;
        
        public FirstRunWizardWindow()
        {
            InitializeComponent();
            _ = InitializeWizardAsync();
        }
        
        private async Task InitializeWizardAsync()
        {
            // Run environment check immediately
            _envResult = await EnvironmentCheckerService.RunDiagnosticAsync();
            
            // Show first step
            ShowStep(0);
        }
        
        private void ShowStep(int stepIndex)
        {
            _currentStep = stepIndex;
            
            // Update progress indicator
            if (StepIndicator != null)
            {
                StepIndicator.Text = $"Bước {_currentStep + 1} / {TOTAL_STEPS}";
            }
            
            // Update navigation buttons
            if (BackBtn != null)
            {
                BackBtn.IsEnabled = _currentStep > 0;
            }
            
            if (NextBtn != null && FinishBtn != null)
            {
                bool isLastStep = _currentStep == TOTAL_STEPS - 1;
                NextBtn.IsVisible = !isLastStep;
                FinishBtn.IsVisible = isLastStep;
            }
            
            // Update header
            UpdateHeaderForStep(_currentStep);
            
            // Clear and build content for current step
            if (ContentPanel != null)
            {
                ContentPanel.Children.Clear();
                BuildStepContent(_currentStep);
            }
        }
        
        private void UpdateHeaderForStep(int step)
        {
            if (HeaderTitle == null || HeaderSubtitle == null) return;
            
            switch (step)
            {
                case 0: // Welcome
                    HeaderTitle.Text = "Chào mừng! 👋";
                    HeaderSubtitle.Text = "Cảm ơn bạn đã cài đặt m-mslc-overlay";
                    break;
                case 1: // Environment Check
                    HeaderTitle.Text = "Kiểm tra hệ thống";
                    HeaderSubtitle.Text = "Đang phân tích môi trường và phụ thuộc";
                    break;
                case 2: // Plugin Selection
                    HeaderTitle.Text = "Chọn tiện ích";
                    HeaderSubtitle.Text = "Chọn các module bạn muốn cài đặt";
                    break;
                case 3: // LiveCaptions Guidance
                    HeaderTitle.Text = "Thiết lập Live Captions";
                    HeaderSubtitle.Text = "Bật tính năng phụ đề trực tiếp của Windows";
                    break;
                case 4: // App Guide
                    HeaderTitle.Text = "Hướng dẫn sử dụng";
                    HeaderSubtitle.Text = "Tìm hiểu cách sử dụng ứng dụng";
                    break;
                case 5: // Engine Selection
                    HeaderTitle.Text = "Chọn engine dịch thuật";
                    HeaderSubtitle.Text = "Cấu hình engine để bắt đầu";
                    break;
            }
        }
        
        private void BuildStepContent(int step)
        {
            switch (step)
            {
                case 0:
                    BuildWelcomeStep();
                    break;
                case 1:
                    BuildEnvironmentCheckStep();
                    break;
                case 2:
                    BuildPluginSelectionStep();
                    break;
                case 3:
                    BuildLiveCaptionsGuidanceStep();
                    break;
                case 4:
                    BuildAppGuideStep();
                    break;
                case 5:
                    BuildEngineSelectionStep();
                    break;
            }
        }
        
        // Step 0: Welcome
        private void BuildWelcomeStep()
        {
            var panel = ContentPanel;
            if (panel == null) return;
            
            // Icon
            var icon = new TextBlock
            {
                Text = "🚀",
                FontSize = 64,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 0, 0, 24)
            };
            panel.Children.Add(icon);
            
            // Title
            var title = new TextBlock
            {
                Text = "m-mslc-overlay",
                FontSize = 28,
                FontWeight = FontWeight.Bold,
                Foreground = Brush.Parse("#111827"),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 0, 0, 12)
            };
            panel.Children.Add(title);
            
            // Description
            var desc = new TextBlock
            {
                Text = "Ứng dụng phiên dịch trực tiếp với AI, hỗ trợ dịch thuật đa ngôn ngữ và ghi chú workspace.",
                FontSize = 14,
                Foreground = Brush.Parse("#6B7280"),
                TextAlignment = Avalonia.Media.TextAlignment.Center,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                MaxWidth = 500,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 0, 0, 32)
            };
            panel.Children.Add(desc);
            
            // Features list
            var featuresStack = new StackPanel { Spacing = 16 };
            
            string[] features = {
                "✨ Dịch thuật thời gian thực từ Live Captions",
                "🤖 Hỗ trợ nhiều AI engine (Gemini, Ollama, Offline)",
                "📝 Workspace ghi chú và quản lý transcript",
                "🎯 Speaker Diarization - nhận diện người nói",
                "⚡ Hiệu năng cao, hỗ trợ CUDA acceleration"
            };
            
            foreach (var feature in features)
            {
                var featureText = new TextBlock
                {
                    Text = feature,
                    FontSize = 13,
                    Foreground = Brush.Parse("#374151"),
                    Margin = new Avalonia.Thickness(32, 0, 32, 0)
                };
                featuresStack.Children.Add(featureText);
            }
            
            panel.Children.Add(featuresStack);
        }
        
        // Step 1: Environment Check
        private void BuildEnvironmentCheckStep()
        {
            var panel = ContentPanel;
            if (panel == null || _envResult == null) return;
            
            var infoText = new TextBlock
            {
                Text = "Đang kiểm tra các phụ thuộc hệ thống cần thiết...",
                FontSize = 14,
                Foreground = Brush.Parse("#6B7280"),
                Margin = new Avalonia.Thickness(0, 0, 0, 20)
            };
            panel.Children.Add(infoText);
            
            // Python status
            AddStatusCard(panel, "Python Runtime",
                _envResult.HasPython ? $"✅ {_envResult.PythonVersion}" : "❌ Chưa cài đặt",
                _envResult.HasPython,
                _envResult.HasPython ? null : "Cần Python 3.10+ để chạy offline translation. Nhấn để tải.",
                _envResult.HasPython ? null : "https://www.python.org/downloads/");
            
            // LiveCaptions status
            AddStatusCard(panel, "Windows Live Captions",
                _envResult.HasLiveCaptionsBinary ? "✅ Đã cài đặt" : "⚠️ Chưa kích hoạt",
                _envResult.HasLiveCaptionsBinary,
                _envResult.HasLiveCaptionsBinary ? null : "Cần bật Live Captions trong Windows Settings để sử dụng app.");
            
            // CUDA status (optional)
            AddStatusCard(panel, "CUDA Acceleration (Tùy chọn)",
                _envResult.HasCuda ? $"✅ {_envResult.CudaVersion}" : "⚠️ CPU Mode",
                true, // Not critical
                _envResult.HasCuda ? null : "Không phát hiện GPU NVIDIA. App sẽ chạy ở chế độ CPU.");
        }
        
        private void AddStatusCard(StackPanel parent, string title, string status, bool isOk, 
                                   string? errorMsg = null, string? actionUrl = null)
        {
            var card = new Border
            {
                Background = Brush.Parse("#FFFFFF"),
                BorderBrush = Brush.Parse(isOk ? "#D1FAE5" : "#FEE2E2"),
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(8),
                Padding = new Avalonia.Thickness(16),
                Margin = new Avalonia.Thickness(0, 0, 0, 12)
            };
            
            var stack = new StackPanel { Spacing = 6 };
            
            var titleText = new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush.Parse("#111827")
            };
            stack.Children.Add(titleText);
            
            var statusText = new TextBlock
            {
                Text = status,
                FontSize = 12,
                Foreground = Brush.Parse(isOk ? "#059669" : "#DC2626")
            };
            stack.Children.Add(statusText);
            
            if (!string.IsNullOrEmpty(errorMsg))
            {
                var errorText = new TextBlock
                {
                    Text = errorMsg,
                    FontSize = 11,
                    Foreground = Brush.Parse("#6B7280"),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(0, 4, 0, 0)
                };
                stack.Children.Add(errorText);
                
                if (!string.IsNullOrEmpty(actionUrl))
                {
                    var linkBtn = new Button
                    {
                        Content = "Tải xuống Python",
                        Margin = new Avalonia.Thickness(0, 8, 0, 0),
                        Padding = new Avalonia.Thickness(12, 6),
                        Background = Brush.Parse("#3B82F6"),
                        Foreground = Brushes.White,
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
                    };
                    linkBtn.Click += (s, e) => {
                        try {
                            Process.Start(new ProcessStartInfo(actionUrl) { UseShellExecute = true });
                        } catch { }
                    };
                    stack.Children.Add(linkBtn);
                }
            }
            
            card.Child = stack;
            parent.Children.Add(card);
        }
        
        // Step 2: Plugin Selection
        private void BuildPluginSelectionStep()
        {
            var panel = ContentPanel;
            if (panel == null) return;
            
            var infoText = new TextBlock
            {
                Text = "Chọn các module tiện ích bạn muốn cài đặt. Bạn có thể cài sau trong Preferences.",
                FontSize = 13,
                Foreground = Brush.Parse("#6B7280"),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 0, 0, 20)
            };
            panel.Children.Add(infoText);
            
            // atom26 checkbox
            var atom26Check = new CheckBox
            {
                Content = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Offline Translation Server (atom26)",
                            FontSize = 14,
                            FontWeight = FontWeight.SemiBold
                        },
                        new TextBlock
                        {
                            Text = "Dịch thuật offline với CTranslate2 (NLLB-200). Yêu cầu Python.",
                            FontSize = 11,
                            Foreground = Brush.Parse("#6B7280"),
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap
                        }
                    }
                },
                IsChecked = _installAtom26,
                Margin = new Avalonia.Thickness(0, 0, 0, 16)
            };
            atom26Check.IsCheckedChanged += (s, e) => _installAtom26 = atom26Check.IsChecked ?? false;
            panel.Children.Add(atom26Check);
            
            // atom32 checkbox
            var atom32Check = new CheckBox
            {
                Content = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Speaker Diarization Engine (atom32)",
                            FontSize = 14,
                            FontWeight = FontWeight.SemiBold
                        },
                        new TextBlock
                        {
                            Text = "Nhận diện và phân biệt người nói trong phiên. Yêu cầu Python.",
                            FontSize = 11,
                            Foreground = Brush.Parse("#6B7280"),
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap
                        }
                    }
                },
                IsChecked = _installAtom32,
                Margin = new Avalonia.Thickness(0, 0, 0, 16)
            };
            atom32Check.IsCheckedChanged += (s, e) => _installAtom32 = atom32Check.IsChecked ?? false;
            panel.Children.Add(atom32Check);
            
            // Warning if no Python
            if (_envResult != null && !_envResult.HasPython)
            {
                var warningBox = new Border
                {
                    Background = Brush.Parse("#FEF3C7"),
                    BorderBrush = Brush.Parse("#F59E0B"),
                    BorderThickness = new Avalonia.Thickness(1),
                    CornerRadius = new Avalonia.CornerRadius(6),
                    Padding = new Avalonia.Thickness(12),
                    Margin = new Avalonia.Thickness(0, 16, 0, 0)
                };
                
                warningBox.Child = new TextBlock
                {
                    Text = "⚠️ Các plugin này yêu cầu Python. Vui lòng cài Python trước khi tiếp tục.",
                    FontSize = 12,
                    Foreground = Brush.Parse("#92400E"),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                };
                panel.Children.Add(warningBox);
            }
        }
        
        // Step 3: LiveCaptions Guidance
        private void BuildLiveCaptionsGuidanceStep()
        {
            var panel = ContentPanel;
            if (panel == null) return;
            
            var intro = new TextBlock
            {
                Text = "m-mslc-overlay cần Windows Live Captions để nhận phụ đề thời gian thực.",
                FontSize = 13,
                Foreground = Brush.Parse("#6B7280"),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 0, 0, 20)
            };
            panel.Children.Add(intro);
            
            // Steps to enable
            string[] steps = {
                "1. Mở Windows Settings (⊞ Win + I)",
                "2. Đi tới Accessibility → Captions",
                "3. Bật Live Captions",
                "4. Chọn ngôn ngữ English (United States)",
                "5. Khởi động Live Captions một lần để verify"
            };
            
            foreach (var step in steps)
            {
                var stepText = new TextBlock
                {
                    Text = step,
                    FontSize = 13,
                    Foreground = Brush.Parse("#374151"),
                    Margin = new Avalonia.Thickness(16, 0, 0, 12)
                };
                panel.Children.Add(stepText);
            }
            
            // Open Settings button
            var openSettingsBtn = new Button
            {
                Content = "🔧 Mở Windows Settings",
                Margin = new Avalonia.Thickness(0, 16, 0, 0),
                Padding = new Avalonia.Thickness(16, 10),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            };
            openSettingsBtn.Click += (s, e) => {
                try {
                    Process.Start(new ProcessStartInfo("ms-settings:easeofaccess-closedcaptioning") 
                        { UseShellExecute = true });
                } catch { }
            };
            panel.Children.Add(openSettingsBtn);
            
            // Note if already detected
            if (_envResult != null && _envResult.HasLiveCaptionsBinary)
            {
                var successNote = new Border
                {
                    Background = Brush.Parse("#ECFDF5"),
                    BorderBrush = Brush.Parse("#10B981"),
                    BorderThickness = new Avalonia.Thickness(1),
                    CornerRadius = new Avalonia.CornerRadius(6),
                    Padding = new Avalonia.Thickness(12),
                    Margin = new Avalonia.Thickness(0, 20, 0, 0)
                };
                successNote.Child = new TextBlock
                {
                    Text = "✅ Live Captions đã được phát hiện trên hệ thống của bạn!",
                    FontSize = 12,
                    Foreground = Brush.Parse("#047857"),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                };
                panel.Children.Add(successNote);
            }
        }
        
        // Step 4: App Guide
        private void BuildAppGuideStep()
        {
            var panel = ContentPanel;
            if (panel == null) return;
            
            var title = new TextBlock
            {
                Text = "Hướng dẫn nhanh",
                FontSize = 18,
                FontWeight = FontWeight.Bold,
                Foreground = Brush.Parse("#111827"),
                Margin = new Avalonia.Thickness(0, 0, 0, 16)
            };
            panel.Children.Add(title);
            
            // Guide cards
            AddGuideCard(panel, "1️⃣ Khởi động Live Captions",
                "Bật Live Captions (Ctrl+Win+L) và chọn audio source bạn muốn dịch.");
            
            AddGuideCard(panel, "2️⃣ Chọn Translation Engine",
                "Vào Preferences để chọn giữa Cloud AI (Gemini, Ollama) hoặc Offline CTranslate2.");
            
            AddGuideCard(panel, "3️⃣ Bắt đầu dịch",
                "Khi Live Captions bắt caption text, app sẽ tự động dịch và hiển thị overlay.");
            
            AddGuideCard(panel, "4️⃣ Workspace & Export",
                "Tạo workspace để ghi chú, export transcript sang SRT, TXT, hay DOCX.");
        }
        
        private void AddGuideCard(StackPanel parent, string title, string desc)
        {
            var card = new Border
            {
                Background = Brush.Parse("#F9FAFB"),
                BorderBrush = Brush.Parse("#E5E7EB"),
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(8),
                Padding = new Avalonia.Thickness(16),
                Margin = new Avalonia.Thickness(0, 0, 0, 12)
            };
            
            var stack = new StackPanel { Spacing = 6 };
            
            var titleText = new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush.Parse("#111827")
            };
            stack.Children.Add(titleText);
            
            var descText = new TextBlock
            {
                Text = desc,
                FontSize = 12,
                Foreground = Brush.Parse("#6B7280"),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };
            stack.Children.Add(descText);
            
            card.Child = stack;
            parent.Children.Add(card);
        }
        
        // Step 5: Engine Selection (Open Preferences)
        private void BuildEngineSelectionStep()
        {
            var panel = ContentPanel;
            if (panel == null) return;
            
            var intro = new TextBlock
            {
                Text = "Bước cuối cùng: Chọn translation engine trong Preferences.",
                FontSize = 14,
                Foreground = Brush.Parse("#374151"),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 0, 0, 20)
            };
            panel.Children.Add(intro);
            
            // Engine options explanation
            AddEngineOption(panel, "☁️ Cloud AI (Gemini, Ollama)",
                "Dịch qua API cloud. Yêu cầu API key và internet. Chất lượng cao.");
            
            AddEngineOption(panel, "💻 Offline CTranslate2",
                "Dịch offline với mô hình NLLB-200. Yêu cầu Python và plugin atom26.");
            
            AddEngineOption(panel, "🌐 DeepL API",
                "Dịch qua DeepL Free API. Yêu cầu API key. Chất lượng tốt nhất.");
            
            var openPrefBtn = new Button
            {
                Content = "⚙️ Mở Preferences",
                Margin = new Avalonia.Thickness(0, 24, 0, 0),
                Padding = new Avalonia.Thickness(20, 12),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                FontSize = 14
            };
            openPrefBtn.Click += async (s, e) => {
                await OpenPreferencesAsync();
            };
            panel.Children.Add(openPrefBtn);
        }
        
        private void AddEngineOption(StackPanel parent, string name, string desc)
        {
            var stack = new StackPanel
            {
                Spacing = 4,
                Margin = new Avalonia.Thickness(0, 0, 0, 16)
            };
            
            var nameText = new TextBlock
            {
                Text = name,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush.Parse("#111827")
            };
            stack.Children.Add(nameText);
            
            var descText = new TextBlock
            {
                Text = desc,
                FontSize = 12,
                Foreground = Brush.Parse("#6B7280"),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };
            stack.Children.Add(descText);
            
            parent.Children.Add(stack);
        }
        
        private async Task OpenPreferencesAsync()
        {
            var prefsDialog = new PreferencesDialog();
            await prefsDialog.ShowDialog(this);
        }
        
        // Navigation handlers
        private void BackBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (_currentStep > 0)
            {
                ShowStep(_currentStep - 1);
            }
        }
        
        private async void NextBtn_Click(object? sender, RoutedEventArgs e)
        {
            // Handle plugin installation on step 2
            if (_currentStep == 2)
            {
                if (_installAtom26 || _installAtom32)
                {
                    await InstallSelectedPluginsAsync();
                }
            }
            
            if (_currentStep < TOTAL_STEPS - 1)
            {
                ShowStep(_currentStep + 1);
            }
        }
        
        private void FinishBtn_Click(object? sender, RoutedEventArgs e)
        {
            // Mark first run as completed
            ConfigManager.Current.HasCompletedFirstRun = true;
            ConfigManager.Save();
            
            Close(true); // Return true to indicate completion
        }
        
        private async Task InstallSelectedPluginsAsync()
        {
            // Check if Python is available
            if (_envResult == null || !_envResult.HasPython)
            {
                var confirmDialog = new Window
                {
                    Width = 400,
                    Height = 200,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Title = "Python không khả dụng"
                };
                
                // Show warning - will implement proper dialog if needed
                LoggerService.Log("[FirstRunWizard] Cannot install plugins - Python not detected.");
                return;
            }
            
            // Install plugins via PluginManifestService
            if (_installAtom26)
            {
                LoggerService.Log("[FirstRunWizard] User requested atom26 installation.");
                // Will be installed in background
                _ = PluginManifestService.EnsureInstalledAsync("atom26",
                    msg => LoggerService.Log($"[atom26] {msg}"),
                    pct => { });
            }
            
            if (_installAtom32)
            {
                LoggerService.Log("[FirstRunWizard] User requested atom32 installation.");
                _ = PluginManifestService.EnsureInstalledAsync("atom32",
                    msg => LoggerService.Log($"[atom32] {msg}"),
                    pct => { });
            }
            
            await Task.Delay(500); // Brief pause for UI feedback
        }
    }
}
