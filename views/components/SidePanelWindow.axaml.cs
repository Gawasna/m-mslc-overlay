using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;

namespace m_mslc_overlay.views.components
{
    public partial class SidePanelWindow : Window
    {
        public Action? OnClosedAction { get; set; }

        public SidePanelWindow()
        {
            InitializeComponent();
            this.Closed += (s, e) => OnClosedAction?.Invoke();
        }

        private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var pos = e.GetPosition(this);
            if (pos.Y >= 8.0)
            {
                BeginMoveDrag(e);
            }
        }

        private void Window_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var point = e.GetCurrentPoint(this);
            if (point.Properties.IsLeftButtonPressed)
            {
                var pos = e.GetPosition(this);
                double width = this.Bounds.Width;
                double height = this.Bounds.Height;
                double margin = 8.0;

                bool isLeft = pos.X < margin;
                bool isRight = pos.X > width - margin;
                bool isTop = pos.Y < margin;
                bool isBottom = pos.Y > height - margin;

                if (isLeft && isTop) this.BeginResizeDrag(WindowEdge.NorthWest, e);
                else if (isRight && isTop) this.BeginResizeDrag(WindowEdge.NorthEast, e);
                else if (isLeft && isBottom) this.BeginResizeDrag(WindowEdge.SouthWest, e);
                else if (isRight && isBottom) this.BeginResizeDrag(WindowEdge.SouthEast, e);
                else if (isLeft) this.BeginResizeDrag(WindowEdge.West, e);
                else if (isRight) this.BeginResizeDrag(WindowEdge.East, e);
                else if (isTop) this.BeginResizeDrag(WindowEdge.North, e);
                else if (isBottom) this.BeginResizeDrag(WindowEdge.South, e);
            }
        }

        private void Window_PointerMoved(object? sender, PointerEventArgs e)
        {
            var pos = e.GetPosition(this);
            double width = this.Bounds.Width;
            double height = this.Bounds.Height;
            double margin = 8.0;

            bool isLeft = pos.X < margin;
            bool isRight = pos.X > width - margin;
            bool isTop = pos.Y < margin;
            bool isBottom = pos.Y > height - margin;

            if (isLeft && isTop)
            {
                this.Cursor = new Cursor(StandardCursorType.TopLeftCorner);
            }
            else if (isRight && isBottom)
            {
                this.Cursor = new Cursor(StandardCursorType.BottomRightCorner);
            }
            else if (isRight && isTop)
            {
                this.Cursor = new Cursor(StandardCursorType.TopRightCorner);
            }
            else if (isLeft && isBottom)
            {
                this.Cursor = new Cursor(StandardCursorType.BottomLeftCorner);
            }
            else if (isLeft || isRight)
            {
                this.Cursor = new Cursor(StandardCursorType.SizeWestEast);
            }
            else if (isTop || isBottom)
            {
                this.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
            }
            else
            {
                this.Cursor = new Cursor(StandardCursorType.Arrow);
            }
        }

        private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object? sender, RoutedEventArgs e)
        {
            Close();
        }

        private void TabTranscript_Click(object? sender, RoutedEventArgs e)
        {
            TranscriptPanel.IsVisible = true;
            GeneratorPanel.IsVisible = false;
            TabTranscriptIndicator.BorderBrush = (IBrush)this.FindResource("PrimaryBrush")!;
            TabGeneratorIndicator.BorderBrush = Brushes.Transparent;
            TabTranscriptText.FontWeight = FontWeight.Bold;
            TabTranscriptText.Foreground = (IBrush)this.FindResource("TextPrimaryBrush")!;
            TabGeneratorText.FontWeight = FontWeight.Normal;
            TabGeneratorText.Foreground = (IBrush)this.FindResource("TextSecondaryBrush")!;
        }

        private void TabGenerator_Click(object? sender, RoutedEventArgs e)
        {
            TranscriptPanel.IsVisible = false;
            GeneratorPanel.IsVisible = true;
            TabTranscriptIndicator.BorderBrush = Brushes.Transparent;
            TabGeneratorIndicator.BorderBrush = (IBrush)this.FindResource("PrimaryBrush")!;
            TabTranscriptText.FontWeight = FontWeight.Normal;
            TabTranscriptText.Foreground = (IBrush)this.FindResource("TextSecondaryBrush")!;
            TabGeneratorText.FontWeight = FontWeight.Bold;
            TabGeneratorText.Foreground = (IBrush)this.FindResource("TextPrimaryBrush")!;
        }

        private void ExportBtn_Click(object? sender, RoutedEventArgs e)
        {
            // Mock export action
        }

        private void CopyAllBtn_Click(object? sender, RoutedEventArgs e)
        {
            // Mock copy all action
        }

        private void ClearHistoryBtn_Click(object? sender, RoutedEventArgs e)
        {
            // Mock clear history action
        }

        private void CopyItemBtn_Click(object? sender, RoutedEventArgs e)
        {
            // Mock copy single item action
        }

        private void SendManualBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ManualSubtitleInput.Text)) return;
            
            // Auto clear if checked
            if (AutoClearCheck.IsChecked == true)
            {
                ManualSubtitleInput.Text = string.Empty;
            }
        }

        private void QuickAnnounceBtn_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Content is string text)
            {
                // Remove bracket characters
                string cleanText = text.Trim('[', ']');
                // Mock sending cleanText
            }
        }

        private void LoadScriptBtn_Click(object? sender, RoutedEventArgs e)
        {
            // Mock load script action
        }

        private void PrompterPrev_Click(object? sender, RoutedEventArgs e)
        {
            // Mock prompter previous action
        }

        private void PrompterNext_Click(object? sender, RoutedEventArgs e)
        {
            // Mock prompter next action
        }

        private void PrompterReset_Click(object? sender, RoutedEventArgs e)
        {
            // Mock prompter reset action
        }

        private void SyncMinus_Click(object? sender, RoutedEventArgs e)
        {
            // Mock sync delay minus
            SyncDelayText.Text = "+200 ms";
        }

        private void SyncPlus_Click(object? sender, RoutedEventArgs e)
        {
            // Mock sync delay plus
            SyncDelayText.Text = "+400 ms";
        }
    }
}
