using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AvaloniaEdit;
using MMslcOverlay.ViewModels.Workspace;
using MMslcOverlay.Views.Controls;

namespace MMslcOverlay.Views.Workspace;

public partial class PaperSheetView : UserControl
{
    public PaperSheetView()
    {
        InitializeComponent();
        this.DataContextChanged += OnDataContextChangedHandler;
    }

    private void OnDataContextChangedHandler(object? sender, EventArgs e)
    {
        if (DataContext is PaperSheetViewModel vm)
        {
            var editor = this.FindControl<TextEditor>("Editor");
            if (editor != null)
            {
                editor.Document = vm.Document;

                editor.TextArea.LeftMargins.Add(new TranscriptGutterMargin(editor.Document));
                
                var magicMargin = new MagicCursorMargin(() => vm.MagicCursorOffset);
                editor.TextArea.LeftMargins.Add(magicMargin);
                editor.Document.Changed += (s, e) => magicMargin.InvalidateVisual();

                editor.TextArea.TextView.LineTransformers.Add(new TranscriptColorizer());
                
                var protector = new MachineSegmentProtector(editor.Document);
                editor.TextArea.ReadOnlySectionProvider = protector;
                
                editor.TextArea.TextView.BackgroundRenderers.Add(new MachineSegmentHighlighter());
                var pageBreakRenderer = new PageBreakRenderer { PageBreakOffsets = vm.PageBreakOffsets };
                editor.TextArea.TextView.BackgroundRenderers.Add(pageBreakRenderer);

                vm.ScrollController.ModeChanged += (mode) =>
                {
                    if (mode == ScrollMode.WatchMagicCursor)
                    {
                        var line = editor.Document.GetLineByOffset(vm.MagicCursorOffset);
                        if (line != null)
                        {
                            editor.ScrollTo(line.LineNumber, 0);
                        }
                    }
                };
                editor.DoubleTapped += (s, e) => 
                {
                    OnEditSegmentClick(s, e);
                };
            }
        }
    }

    private void OnEditSegmentClick(object? sender, RoutedEventArgs e)
    {
        var editor = this.FindControl<TextEditor>("Editor");
        if (editor == null || DataContext is not PaperSheetViewModel vm) return;

        var caretOffset = editor.TextArea.Caret.Offset;
        // Logic to extract segment from caret offset and trigger SegmentEditSession
        // Currently a stub, would open a dialog or inline editor.
        System.Diagnostics.Debug.WriteLine($"Edit segment requested at {caretOffset}");
    }

    private void OnPlaceMagicCursorClick(object? sender, RoutedEventArgs e)
    {
        var editor = this.FindControl<TextEditor>("Editor");
        if (editor == null || DataContext is not PaperSheetViewModel vm) return;

        vm.MagicCursorOffset = editor.TextArea.Caret.Offset;
    }

    private void OnDeleteFreeformClick(object? sender, RoutedEventArgs e)
    {
        // Stub for deleting a freeform block
        System.Diagnostics.Debug.WriteLine("Delete freeform block requested.");
    }
}
