using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Editing;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using MMslcOverlay.Views.Controls;

namespace m_mslc_overlay.views.dialogs
{
    public partial class PaperSheetDemoWindow : Window
    {
        private DispatcherTimer? _sttTimer;
        private int _magicCaretOffset = 0;
        private int _sentenceIndex = 0;
        
        // Mock data that simulates F18 Workspace (audio timestamp, original text, translation)
        private string[] _mockData = new[] {
            "00:00:15|SPK_1|This is an AI generated transcript.",
            "00:00:18|SPK_1|It uses the magic cursor to insert text.",
            "00:00:22|SPK_2|You can edit anywhere in this document.",
            "00:00:25|SPK_2|The UI is rendered using AvaloniaEdit core."
        };

        public PaperSheetDemoWindow()
        {
            InitializeComponent();
            
            // F16.2: Enable editing
            Editor.IsReadOnly = false;
            
            // Initialize with some content
            string initText = "\nUser Notes:\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n";
            Editor.Document.Text = initText;
            _magicCaretOffset = 0; // Magic cursor starts at top
            
            // Register Custom Margin for Timestamps and Speakers (Replicating the Gutter in original concept)
            Editor.TextArea.LeftMargins.Add(new TranscriptGutterMargin(Editor.Document));

            // Register Custom Colorizer for formatting
            Editor.TextArea.TextView.LineTransformers.Add(new TranscriptColorizer());
        }

        private void SimulateSttBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_sttTimer == null)
            {
                _sttTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                _sttTimer.Tick += OnSttTick;
                _sttTimer.Start();
                StatusText.Text = "Magic Cursor: Active";
            }
            else
            {
                _sttTimer.Stop();
                _sttTimer = null;
                StatusText.Text = "Magic Cursor: Stopped";
            }
        }

        private void OnSttTick(object? sender, EventArgs e)
        {
            if (_sentenceIndex >= _mockData.Length)
            {
                _sentenceIndex = 0; // Loop
            }
            
            string rawData = _mockData[_sentenceIndex];
            _sentenceIndex++;
            
            var parts = rawData.Split('|');
            string ts = parts[0];
            string spk = parts[1];
            string text = parts[2];

            // In AvaloniaEdit, we can encode metadata inside the text invisibly, or rely on line numbers.
            // For demo, we just inject text formatted in a specific way that our Colorizer and Margin can parse.
            string insertString = $"[{ts}] [{spk}] {text}\n  ↳ [Bản dịch tạm thời đang chờ xử lý...]\n\n";
            
            // Magic Cursor (F16.3): Insert without disrupting the user's primary Caret
            int userCaret = Editor.CaretOffset;
            
            Editor.Document.Insert(_magicCaretOffset, insertString);
            _magicCaretOffset += insertString.Length;
            
            // Note: userCaret is automatically updated by AvaloniaEdit's Document model (Rope)!
        }

        private void Paper_PointerPressed(object sender, Avalonia.Input.PointerPressedEventArgs e)
        {
            Editor.Focus();
        }
    }

    // --- CUSTOM UI EXTENSIONS FOR AVALONIAEDIT ---

    // Removed TranscriptGutterMargin and TranscriptColorizer to views/controls
}
