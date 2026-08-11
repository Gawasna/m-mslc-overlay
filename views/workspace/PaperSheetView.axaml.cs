using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using MMslcOverlay.ViewModels.Workspace;
using m_mslc_overlay.views.controls;
using MMslcOverlay.Views.Workspace.Components;

namespace MMslcOverlay.Views.Workspace;

public partial class PaperSheetView : UserControl
{
    /// <summary>
    /// Bubbles ExportRequested from the embedded SubToolbar.
    /// Subscribe in the parent window (MainWindow) to perform actual export logic.
    /// </summary>
    public event Func<string, Task>? ExportRequested;

    public PaperSheetView()
    {
        InitializeComponent();
        
        var editor = this.FindControl<WebView2Control>("Editor");
        if (editor != null)
        {
            try 
            {
                // Gap 4 fix: Reset _isWebReady before navigation
                _isWebReady = false;
                editor.NavigateToString(BuildSelfContainedHtml());
                editor.WebMessageReceived += OnWebMessageReceived;
                
                // Gap 2 fix: Add timeout fallback for DOCUMENT_READY
                _documentReadyTimeout = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _documentReadyTimeout.Tick += (s, e) =>
                {
                    _documentReadyTimeout?.Stop();
                    if (!_isWebReady)
                    {
                        System.Diagnostics.Debug.WriteLine("[PaperSheetView] WARNING: DOCUMENT_READY not received after 3s. JS may have failed to init.");
                    }
                };
                _documentReadyTimeout.Start();
            } 
            catch (Exception ex) 
            {
                System.Diagnostics.Debug.WriteLine($"[PaperSheetView] asset load failed: {ex.Message}");
            }
        }

        // Wire SubToolbar ExportRequested bubble-up
        var subToolbar = this.FindControl<SubToolbar>("WorkspaceSubToolbar");
        if (subToolbar != null)
        {
            subToolbar.ExportRequested += async (payload) =>
            {
                if (ExportRequested != null)
                    await ExportRequested.Invoke(payload);
            };
        }

        this.DataContextChanged += OnDataContextChangedHandler;
    }

    private bool _isWebReady = false;
    private Avalonia.Threading.DispatcherTimer? _documentReadyTimeout;

    private void OnDataContextChangedHandler(object? sender, EventArgs e)
    {
        UpdateViewModelWiring();
        // REMOVED: Synthetic DOCUMENT_READY call — only JS can trigger it
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
            // Bug 1 fix: nếu WebView2 đã sẵn sàng từ trước (workspace được reopen trong cùng window),
            // JS sẽ không fire DOCUMENT_READY nữa → phải inject synthetic để LoadInitialState chạy.
            if (_isWebReady && _boundVm?.Sheet is PaperSheetViewModel sheetVm)
            {
                sheetVm.HandleWebMessage("{\"type\":\"DOCUMENT_READY\"}");
            }
        }
    }

    private void WireSheet()
    {
        if (_boundVm?.Sheet is PaperSheetViewModel vm)
        {
            // Gap 4 fix: Guard SendToEditor with _isWebReady check
            vm.SendToEditorAction = (msg) => 
            {
                if (!_isWebReady)
                {
                    System.Diagnostics.Debug.WriteLine($"[PaperSheetView] Dropped message {msg.Type} — WebView2 not ready");
                    return;
                }
                if (Editor == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[PaperSheetView] Dropped message {msg.Type} — Editor is null");
                    return;
                }
                
                string json = System.Text.Json.JsonSerializer.Serialize(msg);
                Console.WriteLine($"[PaperSheetView] sending message: {json}");
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    Editor.PostWebMessage(json);
                });
            };
            
            vm.ShowContextMenuAction = (menuType, targetId) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ShowEditorContextMenu(menuType, targetId);
                });
            };

            vm.OpenEditDialogAction = async (segment) =>
            {
                var dialog = new SegmentEditDialog(segment);
                if (this.VisualRoot is Window window)
                {
                    await dialog.ShowDialog(window);
                    if (dialog.Confirmed)
                    {
                        vm.CommitSegmentEdit(segment, dialog.ResultTextSrc, dialog.ResultTextTrs);
                    }
                }
            };
        }
    }

    private void ShowEditorContextMenu(string menuType, string targetId)
    {
        var menu = this.FindControl<ContextMenu>("EditorContextMenu");
        if (menu == null || Editor == null) return;
        
        if (menuType == "MachineSegment")
        {
            menu.ItemsSource = new[]
            {
                new MenuItem { Header = $"Phát lại đoạn âm thanh ({targetId})" },
                new MenuItem { Header = "Ẩn phân đoạn này" },
                new MenuItem { Header = "Chuyển thành văn bản tự do" }
            };
        }
        else if (menuType == "FreeformBlock")
        {
            menu.ItemsSource = new[]
            {
                new MenuItem { Header = "Định dạng lại đoạn văn bản" },
                new MenuItem { Header = $"Xóa khối văn bản tự do ({targetId})" }
            };
        }
        else 
        {
            menu.ItemsSource = new[]
            {
                new MenuItem { Header = "Sao chép" },
                new MenuItem { Header = "Dán" }
            };
        }
        
        menu.Open(Editor);
    }

    private void OnWebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try 
        {
            string json = e.TryGetWebMessageAsString();
            Console.WriteLine($"[PaperSheetView] received message: {json}");
            System.Diagnostics.Debug.WriteLine($"[PaperSheetView] received message: {json}");
            
            // Gap 2 fix: DOCUMENT_READY sets _isWebReady flag first
            if (json.Contains("\"DOCUMENT_READY\""))
            {
                _isWebReady = true;
                _documentReadyTimeout?.Stop(); // Cancel timeout — JS init succeeded
                System.Diagnostics.Debug.WriteLine("[PaperSheetView] DOCUMENT_READY received, WebView2 is ready");
            }
            
            // ✅ Debug: Check PLAY_AUDIO forwarding
            if (json.Contains("\"PLAY_AUDIO\""))
            {
                System.Diagnostics.Debug.WriteLine($"[PaperSheetView] ✅ PLAY_AUDIO detected, checking DataContext...");
                System.Diagnostics.Debug.WriteLine($"[PaperSheetView]   DataContext type: {DataContext?.GetType().Name ?? "null"}");
                System.Diagnostics.Debug.WriteLine($"[PaperSheetView]   Is WorkspaceViewModel: {DataContext is WorkspaceViewModel}");
                if (DataContext is WorkspaceViewModel wvm)
                {
                    System.Diagnostics.Debug.WriteLine($"[PaperSheetView]   Sheet type: {wvm.Sheet?.GetType().Name ?? "null"}");
                    System.Diagnostics.Debug.WriteLine($"[PaperSheetView]   Is PaperSheetViewModel: {wvm.Sheet is PaperSheetViewModel}");
                }
            }
            
            if (DataContext is WorkspaceViewModel workspaceVm && workspaceVm.Sheet is PaperSheetViewModel vm)
            {
                if (json.Contains("\"PLAY_AUDIO\""))
                {
                    System.Diagnostics.Debug.WriteLine($"[PaperSheetView] → Forwarding PLAY_AUDIO to PaperSheetViewModel");
                }
                vm.HandleWebMessage(json);
            }
            else
            {
                if (json.Contains("\"PLAY_AUDIO\""))
                {
                    System.Diagnostics.Debug.WriteLine($"[PaperSheetView] ❌ Cannot forward - DataContext check FAILED");
                }
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
