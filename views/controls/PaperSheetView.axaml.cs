using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AvaloniaEdit;
using MMslcOverlay.ViewModels.Workspace;

namespace MMslcOverlay.Views.Controls;

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
                
                var magicMargin = new MagicCursorMargin();
                editor.TextArea.LeftMargins.Add(magicMargin);

                editor.TextArea.TextView.LineTransformers.Add(new TranscriptColorizer());
                
                var protector = new MachineSegmentProtector(editor.Document);
                editor.TextArea.ReadOnlySectionProvider = protector;
                
                editor.TextArea.TextView.BackgroundRenderers.Add(new MachineSegmentHighlighter());
                editor.TextArea.TextView.BackgroundRenderers.Add(new PageBreakRenderer());
            }
        }
    }
}
