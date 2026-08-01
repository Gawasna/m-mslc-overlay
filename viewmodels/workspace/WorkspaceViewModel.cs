using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MMslcOverlay.Services.Workspace;

namespace MMslcOverlay.ViewModels.Workspace;

public enum WorkspaceState { Idle, Active }

/// <summary>
/// Root ViewModel cho một workspace session.
/// Hold WorkspaceService (lifetime), expose PaperSheetViewModel sang MainWindow.
/// </summary>
public class WorkspaceViewModel : INotifyPropertyChanged, IDisposable
{
    private WorkspaceService? _service;
    public WorkspaceService? Service => _service;
    private bool _isOpen;
    private PaperSheetViewModel? _sheet;
    private string _workspaceName = "Untitled";

    public NavPaneViewModel NavPane { get; } = new NavPaneViewModel();

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
                // We need to re-evaluate CanExecute for export commands when IsOpen changes
                ((RelayCommand)ExportSrtCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ExportTxtCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ExportMdCommand).RaiseCanExecuteChanged();
                ((RelayCommand)ExportPdfCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public string WorkspaceName
    {
        get => _workspaceName;
        private set
        {
            if (_workspaceName != value)
            {
                _workspaceName = value;
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

    // Commands
    public ICommand NewWorkspaceCommand { get; }
    public ICommand OpenWorkspaceCommand { get; }
    public ICommand ExportSrtCommand { get; }
    public ICommand ExportTxtCommand { get; }
    public ICommand ExportMdCommand { get; }
    public ICommand ExportPdfCommand { get; }

    public WorkspaceViewModel()
    {
        var settings = WorkspaceSettings.Load();
        
        NewWorkspaceCommand = new RelayCommand(() => OpenOrCreate(settings.ResolveWorkspacePath()));
        OpenWorkspaceCommand = new RelayCommand(() => OpenOrCreate(settings.ResolveWorkspacePath()));
        
        ExportSrtCommand = new RelayCommand(() => { /* stub */ }, () => IsOpen);
        ExportTxtCommand = new RelayCommand(() => { /* stub */ }, () => IsOpen);
        ExportMdCommand = new RelayCommand(() => { /* stub */ }, () => IsOpen);
        ExportPdfCommand = new RelayCommand(() => { /* stub */ }, () => IsOpen);
    }

    public void OpenOrCreate(string workspaceRoot)
    {
        try
        {
            _service?.Dispose();
            _service = new WorkspaceService(workspaceRoot);
            _service.OpenOrCreate();
            
            WorkspaceName = Path.GetFileName(workspaceRoot);
            Sheet = new PaperSheetViewModel(_service);
            IsOpen = true;
            State = WorkspaceState.Active;

            // Save settings
            var settings = WorkspaceSettings.Load();
            settings.LastWorkspacePath = workspaceRoot;
            settings.Save();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open workspace: {ex.Message}");
            // Handle error, e.g., show a dialog in a real app
        }
    }

    // Gap 7: Close workspace with cleanup
    public void CloseWorkspace()
    {
        _service?.Dispose();
        _service = null;
        Sheet = null;
        IsOpen = false;
        State = WorkspaceState.Idle;
        WorkspaceName = "Untitled";
    }

    // Gap 8: Start recording (auto-creates workspace if needed)
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

    public void StartRecording()
    {
        if (_service?.AudioService == null)
        {
            System.Diagnostics.Debug.WriteLine("[WorkspaceViewModel] Cannot start recording: AudioService not initialized.");
            return;
        }
        _service.AudioService.StartRecording();
        IsRecording = true;
    }

    public void StopRecording()
    {
        _service?.AudioService?.StopRecording();
        IsRecording = false;
    }

    public void Dispose()
    {
        _service?.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected virtual void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ─── UI Actions ────────────────────────────────────────────────────────
    public void ExportSrt()
    {
        if (ExportSrtCommand.CanExecute(null))
        {
            ExportSrtCommand.Execute(null);
        }
    }

    public void ImportScript()
    {
        // Stub for importing script in workspace
    }
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
