using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;

namespace m_mslc_overlay.viewmodels.transcript
{
    /// <summary>
    /// Tracks live recording session state consumed by RecordingHeaderBar.
    /// Single responsibility: session timing + status. No UI refs, no business logic.
    /// </summary>
    public sealed class RecordingSessionViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly DispatcherTimer _elapsedTimer;
        private DateTime _sessionStart;

        private bool _isRecording;
        private string _sessionName = "SESSION #01";
        private TimeSpan _elapsed = TimeSpan.Zero;
        private double _micLevel; // 0.0 – 1.0

        // ─── Properties ───────────────────────────────────────────────

        public bool IsRecording
        {
            get => _isRecording;
            private set { _isRecording = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusLabel)); }
        }

        public string SessionName
        {
            get => _sessionName;
            set { _sessionName = value; OnPropertyChanged(); }
        }

        public TimeSpan Elapsed
        {
            get => _elapsed;
            private set { _elapsed = value; OnPropertyChanged(); OnPropertyChanged(nameof(ElapsedFormatted)); }
        }

        public string ElapsedFormatted => _elapsed.ToString(@"hh\:mm\:ss");

        public double MicLevel
        {
            get => _micLevel;
            set { _micLevel = Math.Clamp(value, 0.0, 1.0); OnPropertyChanged(); }
        }

        /// Human-readable label for the status badge.
        public string StatusLabel => IsRecording ? "● RECORDING ACTIVE" : "○ STOPPED";

        // ─── Constructor ──────────────────────────────────────────────

        public RecordingSessionViewModel()
        {
            _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _elapsedTimer.Tick += (_, _) => Elapsed = DateTime.Now - _sessionStart;
        }

        // ─── Commands ────────────────────────────────────────────────

        public void StartRecording(string? sessionName = null)
        {
            if (IsRecording) return;
            if (sessionName is not null) SessionName = sessionName;
            _sessionStart = DateTime.Now;
            Elapsed = TimeSpan.Zero;
            IsRecording = true;
            _elapsedTimer.Start();
        }

        public void StopRecording()
        {
            if (!IsRecording) return;
            _elapsedTimer.Stop();
            IsRecording = false;
        }

        // ─── INotifyPropertyChanged ──────────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void Dispose()
        {
            _elapsedTimer.Stop();
        }
    }
}
