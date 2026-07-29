using System;
using System.IO;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using MMslcOverlay.ViewModels.Workspace;
using m_mslc_overlay.views.controls;

namespace MMslcOverlay.Views.Workspace;

public partial class PaperSheetView : UserControl
{
    public PaperSheetView()
    {
        InitializeComponent();
        
        var editor = this.FindControl<WebView2Control>("Editor");
        if (editor != null)
        {
            try 
            {
                editor.NavigateToString(BuildSelfContainedHtml());
                editor.WebMessageReceived += OnWebMessageReceived;
            } 
            catch (Exception ex) 
            {
                System.Diagnostics.Debug.WriteLine($"[PaperSheetView] asset load failed: {ex.Message}");
            }
        }

        this.DataContextChanged += OnDataContextChangedHandler;
    }

    private bool _isWebReady = false;

    private void OnDataContextChangedHandler(object? sender, EventArgs e)
    {
        UpdateViewModelWiring();
        if (_isWebReady && DataContext is WorkspaceViewModel workspaceVm && workspaceVm.Sheet is PaperSheetViewModel vm)
        {
            vm.HandleWebMessage("{\"type\":\"DOCUMENT_READY\"}");
        }
    }

    private WorkspaceViewModel? _boundVm;

    private void UpdateViewModelWiring()
    {
        if (DataContext is WorkspaceViewModel workspaceVm)
        {
            if (_boundVm != workspaceVm)
            {
                if (_boundVm != null)
                {
                    _boundVm.PropertyChanged -= WorkspaceVm_PropertyChanged;
                }
                _boundVm = workspaceVm;
                _boundVm.PropertyChanged += WorkspaceVm_PropertyChanged;
            }
            WireSheet();
        }
    }

    private void WorkspaceVm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.Sheet))
        {
            WireSheet();
            if (_isWebReady && _boundVm?.Sheet is PaperSheetViewModel vm)
            {
                vm.HandleWebMessage("{\"type\":\"DOCUMENT_READY\"}");
            }
        }
    }

    private void WireSheet()
    {
        if (_boundVm?.Sheet is PaperSheetViewModel vm)
        {
            vm.SendToEditorAction = (msg) => 
            {
                if (Editor != null) 
                {
                    string json = System.Text.Json.JsonSerializer.Serialize(msg);
                    Console.WriteLine($"[PaperSheetView] sending message: {json}");
                    Editor.PostWebMessage(json);
                }
            };
        }
    }

    private void OnWebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try 
        {
            string json = e.TryGetWebMessageAsString();
            Console.WriteLine($"[PaperSheetView] received message: {json}");
            System.Diagnostics.Debug.WriteLine($"[PaperSheetView] received message: {json}");
            
            if (DataContext is WorkspaceViewModel workspaceVm && workspaceVm.Sheet is PaperSheetViewModel vm)
            {
                vm.HandleWebMessage(json);
            }
            else if (json.Contains("\"DOCUMENT_READY\""))
            {
                _isWebReady = true;
            }
        } 
        catch (Exception ex) 
        {
            Console.WriteLine($"[PaperSheetView] web message error: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[PaperSheetView] web message error: {ex.Message}");
        }
    }

    // ─── Self-contained HTML ──────────────────────────────────────────────────
    private static string BuildSelfContainedHtml()
    {
        string css = ReadAsset("avares://m-mslc-overlay/assets/workspace/editor.css");
        string js  = ReadAsset("avares://m-mslc-overlay/assets/workspace/editor.js");

        string errorScript =
            "<script>" +
            "window.onerror = function(msg, url, line, col, error) {" +
            "    window.chrome.webview.postMessage(JSON.stringify({ type: \"JS_ERROR\", message: msg, line: line }));" +
            "};" +
            "console.error = function(msg) {" +
            "    window.chrome.webview.postMessage(JSON.stringify({ type: \"JS_ERROR\", message: msg }));" +
            "};" +
            "</script>";

        string focusScript =
            "<script>" +
            "document.addEventListener('click', function() {" +
            "    var pm = document.querySelector('.ProseMirror');" +
            "    if (pm) pm.focus();" +
            "});" +
            "</script>";

        return $"""
            <!DOCTYPE html>
            <html>
            <head>
              <meta charset="utf-8">
              <style>{css}</style>
            </head>
            <body>
              <div id="editor"></div>
              {errorScript}
              <script>{js}</script>
              {focusScript}
            </body>
            </html>
            """;
    }

    private static string ReadAsset(string avares)
    {
        var uri = new Uri(avares);
        using var stream = AssetLoader.Open(uri);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
