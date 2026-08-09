using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MMslcOverlay.Core.Workspace.Models;
using System;

namespace MMslcOverlay.Views.Workspace;

public partial class SegmentEditDialog : Window
{
    public string ResultTextSrc { get; private set; } = string.Empty;
    public string? ResultTextTrs { get; private set; }
    public bool Confirmed { get; private set; }

    public SegmentEditDialog()
    {
        InitializeComponent();
    }

    public SegmentEditDialog(MergedSegment segment) : this()
    {
        var ts = TimeSpan.FromMilliseconds(segment.BaseSegment.TsStartMs);
        SegmentInfoLabel.Text = $"[{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}] [{segment.BaseSegment.SpeakerId}]";

        TextSrcBox.Text = segment.TextSrc;
        TextTrsBox.Text = segment.TextTrs ?? string.Empty;

        KeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape) OnCancel(s, e);
            if ((e.Key == Key.Enter || e.Key == Key.S) && e.KeyModifiers.HasFlag(KeyModifiers.Control)) OnConfirm(s, e);
        };
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        ResultTextSrc = TextSrcBox.Text ?? string.Empty;
        ResultTextTrs = string.IsNullOrWhiteSpace(TextTrsBox.Text) ? null : TextTrsBox.Text;
        Confirmed = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
