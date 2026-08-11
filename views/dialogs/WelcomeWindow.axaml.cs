using System;
using System.Collections.Generic;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using Material.Icons.Avalonia;
using m_mslc_overlay.services;

namespace m_mslc_overlay.views.dialogs
{
    public partial class WelcomeWindow : Window
    {
        private int _currentSlideIndex = 0;
        private readonly List<Control> _slides = new();

        public WelcomeWindow()
        {
            InitializeComponent();
            RebuildUI();
        }

        private void RebuildUI()
        {
            BuildSlides();
            UpdateNavigationLabels();
            ShowSlide(_currentSlideIndex, isForward: true);
        }

        private void BuildSlides()
        {
            _slides.Clear();
            _slides.Add(CreateSlide1_Language());
            _slides.Add(CreateSlide2_Injection());
            _slides.Add(CreateSlide3_Overlay());
            _slides.Add(CreateSlide4_ControlCenter());
        }

        private string GetLang() => ConfigManager.Current.Language ?? "vi-VN";

        private void UpdateNavigationLabels()
        {
            string lang = GetLang();
            SkipTopBtn.Content = GetText("Skip", lang);
            SkipBtn.Content = GetText("SkipGuide", lang);
            BackBtn.Content = GetText("Back", lang);

            if (_currentSlideIndex == _slides.Count - 1)
            {
                NextBtn.Content = GetText("GetStarted", lang);
                NextBtn.Width = 150;
            }
            else
            {
                NextBtn.Content = GetText("Next", lang);
                NextBtn.Width = 120;
            }
        }

        #region Slide Creators

        // Slide 1: Audio & Language Setup
        private Control CreateSlide1_Language()
        {
            string lang = GetLang();
            var root = new StackPanel { Spacing = 20, VerticalAlignment = VerticalAlignment.Center };

            // Header Icon & Title
            var headerStack = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
            var iconBorder = new Border
            {
                Width = 64,
                Height = 64,
                CornerRadius = new Avalonia.CornerRadius(32),
                Background = new SolidColorBrush(Color.Parse("#E0F2FE")),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new MaterialIcon
                {
                    Kind = MaterialIconKind.Translate,
                    Width = 36,
                    Height = 36,
                    Foreground = new SolidColorBrush(Color.Parse("#0284C7"))
                }
            };

            var titleText = new TextBlock
            {
                Text = GetText("S1_Title", lang),
                FontSize = 22,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.Parse("#0F172A")),
                TextAlignment = TextAlignment.Center
            };

            var descText = new TextBlock
            {
                Text = GetText("S1_Desc", lang),
                FontSize = 14,
                FontWeight = FontWeight.Medium,
                Foreground = new SolidColorBrush(Color.Parse("#334155")),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 640
            };

            headerStack.Children.Add(iconBorder);
            headerStack.Children.Add(titleText);
            headerStack.Children.Add(descText);

            // Language Selector Card
            var langCard = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#F8FAFC")),
                CornerRadius = new Avalonia.CornerRadius(12),
                BorderBrush = new SolidColorBrush(Color.Parse("#CBD5E1")),
                BorderThickness = new Avalonia.Thickness(1.5),
                Padding = new Avalonia.Thickness(24, 18),
                HorizontalAlignment = HorizontalAlignment.Center,
                Width = 560
            };

            var langGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*, Auto") };

            var langLabelStack = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            langLabelStack.Children.Add(new TextBlock
            {
                Text = GetText("S1_SelectLabel", lang),
                FontWeight = FontWeight.Bold,
                FontSize = 15,
                Foreground = new SolidColorBrush(Color.Parse("#0F172A"))
            });
            langLabelStack.Children.Add(new TextBlock
            {
                Text = GetText("S1_SelectSub", lang),
                FontSize = 12,
                FontWeight = FontWeight.Medium,
                Foreground = new SolidColorBrush(Color.Parse("#475569"))
            });

            var langCombo = new ComboBox
            {
                Width = 200,
                Height = 38,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse("#0F172A")),
                FontWeight = FontWeight.Bold
            };

            var langItems = new List<ComboBoxItem>
            {
                new ComboBoxItem { Content = "🇻🇳 Tiếng Việt (vi-VN)", Tag = "vi-VN" },
                new ComboBoxItem { Content = "🇺🇸 English (en-US)", Tag = "en-US" },
                new ComboBoxItem { Content = "🇯🇵 日本語 (ja-JP)", Tag = "ja-JP" },
                new ComboBoxItem { Content = "🇨🇳 中文 (zh-CN)", Tag = "zh-CN" },
                new ComboBoxItem { Content = "🇰🇷 한국어 (ko-KR)", Tag = "ko-KR" }
            };

            int selectedIdx = 0;
            for (int i = 0; i < langItems.Count; i++)
            {
                if ((string)langItems[i].Tag! == lang) selectedIdx = i;
                langCombo.Items.Add(langItems[i]);
            }
            langCombo.SelectedIndex = selectedIdx;

            langCombo.SelectionChanged += (s, e) =>
            {
                if (langCombo.SelectedItem is ComboBoxItem selected && selected.Tag is string newLang)
                {
                    if (newLang != ConfigManager.Current.Language)
                    {
                        ConfigManager.Current.Language = newLang;
                        LanguageManager.LoadLanguage(newLang);
                        RebuildUI();
                    }
                }
            };

            Grid.SetColumn(langLabelStack, 0);
            Grid.SetColumn(langCombo, 1);
            langGrid.Children.Add(langLabelStack);
            langGrid.Children.Add(langCombo);
            langCard.Child = langGrid;

            // Badges
            var badgePanel = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 10, 0, 0)
            };

            badgePanel.Children.Add(CreateBadge(GetText("S1_Badge1", lang)));
            badgePanel.Children.Add(CreateBadge(GetText("S1_Badge2", lang)));

            root.Children.Add(headerStack);
            root.Children.Add(langCard);
            root.Children.Add(badgePanel);

            return root;
        }

        // Slide 2: C++ Engine Injection
        private Control CreateSlide2_Injection()
        {
            string lang = GetLang();
            var root = new StackPanel { Spacing = 18, VerticalAlignment = VerticalAlignment.Center };

            var headerStack = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
            var iconBorder = new Border
            {
                Width = 64,
                Height = 64,
                CornerRadius = new Avalonia.CornerRadius(32),
                Background = new SolidColorBrush(Color.Parse("#FEF2F2")),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new MaterialIcon
                {
                    Kind = MaterialIconKind.Flash,
                    Width = 36,
                    Height = 36,
                    Foreground = new SolidColorBrush(Color.Parse("#DC2626"))
                }
            };

            var titleText = new TextBlock
            {
                Text = GetText("S2_Title", lang),
                FontSize = 22,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.Parse("#0F172A")),
                TextAlignment = TextAlignment.Center
            };

            var descText = new TextBlock
            {
                Text = GetText("S2_Desc", lang),
                FontSize = 14,
                FontWeight = FontWeight.Medium,
                Foreground = new SolidColorBrush(Color.Parse("#334155")),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 640
            };

            headerStack.Children.Add(iconBorder);
            headerStack.Children.Add(titleText);
            headerStack.Children.Add(descText);

            var cardsPanel = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, Width = 600 };

            cardsPanel.Children.Add(CreateFeatureRow(MaterialIconKind.ShieldCheck, GetText("S2_Item1Title", lang), GetText("S2_Item1Desc", lang)));
            cardsPanel.Children.Add(CreateFeatureRow(MaterialIconKind.Speedometer, GetText("S2_Item2Title", lang), GetText("S2_Item2Desc", lang)));
            cardsPanel.Children.Add(CreateFeatureRow(MaterialIconKind.Autorenew, GetText("S2_Item3Title", lang), GetText("S2_Item3Desc", lang)));

            root.Children.Add(headerStack);
            root.Children.Add(cardsPanel);

            return root;
        }

        // Slide 3: Floating Caption Overlay
        private Control CreateSlide3_Overlay()
        {
            string lang = GetLang();
            var root = new StackPanel { Spacing = 18, VerticalAlignment = VerticalAlignment.Center };

            var headerStack = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
            var iconBorder = new Border
            {
                Width = 64,
                Height = 64,
                CornerRadius = new Avalonia.CornerRadius(32),
                Background = new SolidColorBrush(Color.Parse("#F0FDF4")),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new MaterialIcon
                {
                    Kind = MaterialIconKind.WindowRestore,
                    Width = 36,
                    Height = 36,
                    Foreground = new SolidColorBrush(Color.Parse("#16A34A"))
                }
            };

            var titleText = new TextBlock
            {
                Text = GetText("S3_Title", lang),
                FontSize = 22,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.Parse("#0F172A")),
                TextAlignment = TextAlignment.Center
            };

            var descText = new TextBlock
            {
                Text = GetText("S3_Desc", lang),
                FontSize = 14,
                FontWeight = FontWeight.Medium,
                Foreground = new SolidColorBrush(Color.Parse("#334155")),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 640
            };

            headerStack.Children.Add(iconBorder);
            headerStack.Children.Add(titleText);
            headerStack.Children.Add(descText);

            var tipsGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*, *, *"), Width = 660, HorizontalAlignment = HorizontalAlignment.Center };

            var tip1 = CreateTipCard(MaterialIconKind.GestureSwipe, GetText("S3_Tip1Title", lang), GetText("S3_Tip1Desc", lang));
            var tip2 = CreateTipCard(MaterialIconKind.Resize, GetText("S3_Tip2Title", lang), GetText("S3_Tip2Desc", lang));
            var tip3 = CreateTipCard(MaterialIconKind.Cog, GetText("S3_Tip3Title", lang), GetText("S3_Tip3Desc", lang));

            Grid.SetColumn(tip1, 0);
            Grid.SetColumn(tip2, 1);
            Grid.SetColumn(tip3, 2);

            tipsGrid.Children.Add(tip1);
            tipsGrid.Children.Add(tip2);
            tipsGrid.Children.Add(tip3);

            root.Children.Add(headerStack);
            root.Children.Add(tipsGrid);

            return root;
        }

        // Slide 4: Control Center & Hotkeys
        private Control CreateSlide4_ControlCenter()
        {
            string lang = GetLang();
            var root = new StackPanel { Spacing = 18, VerticalAlignment = VerticalAlignment.Center };

            var headerStack = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
            var iconBorder = new Border
            {
                Width = 64,
                Height = 64,
                CornerRadius = new Avalonia.CornerRadius(32),
                Background = new SolidColorBrush(Color.Parse("#FEF9C3")),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new MaterialIcon
                {
                    Kind = MaterialIconKind.RocketLaunch,
                    Width = 36,
                    Height = 36,
                    Foreground = new SolidColorBrush(Color.Parse("#CA8A04"))
                }
            };

            var titleText = new TextBlock
            {
                Text = GetText("S4_Title", lang),
                FontSize = 22,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.Parse("#0F172A")),
                TextAlignment = TextAlignment.Center
            };

            var descText = new TextBlock
            {
                Text = GetText("S4_Desc", lang),
                FontSize = 14,
                FontWeight = FontWeight.Medium,
                Foreground = new SolidColorBrush(Color.Parse("#334155")),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 640
            };

            headerStack.Children.Add(iconBorder);
            headerStack.Children.Add(titleText);
            headerStack.Children.Add(descText);

            var hotkeyCard = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#F8FAFC")),
                CornerRadius = new Avalonia.CornerRadius(12),
                BorderBrush = new SolidColorBrush(Color.Parse("#CBD5E1")),
                BorderThickness = new Avalonia.Thickness(1.5),
                Padding = new Avalonia.Thickness(24, 16),
                HorizontalAlignment = HorizontalAlignment.Center,
                Width = 600
            };

            var hotkeyStack = new StackPanel { Spacing = 10 };
            hotkeyStack.Children.Add(CreateHotkeyRow("F1 / Alt+Shift+X", GetText("HK_F1", lang)));
            hotkeyStack.Children.Add(CreateHotkeyRow("F2", GetText("HK_F2", lang)));
            hotkeyStack.Children.Add(CreateHotkeyRow("F3", GetText("HK_F3", lang)));
            hotkeyStack.Children.Add(CreateHotkeyRow("F4", GetText("HK_F4", lang)));
            hotkeyStack.Children.Add(CreateHotkeyRow("F5", GetText("HK_F5", lang)));

            hotkeyCard.Child = hotkeyStack;

            root.Children.Add(headerStack);
            root.Children.Add(hotkeyCard);

            return root;
        }

        #endregion

        #region Helper Component Builders

        private Border CreateBadge(string text)
        {
            return new Border
            {
                Background = new SolidColorBrush(Color.Parse("#E2E8F0")),
                BorderBrush = new SolidColorBrush(Color.Parse("#CBD5E1")),
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(16),
                Padding = new Avalonia.Thickness(14, 6),
                Margin = new Avalonia.Thickness(4),
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.Parse("#0F172A")),
                    FontWeight = FontWeight.Bold
                }
            };
        }

        private Border CreateFeatureRow(MaterialIconKind icon, string title, string description)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#F8FAFC")),
                CornerRadius = new Avalonia.CornerRadius(10),
                BorderBrush = new SolidColorBrush(Color.Parse("#CBD5E1")),
                BorderThickness = new Avalonia.Thickness(1),
                Padding = new Avalonia.Thickness(16, 12)
            };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto, *") };
            var iconBox = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new Avalonia.CornerRadius(10),
                Background = new SolidColorBrush(Color.Parse("#E0F2FE")),
                Margin = new Avalonia.Thickness(0, 0, 14, 0),
                Child = new MaterialIcon
                {
                    Kind = icon,
                    Width = 22,
                    Height = 22,
                    Foreground = new SolidColorBrush(Color.Parse("#0284C7"))
                }
            };

            var textStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.Bold, FontSize = 14, Foreground = new SolidColorBrush(Color.Parse("#0F172A")) });
            textStack.Children.Add(new TextBlock { Text = description, FontSize = 12, FontWeight = FontWeight.Medium, Foreground = new SolidColorBrush(Color.Parse("#334155")), TextWrapping = TextWrapping.Wrap });

            Grid.SetColumn(iconBox, 0);
            Grid.SetColumn(textStack, 1);
            grid.Children.Add(iconBox);
            grid.Children.Add(textStack);

            card.Child = grid;
            return card;
        }

        private Border CreateTipCard(MaterialIconKind icon, string title, string description)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#F8FAFC")),
                CornerRadius = new Avalonia.CornerRadius(12),
                BorderBrush = new SolidColorBrush(Color.Parse("#CBD5E1")),
                BorderThickness = new Avalonia.Thickness(1),
                Padding = new Avalonia.Thickness(14, 18),
                Margin = new Avalonia.Thickness(6)
            };

            var stack = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
            var iconBox = new MaterialIcon
            {
                Kind = icon,
                Width = 30,
                Height = 30,
                Foreground = new SolidColorBrush(Color.Parse("#0284C7")),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var titleBlock = new TextBlock { Text = title, FontWeight = FontWeight.Bold, FontSize = 14, TextAlignment = TextAlignment.Center, Foreground = new SolidColorBrush(Color.Parse("#0F172A")) };
            var descBlock = new TextBlock { Text = description, FontSize = 12, FontWeight = FontWeight.Medium, TextAlignment = TextAlignment.Center, Foreground = new SolidColorBrush(Color.Parse("#334155")), TextWrapping = TextWrapping.Wrap };

            stack.Children.Add(iconBox);
            stack.Children.Add(titleBlock);
            stack.Children.Add(descBlock);

            card.Child = stack;
            return card;
        }

        private Grid CreateHotkeyRow(string key, string description)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("180, *") };

            var keyBadge = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#E2E8F0")),
                BorderBrush = new SolidColorBrush(Color.Parse("#94A3B8")),
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(6),
                Padding = new Avalonia.Thickness(10, 5),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = key,
                    FontWeight = FontWeight.Bold,
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.Parse("#0F172A"))
                }
            };

            var descText = new TextBlock
            {
                Text = description,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse("#0F172A"))
            };

            Grid.SetColumn(keyBadge, 0);
            Grid.SetColumn(descText, 1);
            grid.Children.Add(keyBadge);
            grid.Children.Add(descText);

            return grid;
        }

        #endregion

        #region Multilingual Dictionary

        private string GetText(string key, string lang)
        {
            return (lang) switch
            {
                "en-US" => GetEnText(key),
                "ja-JP" => GetJaText(key),
                "zh-CN" => GetZhText(key),
                "ko-KR" => GetKoText(key),
                _ => GetViText(key)
            };
        }

        private string GetViText(string key) => key switch
        {
            "Skip" => "Bỏ qua",
            "SkipGuide" => "Bỏ qua hướng dẫn",
            "Back" => "◀ Quay lại",
            "Next" => "Tiếp theo ▶",
            "GetStarted" => "Bắt đầu ngay 🚀",
            "S1_Title" => "Chào mừng bạn đến với MSLC Subtitle Engine",
            "S1_Desc" => "Hệ thống trích xuất phụ đề thời gian thực và tự động dịch thuật thông minh bằng trí tuệ nhân tạo (AI).",
            "S1_SelectLabel" => "Ngôn ngữ hiển thị mặc định",
            "S1_SelectSub" => "Tùy chỉnh giao diện và phụ đề hiển thị",
            "S1_Badge1" => "⚡ Trích xuất âm thanh trực tiếp từ hệ thống",
            "S1_Badge2" => "🤖 Hỗ trợ AI (Ollama, Gemini, DeepL)",
            "S2_Title" => "Tự động Inject & Hook Tiến trình C++ Native",
            "S2_Desc" => "Bộ engine C++ (`Host.exe` & `Agent.dll`) tự động kết nối và hook vào Windows Live Caption để nhận diện câu nói với độ trễ cực thấp.",
            "S2_Item1Title" => "Quyền Quản trị viên (Admin UAC)",
            "S2_Item1Desc" => "Chạy ứng dụng dưới quyền Administrator để nạp DLL IPC Pipe vào tiến trình hệ thống.",
            "S2_Item2Title" => "Siêu tốc độ & Tối ưu",
            "S2_Item2Desc" => "Giao tiếp bộ nhớ chia sẻ IPC đạt độ trễ dưới 1 mili-giây, không tiêu tốn tài nguyên.",
            "S2_Item3Title" => "Tự động khôi phục kết nối",
            "S2_Item3Desc" => "Tự động phát hiện và kết nối lại ngay nếu Windows Live Caption được mở lại.",
            "S3_Title" => "Phụ đề Nổi Overlay Tự do Tùy chỉnh",
            "S3_Desc" => "Khung phụ đề nổi trong suốt hiển thị mượt mà trên mọi màn hình ứng dụng, phim ảnh và trò chơi.",
            "S3_Tip1Title" => "Kéo & Thả",
            "S3_Tip1Desc" => "Nhấp giữ chuột trái vào khung phụ đề để di chuyển tới vị trí bất kỳ.",
            "S3_Tip2Title" => "Co giãn kích thước",
            "S3_Tip2Desc" => "Thay đổi cỡ khung hình linh hoạt qua viền cửa sổ phụ đề.",
            "S3_Tip3Title" => "Menu Nhanh",
            "S3_Tip3Desc" => "Nhấp chuột phải vào Overlay để chỉnh cỡ chữ, màu nền và độ mờ.",
            "S4_Title" => "Trung tâm Điều khiển & Phím tắt Nhanh",
            "S4_Desc" => "Làm chủ toàn bộ hệ thống dịch phụ đề thời gian thực bằng bộ phím tắt tiện lợi.",
            "HK_F1" => "Ẩn / Hiện Phụ đề Nổi Overlay",
            "HK_F2" => "Tạm dừng / Tiếp tục trích xuất âm thanh",
            "HK_F3" => "Khóa / Mở khóa vị trí khung Phụ đề Nổi",
            "HK_F4" => "Chuyển nhanh Ngôn ngữ Dịch AI",
            "HK_F5" => "Mở bảng Cài đặt hệ thống (Preferences)",
            _ => key
        };

        private string GetEnText(string key) => key switch
        {
            "Skip" => "Skip",
            "SkipGuide" => "Skip Guide",
            "Back" => "◀ Back",
            "Next" => "Next ▶",
            "GetStarted" => "Get Started 🚀",
            "S1_Title" => "Welcome to MSLC Subtitle Engine",
            "S1_Desc" => "Real-time subtitle extraction and intelligent AI translation system.",
            "S1_SelectLabel" => "Default Display Language",
            "S1_SelectSub" => "Customize UI and subtitle language",
            "S1_Badge1" => "⚡ Direct system audio extraction",
            "S1_Badge2" => "🤖 AI Support (Ollama, Gemini, DeepL)",
            "S2_Title" => "Automatic C++ Native Engine Injection",
            "S2_Desc" => "Native C++ engine (`Host.exe` & `Agent.dll`) automatically connects and hooks into Windows Live Caption for low-latency recognition.",
            "S2_Item1Title" => "Administrator Privileges (Admin UAC)",
            "S2_Item1Desc" => "Run application as Administrator to load DLL IPC Pipe into system process.",
            "S2_Item2Title" => "Ultra Fast & Optimized",
            "S2_Item2Desc" => "IPC shared memory communication achieves sub-millisecond latency with zero overhead.",
            "S2_Item3Title" => "Auto Connection Recovery",
            "S2_Item3Desc" => "Automatically detects and reconnects whenever Windows Live Caption restarts.",
            "S3_Title" => "Customizable Floating Caption Overlay",
            "S3_Desc" => "Transparent floating overlay displaying smoothly over any application, movie, or game.",
            "S3_Tip1Title" => "Drag & Drop",
            "S3_Tip1Desc" => "Click and hold left mouse button to move overlay anywhere.",
            "S3_Tip2Title" => "Resize Freely",
            "S3_Tip2Desc" => "Resize caption frame easily via window borders.",
            "S3_Tip3Title" => "Quick Menu",
            "S3_Tip3Desc" => "Right-click overlay to customize font size, background color, and opacity.",
            "S4_Title" => "Control Center & Quick Hotkeys",
            "S4_Desc" => "Master the entire real-time subtitle translation system with convenient hotkeys.",
            "HK_F1" => "Toggle Floating Caption Overlay",
            "HK_F2" => "Pause / Resume audio extraction",
            "HK_F3" => "Lock / Unlock Overlay position",
            "HK_F4" => "Quick Switch AI Target Language",
            "HK_F5" => "Open System Preferences",
            _ => key
        };

        private string GetJaText(string key) => key switch
        {
            "Skip" => "スキップ",
            "SkipGuide" => "ガイドをスキップ",
            "Back" => "◀ 戻る",
            "Next" => "次へ ▶",
            "GetStarted" => "今すぐ始める 🚀",
            "S1_Title" => "MSLC Subtitle Engineへようこそ",
            "S1_Desc" => "リアルタイム字幕抽出およびインテリジェントAI翻訳システム。",
            "S1_SelectLabel" => "デフォルト表示言語",
            "S1_SelectSub" => "UIと字幕の表示言語をカスタマイズ",
            "S1_Badge1" => "⚡ システム音声の直接抽出",
            "S1_Badge2" => "🤖 AIサポート (Ollama, Gemini, DeepL)",
            "S2_Title" => "C++ Native Engineの自動注入",
            "S2_Desc" => "ネイティブC++エンジン（Host.exe & Agent.dll）がWindows Live Captionに自動接続します。",
            "S2_Item1Title" => "管理者権限 (Admin UAC)",
            "S2_Item1Desc" => "システムプロセスにDLL IPC Pipeをロードするため管理者権限で実行します。",
            "S2_Item2Title" => "超高速＆最適化",
            "S2_Item2Desc" => "IPC共有メモリ通信によりミリ秒未満のレイテンシを実現。",
            "S2_Item3Title" => "自動接続復旧",
            "S2_Item3Desc" => "Windows Live Captionが再起動されると自動で再接続します。",
            "S3_Title" => "カスタマイズ可能なオーバーレイ字幕",
            "S3_Desc" => "あらゆるアプリやゲームの上に滑らかに表示される透明オーバーレイ。",
            "S3_Tip1Title" => "ドラッグ＆ドロップ",
            "S3_Tip1Desc" => "左クリック長押しで字幕ウィンドウを自由に移動。",
            "S3_Tip2Title" => "サイズ変更",
            "S3_Tip2Desc" => "ウィンドウ枠をドラッグしてサイズを自由に変更。",
            "S3_Tip3Title" => "クイックメニュー",
            "S3_Tip3Desc" => "右クリックでフォントサイズ、背景色、透明度を調整。",
            "S4_Title" => "コントロールセンターとショートカットキー",
            "S4_Desc" => "便利なショートカットキーでリアルタイム字幕翻訳をマスターしましょう。",
            "HK_F1" => "オーバーレイ字幕の表示/非表示",
            "HK_F2" => "音声抽出の一時停止 / 再開",
            "HK_F3" => "オーバーレイ位置の固定 / 解除",
            "HK_F4" => "AI翻訳言語のクイック切り替え",
            "HK_F5" => "システム設定を開く",
            _ => key
        };

        private string GetZhText(string key) => key switch
        {
            "Skip" => "跳过",
            "SkipGuide" => "跳过指引",
            "Back" => "◀ 返回",
            "Next" => "下一步 ▶",
            "GetStarted" => "立即开始 🚀",
            "S1_Title" => "欢迎使用 MSLC Subtitle Engine",
            "S1_Desc" => "实时字幕提取与智能 AI 翻译系统。",
            "S1_SelectLabel" => "默认显示语言",
            "S1_SelectSub" => "自定义界面与字幕显示语言",
            "S1_Badge1" => "⚡ 实时系统音频提取",
            "S1_Badge2" => "🤖 支持 AI 引擎 (Ollama, Gemini, DeepL)",
            "S2_Title" => "自动 C++ 原生引擎注入",
            "S2_Desc" => "Native C++ 引擎（Host.exe 与 Agent.dll）自动挂钩 Windows Live Caption。",
            "S2_Item1Title" => "管理员权限 (Admin UAC)",
            "S2_Item1Desc" => "以管理员身份运行应用以将 DLL 注入系统进程。",
            "S2_Item2Title" => "极速与性能优化",
            "S2_Item2Desc" => "IPC 共享内存通信延迟低于 1 毫秒，零资源浪费。",
            "S2_Item3Title" => "自动恢复连接",
            "S2_Item3Desc" => "当 Windows Live Caption 重启时自动重新连接。",
            "S3_Title" => "自由自定义悬浮字幕 Overlay",
            "S3_Desc" => "透明悬浮字幕框，可在任何应用程序和游戏上方流畅显示。",
            "S3_Tip1Title" => "拖拽移动",
            "S3_Tip1Desc" => "按住鼠标左键可将字幕框移动至任意位置。",
            "S3_Tip2Title" => "调整大小",
            "S3_Tip2Desc" => "拖动窗口边缘轻松调整字幕框尺寸。",
            "S3_Tip3Title" => "快捷菜单",
            "S3_Tip3Desc" => "右键点击 Overlay 自定义字号、背景色及不透明度。",
            "S4_Title" => "控制中心与快捷键",
            "S4_Desc" => "通过便捷的快捷键轻松掌控实时字幕翻译系统。",
            "HK_F1" => "显示 / 隐藏悬浮字幕 Overlay",
            "HK_F2" => "暂停 / 恢复音频提取",
            "HK_F3" => "锁定 / 解锁 Overlay 位置",
            "HK_F4" => "快速切换 AI 目标语言",
            "HK_F5" => "打开系统首选项 (Preferences)",
            _ => key
        };

        private string GetKoText(string key) => key switch
        {
            "Skip" => "건너뛰기",
            "SkipGuide" => "가이드 건너뛰기",
            "Back" => "◀ 이전",
            "Next" => "다음 ▶",
            "GetStarted" => "지금 시작하기 🚀",
            "S1_Title" => "MSLC Subtitle Engine에 오신 것을 환영합니다",
            "S1_Desc" => "실시간 자막 추출 및 지능형 AI 번역 시스템입니다.",
            "S1_SelectLabel" => "기본 표시 언어",
            "S1_SelectSub" => "UI 및 자막 언어 설정",
            "S1_Badge1" => "⚡ 실시간 시스템 오디오 추출",
            "S1_Badge2" => "🤖 AI 지원 (Ollama, Gemini, DeepL)",
            "S2_Title" => "C++ 네이티브 엔진 자동 인젝션",
            "S2_Desc" => "네이티브 C++ 엔진(Host.exe 및 Agent.dll)이 Windows Live Caption에 자동으로 연결됩니다.",
            "S2_Item1Title" => "관리자 권한 (Admin UAC)",
            "S2_Item1Desc" => "DLL IPC Pipe를 시스템 프로세스에 로드하려면 관리자 권한으로 실행하세요.",
            "S2_Item2Title" => "초고속 & 최적화",
            "S2_Item2Desc" => "IPC 공유 메모리 통신으로 1밀리초 미만의 지연 시간을 달성합니다.",
            "S2_Item3Title" => "자동 연결 복구",
            "S2_Item3Desc" => "Windows Live Caption이 재시작되면 자동으로 재연결합니다.",
            "S3_Title" => "자유로운 커스텀 플로팅 자막 오버레이",
            "S3_Desc" => "모든 애플리케이션 및 게임 위에 매끄럽게 표시되는 투명 오버레이.",
            "S3_Tip1Title" => "드래그 & 드롭",
            "S3_Tip1Desc" => "마우스 왼쪽 버튼을 누른 채 자막 창을 어디로든 이동하세요.",
            "S3_Tip2Title" => "자유로운 크기 조절",
            "S3_Tip2Desc" => "창 테두리를 드래그하여 크기를 조절하세요.",
            "S3_Tip3Title" => "퀵 메뉴",
            "S3_Tip3Desc" => "우클릭으로 폰트 크기, 배경색 및 투명도를 설정하세요.",
            "S4_Title" => "컨트롤 센터 및 단축키",
            "S4_Desc" => "유용한 단축키로 실시간 자막 번역 시스템을 제어하세요.",
            "HK_F1" => "플로팅 자막 오버레이 표시 / 숨기기",
            "HK_F2" => "오디오 추출 일시정지 / 재개",
            "HK_F3" => "오버레이 위치 잠금 / 해제",
            "HK_F4" => "AI 번역 언어 빠른 전환",
            "HK_F5" => "시스템 환경설정 열기",
            _ => key
        };

        #endregion

        #region Navigation Logic

        private void ShowSlide(int index, bool isForward)
        {
            if (index < 0 || index >= _slides.Count) return;

            _currentSlideIndex = index;

            var transition = new PageSlide(TimeSpan.FromMilliseconds(250), PageSlide.SlideAxis.Horizontal);
            SlideTransitionContainer.PageTransition = transition;
            SlideTransitionContainer.Content = _slides[_currentSlideIndex];

            BackBtn.IsEnabled = (_currentSlideIndex > 0);
            UpdateNavigationLabels();
            UpdateDots(index);
        }

        private void UpdateDots(int activeIndex)
        {
            var dots = new[] { Dot0, Dot1, Dot2, Dot3 };
            for (int i = 0; i < dots.Length; i++)
            {
                if (i == activeIndex)
                {
                    if (!dots[i].Classes.Contains("Active")) dots[i].Classes.Add("Active");
                }
                else
                {
                    dots[i].Classes.Remove("Active");
                }
            }
        }

        private void BackBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (_currentSlideIndex > 0)
            {
                ShowSlide(_currentSlideIndex - 1, isForward: false);
            }
        }

        private void NextBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (_currentSlideIndex < _slides.Count - 1)
            {
                ShowSlide(_currentSlideIndex + 1, isForward: true);
            }
            else
            {
                CompleteOnboarding();
            }
        }

        private void SkipBtn_Click(object? sender, RoutedEventArgs e)
        {
            CompleteOnboarding();
        }

        private void Dot_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Border dot && dot.Tag is string tagStr && int.TryParse(tagStr, out int targetIdx))
            {
                bool isForward = targetIdx >= _currentSlideIndex;
                ShowSlide(targetIdx, isForward);
            }
        }

        private void CompleteOnboarding()
        {
            ConfigManager.Current.HasCompletedOnboarding = true;
            ConfigManager.Save();
            Close();
        }

        #endregion
    }
}
