using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using MMslcOverlay.Core.Workspace.Export;
using MMslcOverlay.Core.Workspace.Models;
using MMslcOverlay.Core.Workspace.Storage;
using MMslcOverlay.Services.Workspace;

namespace MMslcOverlay.ViewModels.Workspace;

public enum WorkspaceState { Idle, Active }

/// <summary>
/// File entry shown in the Sidebar's Session Files list.
/// </summary>
public sealed class WorkspaceFileItem
{
    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string Icon { get; set; } = "FileDocumentOutline"; // MaterialIcon kind name
}

/// <summary>
/// Root ViewModel cho một workspace session.
/// Hold WorkspaceService (lifetime), expose PaperSheetViewModel sang MainWindow.
/// </summary>
public class WorkspaceViewModel : INotifyPropertyChanged, IDisposable
{
    private WorkspaceService? _service;
    public WorkspaceService? Service => _service;
    private bool _isOpen;
    private bool _isDirty;
    private PaperSheetViewModel? _sheet;
    private string _workspaceName = "Untitled";
    private string _workspacePath = string.Empty;
    private string _lastModifiedDisplay = string.Empty;

    public NavPaneViewModel NavPane { get; } = new NavPaneViewModel();

    /// <summary>Danh sách file trong workspace root và exports/ hiển thị trên Sidebar.</summary>
    public ObservableCollection<WorkspaceFileItem> SessionFiles { get; } = new();

    private string _selectedAiModel = "Gemini 1.5 Pro";
    public string SelectedAiModel
    {
        get => _selectedAiModel;
        set
        {
            if (_selectedAiModel != value)
            {
                _selectedAiModel = value;
                NavPane.AiPane.SelectedModel = value;
                OnPropertyChanged();
            }
        }
    }

    private WorkspaceState _state = WorkspaceState.Idle;
    public WorkspaceState State
    {
        get => _state;
        private set { if (_state != value) { _state = value; OnPropertyChanged(); } }
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set
        {
            if (_isOpen != value)
            {
                _isOpen = value;
                OnPropertyChanged();
                ((RelayCommand)ExportSrtCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ExportTxtCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ExportMdCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ExportPdfCommand).RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Dirty flag: true khi user có thay đổi chưa flush xuống SQLite.</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (_isDirty != value)
            {
                _isDirty = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public void MarkDirty() => IsDirty = true;
    public void ClearDirty() => IsDirty = false;

    public string WorkspaceName
    {
        get => _workspaceName;
        private set
        {
            if (_workspaceName != value)
            {
                _workspaceName = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    /// <summary>Tên hiển thị kèm dấu * khi có thay đổi chưa lưu.</summary>
    public string DisplayName => IsDirty ? $"{WorkspaceName} *" : WorkspaceName;

    public string WorkspacePath
    {
        get => _workspacePath;
        private set
        {
            if (_workspacePath != value)
            {
                _workspacePath = value;
                OnPropertyChanged();
            }
        }
    }

    public string LastModifiedDisplay
    {
        get => _lastModifiedDisplay;
        private set
        {
            if (_lastModifiedDisplay != value)
            {
                _lastModifiedDisplay = value;
                OnPropertyChanged();
            }
        }
    }

    public PaperSheetViewModel? Sheet
    {
        get => _sheet;
        private set
        {
            if (_sheet != value)
            {
                _sheet = value;
                OnPropertyChanged();
            }
        }
    }

    // Export commands (File > Export menu, SubToolbar)
    public ICommand ExportSrtCommand { get; }
    public ICommand ExportTxtCommand { get; }
    public ICommand ExportMdCommand { get; }
    public ICommand ExportPdfCommand { get; }

    /// <summary>UI hook: gọi SaveFileDialog và trả path, null nếu cancel.</summary>
    public Func<string, string, string, Task<string?>>? RequestSavePathAction { get; set; }

    /// <summary>UI hook: mở file từ sidebar bằng default OS app.</summary>
    public Action<string>? OpenFileExternallyAction { get; set; }

    public WorkspaceViewModel()
    {
        ExportSrtCommand  = new RelayCommand(() => _ = ExportAsync(new SrtExporter(),      "srt",  "Subtitle (.srt)"),  () => IsOpen);
        ExportTxtCommand  = new RelayCommand(() => _ = ExportAsync(new TxtExporter(),      "txt",  "Text (.txt)"),      () => IsOpen);
        ExportMdCommand   = new RelayCommand(() => _ = ExportAsync(new MarkdownExporter(), "md",   "Markdown (.md)"),   () => IsOpen);
        ExportPdfCommand  = new RelayCommand(() => _ = ExportAsync(new PdfExporter(),      "pdf",  "PDF (.pdf)"),       () => IsOpen);
    }

    public void OpenOrCreate(string workspaceRoot)
    {
        try
        {
            _service?.Dispose();
            _service = new WorkspaceService(workspaceRoot);
            _service.OpenOrCreate();

            WorkspaceName = Path.GetFileName(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar));
            WorkspacePath = workspaceRoot;
            Sheet = new PaperSheetViewModel(_service, this);
            IsOpen = true;
            State = WorkspaceState.Active;
            ClearDirty();

            RefreshSessionFiles();
            UpdateLastModified(workspaceRoot);

            var settings = WorkspaceSettings.Load();
            settings.LastWorkspacePath = workspaceRoot;
            settings.Save();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open workspace: {ex.Message}");
            throw;
        }
    }

    /// <summary>Đóng workspace và release resources.</summary>
    public void CloseWorkspace()
    {
        _service?.Dispose();
        _service = null;
        Sheet = null;
        IsOpen = false;
        State = WorkspaceState.Idle;
        WorkspaceName = "Untitled";
        WorkspacePath = string.Empty;
        LastModifiedDisplay = string.Empty;
        SessionFiles.Clear();
        ClearDirty();
    }

    /// <summary>Flush freeform pending edits thông qua Sheet VM rồi chờ ack.</summary>
    public async Task FlushPendingAsync(int timeoutMs = 2000)
    {
        if (Sheet is { } sheet)
        {
            sheet.RequestFlushFreeform();
            var tcs = new TaskCompletionSource<bool>();
            sheet.FreeformFlushed += OnFlushed;
            void OnFlushed()
            {
                sheet.FreeformFlushed -= OnFlushed;
                tcs.TrySetResult(true);
            }
            var delay = Task.Delay(timeoutMs);
            await Task.WhenAny(tcs.Task, delay);
            ClearDirty();
        }
    }

    // ─── Recording ──────────────────────────────────────────────────────
    private bool _isRecording;

    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (_isRecording != value)
            {
                _isRecording = value;
                OnPropertyChanged();
            }
        }
    }

    public void ToggleRecording()
    {
        if (IsRecording) StopRecording();
        else StartRecording();
    }

    public void StartRecording()
    {
        if (!IsOpen) return;
        
        // Use NEW StreamingPcmRecorder for Phase 2
        if (_service?.AudioRecorder == null)
        {
            System.Diagnostics.Debug.WriteLine("[WorkspaceViewModel] Cannot start recording: AudioRecorder not initialized.");
            return;
        }
        
        _service.AudioRecorder.StartRecording();
        IsRecording = true;
        System.Diagnostics.Debug.WriteLine($"[WorkspaceViewModel] Recording started: {_service.AudioRecorder.SessionId}");
    }

    public void StopRecording()
    {
        // Use NEW StreamingPcmRecorder for Phase 2
        _service?.AudioRecorder?.StopRecording();
        IsRecording = false;
        
        if (!string.IsNullOrEmpty(WorkspacePath)) RefreshSessionFiles();
        System.Diagnostics.Debug.WriteLine("[WorkspaceViewModel] Recording stopped");
    }

    // ─── Export / Import / Files ────────────────────────────────────────
    private async Task ExportAsync(IExporter exporter, string ext, string label)
    {
        if (_service?.SegmentRepo == null) return;
        if (RequestSavePathAction == null)
        {
            System.Diagnostics.Debug.WriteLine("[Export] No RequestSavePathAction wired.");
            return;
        }

        // Flush trước khi export để đảm bảo nội dung mới nhất
        await FlushPendingAsync();

        var suggestedName = $"{WorkspaceName}.{ext}";
        var destPath = await RequestSavePathAction(label, ext, suggestedName);
        if (string.IsNullOrEmpty(destPath)) return;

        try
        {
            var engine = new ExportEngine(_service.SegmentRepo);
            string content = engine.RunExport(exporter);

            if (ext.Equals("pdf", StringComparison.OrdinalIgnoreCase))
            {
                // PDF exporter returns a temp file path
                if (File.Exists(content))
                {
                    File.Copy(content, destPath, true);
                    File.Delete(content);
                }
            }
            else
            {
                await File.WriteAllTextAsync(destPath, content, System.Text.Encoding.UTF8);
            }
            RefreshSessionFiles();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Export] failed: {ex.Message}");
        }
    }

    /// <summary>Import script (txt/md) vào freeform_blocks anchored at document top.</summary>
    public async Task ImportScriptAsync(string filePath)
    {
        if (_service?.UserDataRepo == null) return;
        try
        {
            string content = await File.ReadAllTextAsync(filePath);
            if (string.IsNullOrWhiteSpace(content)) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var block = new FreeformBlock
            {
                AnchorAfter = null,
                Content = content,
                CreatedAt = now,
                UpdatedAt = now
            };
            long id = _service.UserDataRepo.InsertFreeformBlock(block);
            MarkDirty();

            // Reload toàn bộ document để JS render block mới
            Sheet?.SendToEditor(new BridgeMessage
            {
                Type = "FREEFORM_PERSISTED",
                AnchorAfter = null,
                BlockId = id.ToString(),
                Content = content
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ImportScript] failed: {ex.Message}");
        }
    }

    public void RefreshSessionFiles()
    {
        SessionFiles.Clear();
        if (string.IsNullOrEmpty(WorkspacePath) || _service == null) return;

        try
        {
            // WAV files của active/sealed chunks
            var segDir = _service.Storage.SegmentsDir;
            if (Directory.Exists(segDir))
            {
                foreach (var wav in Directory.EnumerateFiles(segDir, "*.audio.wav"))
                {
                    SessionFiles.Add(new WorkspaceFileItem
                    {
                        FileName = Path.GetFileName(wav),
                        FullPath = wav,
                        Icon = "MusicNote"
                    });
                }
            }

            // Exported artifacts
            var expDir = _service.Storage.ExportsDir;
            if (Directory.Exists(expDir))
            {
                foreach (var f in Directory.EnumerateFiles(expDir, "*.*"))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    SessionFiles.Add(new WorkspaceFileItem
                    {
                        FileName = Path.GetFileName(f),
                        FullPath = f,
                        Icon = ext switch
                        {
                            ".srt" => "SubtitlesOutline",
                            ".txt" => "FileDocumentOutline",
                            ".md"  => "LanguageMarkdown",
                            ".pdf" => "FilePdfBox",
                            _      => "FileOutline"
                        }
                    });
                }
            }

            // notes.md hoặc user-saved docs tại root workspace
            foreach (var f in Directory.EnumerateFiles(WorkspacePath, "*.md").Union(Directory.EnumerateFiles(WorkspacePath, "*.txt")))
            {
                SessionFiles.Add(new WorkspaceFileItem
                {
                    FileName = Path.GetFileName(f),
                    FullPath = f,
                    Icon = "NotebookOutline"
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SessionFiles] scan failed: {ex.Message}");
        }
    }

    private void UpdateLastModified(string workspaceRoot)
    {
        try
        {
            var meta = _service?.Storage.SessionMetaPath;
            DateTime modified = meta != null && File.Exists(meta)
                ? File.GetLastWriteTime(meta)
                : File.GetLastWriteTime(workspaceRoot);
            LastModifiedDisplay = $"Last Modified: {modified:yyyy-MM-dd HH:mm}";
        }
        catch { LastModifiedDisplay = string.Empty; }
    }

    public void OpenSessionFile(WorkspaceFileItem? item)
    {
        if (item == null || string.IsNullOrEmpty(item.FullPath)) return;
        OpenFileExternallyAction?.Invoke(item.FullPath);
    }

    public void Dispose()
    {
        _service?.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected virtual void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ─── Legacy convenience entry points (used by toolbar buttons) ─────
    public void ExportSrt()
    {
        if (ExportSrtCommand.CanExecute(null)) ExportSrtCommand.Execute(null);
    }

    public void ImportScript()
    {
        // Alias for toolbar; actual file picker wired in MainWindow.
        ImportScriptRequested?.Invoke();
    }

    public event Action? ImportScriptRequested;
}

public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute == null || _canExecute();

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
