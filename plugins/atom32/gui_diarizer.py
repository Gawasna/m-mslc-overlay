import os
import sys
import warnings
warnings.filterwarnings('ignore', category=FutureWarning)
import time
import queue
import threading
import wave
import tkinter as tk
from tkinter import ttk, messagebox, scrolledtext, simpledialog
import numpy as np
import pyaudiowpatch as pyaudio
import sounddevice as sd
import matplotlib
matplotlib.use("TkAgg")
from matplotlib.backends.backend_tkagg import FigureCanvasTkAgg, NavigationToolbar2Tk
import matplotlib.pyplot as plt
import psutil
from diarizer import SileroVAD, CamPlusExtractor, SpeakerClusteringEngine
import uuid
from diarizer.voice_profile_db import VoiceProfileDB, MAX_POOL_SIZE
import socket
import json
from collections import deque
import ctypes

class CommitSignalBuffer:
    def __init__(self, max_size: int = 300):
        self._ring = deque(maxlen=max_size)
        self._lock = threading.Lock()
        self._last_known_lc_state = 'UNKNOWN'
        self._last_heartbeat_ts = 0.0

    def push(self, signal: dict) -> None:
        if signal.get('type') == 'state':
            self._last_known_lc_state = signal.get('lc_state', 'UNKNOWN')
            self._last_heartbeat_ts = time.monotonic()
            return
        with self._lock:
            self._ring.append(signal)

    def get_window(self, start_sec: float, end_sec: float, epoch_offset_sec: float = 0.0, tolerance_sec: float = 0.2) -> list:
        with self._lock:
            snapshot = list(self._ring)
        result = []
        t_start = start_sec - tolerance_sec
        t_end = end_sec + tolerance_sec
        for item in snapshot:
            if 'acoustic_end_ms' in item:
                t = item['acoustic_end_ms'] / 1000.0 + epoch_offset_sec
                if t_start <= t <= t_end:
                    result.append(item)
        return result

    def get_state_at(self, tolerance_ms: float = 300.0) -> str:
        age_ms = (time.monotonic() - self._last_heartbeat_ts) * 1000
        if age_ms > tolerance_ms:
            return 'UNKNOWN'
        return self._last_known_lc_state

class UdpSignalReceiver(threading.Thread):
    def __init__(self, buffer: CommitSignalBuffer, port: int = 47832):
        super().__init__(daemon=True)
        self._buf = buffer
        self._port = port
        self._sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            self._sock.bind(('127.0.0.1', port))
            self._sock.settimeout(1.0)
        except Exception as e:
            print(f"[UDP] Failed to bind to port {port}: {e}")
            self._sock = None

    def run(self):
        if not self._sock:
            return
        print(f"[UDP] Listening for LiveCaption signals on port {self._port}...")
        while True:
            try:
                data, _ = self._sock.recvfrom(512)
                signal = json.loads(data.decode('utf-8'))
                if signal.get('type') == 'sync':
                    # Can be used to adjust offset if needed
                    pass
                elif signal.get('type') == 'commit':
                    print(f"[LiveCaption] Received commit signal: {signal.get('reason')} - {signal.get('acoustic_end_ms')} ms")
                self._buf.push(signal)
            except socket.timeout:
                continue
            except Exception:
                pass  # Ignore malformed packets

class RealTimeDiarizerGUI:
    def __init__(self, root):
        self.root = root
        self.root.title("ATOM32: Local Offline Speaker Diarization Real-Time Testbench (Rolling Buffer)")
        self.root.geometry("900x600")
        self.root.minsize(800, 500)
        
        # Paths to ONNX models
        models_dir = os.path.join(os.path.dirname(__file__), "models")
        self.vad_model_path = os.path.join(models_dir, "silero_vad.onnx")
        self.campplus_model_path = os.path.join(models_dir, "campplus.onnx")
        
        # Verify models exist
        if not os.path.exists(self.vad_model_path) or not os.path.exists(self.campplus_model_path):
            messagebox.showerror(
                "Missing Models", 
                "Model ONNX files not found. Please run 'python model_downloader.py' first."
            )
            self.root.destroy()
            return
            
        # Audio constants
        self.sample_rate = 16000
        self.chunk_size = 512
        
        # Queues for 2-stage pipeline
        self.chunk_queue = queue.Queue()
        self.segment_queue = queue.Queue()
        
        # State variables
        self.is_recording = False
        self.stream = None
        self.vad_thread = None
        self.embedding_thread = None
        
        # Diarizer core models
        self.vad = None
        self.extractor = None
        
        # Rolling buffer state
        self.rolling_buffer = np.zeros(0, dtype=np.float32)
        self.total_processed_samples = 0
        self.rolling_buffer_max_samples = 30 * self.sample_rate # 30 seconds
        
        # Speaker registry
        self.segment_registry = []
        self.persistent_id_counter = 1
        self.speaker_profiles_data = {}
        self.speaker_colors = ["#4A90E2", "#F5A623", "#7ED321", "#BD10E0", "#9013FE", "#50E3C2"]
        self.clustering_engine = SpeakerClusteringEngine()
        
        models_dir = os.path.join(os.path.dirname(__file__), "models")
        self.db = VoiceProfileDB(os.path.join(models_dir, 'voice_profiles.db'))
        self.session_id = str(uuid.uuid4())
        self.uid_to_profile_id: dict = {}
        self._known_profiles = []
        self.manual_merges = {}
        
        # LiveCaption UDP Sync
        self.lc_buffer = CommitSignalBuffer()
        self.lc_receiver = UdpSignalReceiver(self.lc_buffer, 47832)
        self.lc_receiver.start()
        self._lc_epoch_offset = 0.0
        self._recording_start_ticks = 0
        
        # Audio level tracking for UI meter
        self.current_vol = 0.0
        self.stream_samplerate = 16000
        self.stream_channels = 1
        self.monitor_channels = 1
        self.callback_resample_buffer = np.zeros(0, dtype=np.float32)
        self.monitor_stream = None
        self.p = None
        self.monitor_p = None
        
        # Setup GUI layout
        self.setup_ui()
        self.refresh_devices()
        
        # Handle window closure
        self.root.protocol("WM_DELETE_WINDOW", self.on_closing)
        
        # Start background volume meter loop
        self.update_meter_loop()

    def setup_ui(self):
        # Configure theme
        self.root.title("Real-Time Diarization Engine")
        self.root.geometry("1400x800")
        
        # Enforce a consistent light theme to avoid OS-level dark theme clashes
        style = ttk.Style()
        if "clam" in style.theme_names():
            style.theme_use("clam")
            
        main_pane = ttk.PanedWindow(self.root, orient=tk.HORIZONTAL)
        main_pane.pack(fill=tk.BOTH, expand=True, padx=10, pady=10)
        
        # Left Panel: Controls
        left_frame = ttk.LabelFrame(main_pane, text=" Controls & Configuration ", padding=10)
        main_pane.add(left_frame, weight=1)
        
        # Center Panel: Timeline & Profiles (Vertical Pane)
        center_pane = ttk.PanedWindow(main_pane, orient=tk.VERTICAL)
        main_pane.add(center_pane, weight=2)
        
        timeline_frame = ttk.LabelFrame(center_pane, text=" Real-Time Speaker Timeline ", padding=10)
        center_pane.add(timeline_frame, weight=3)
        
        profiles_frame = ttk.LabelFrame(center_pane, text=" Speaker Roster & Audio Reference ", padding=10)
        center_pane.add(profiles_frame, weight=1)
        
        # Right Panel: Cluster Visualization (Fish-Eye)
        vis_frame = ttk.LabelFrame(main_pane, text=" Cluster Visualization (Fish-Eye Map) ", padding=10)
        main_pane.add(vis_frame, weight=2)
        
        # --- LEFT PANEL CONTENTS ---
        ttk.Label(left_frame, text="Audio Input Device (WASAPI Loopback/Microphone):").pack(anchor=tk.W, pady=(0, 2))
        self.device_combo = ttk.Combobox(left_frame, state="readonly", width=40)
        self.device_combo.pack(fill=tk.X, pady=(0, 5))
        self.device_combo.bind("<<ComboboxSelected>>", self.on_device_selected)
        
        btn_refresh = ttk.Button(left_frame, text="Refresh Audio Devices", command=self.refresh_devices)
        btn_refresh.pack(fill=tk.X, pady=(0, 15))
        
        # Threshold Slider
        ttk.Label(left_frame, text="VAD Sensitivity Threshold:").pack(anchor=tk.W, pady=(0, 2))
        self.threshold_var = tk.DoubleVar(value=0.5)
        scale_frame = ttk.Frame(left_frame)
        scale_frame.pack(fill=tk.X, pady=(0, 15))
        lbl_thresh = ttk.Label(scale_frame, text="0.50", width=5)
        lbl_thresh.pack(side=tk.RIGHT)
        scale_thresh = ttk.Scale(
            scale_frame, 
            from_=0.1, to=0.9, 
            variable=self.threshold_var, 
            command=lambda v: lbl_thresh.config(text=f"{float(v):.2f}")
        )
        scale_thresh.pack(side=tk.LEFT, fill=tk.X, expand=True)
        
        # Speakers Limit Combobox (Force fixed speakers or Auto-estimate)
        ttk.Label(left_frame, text="Expected Speakers Count:").pack(anchor=tk.W, pady=(0, 2))
        self.speakers_limit_combo = ttk.Combobox(left_frame, state="readonly", values=["Auto-Estimate", "1", "2", "3", "4"])
        self.speakers_limit_combo.current(0) # Default to Auto-Estimate for flexibility
        self.speakers_limit_combo.pack(fill=tk.X, pady=(0, 15))
        
        # Min Speech Duration Spinner
        ttk.Label(left_frame, text="Min Speech Duration (seconds):").pack(anchor=tk.W, pady=(0, 2))
        self.min_speech_var = tk.DoubleVar(value=1.2)
        spin_min = ttk.Spinbox(left_frame, from_=0.5, to=5.0, increment=0.1, textvariable=self.min_speech_var, width=10)
        spin_min.pack(anchor=tk.W, pady=(0, 15))
        
        # Control Buttons
        self.btn_start = ttk.Button(left_frame, text="▶ Start Diarization", command=self.start_diarization)
        self.btn_start.pack(fill=tk.X, pady=(0, 5))
        
        self.btn_stop = ttk.Button(left_frame, text="■ Stop", command=self.stop_diarization, state=tk.DISABLED)
        self.btn_stop.pack(fill=tk.X, pady=(0, 15))
        
        self.enable_viz_var = tk.BooleanVar(value=True)
        chk_viz = ttk.Checkbutton(left_frame, text="Enable Visualizer (High CPU)", variable=self.enable_viz_var)
        chk_viz.pack(anchor=tk.W, pady=(0, 20))
        
        # Audio Signal Meter
        ttk.Label(left_frame, text="Audio Signal Level:").pack(anchor=tk.W, pady=(0, 2))
        self.meter_canvas = tk.Canvas(left_frame, height=20, bg="#E0E0E0", highlightthickness=0)
        self.meter_canvas.pack(fill=tk.X, pady=(0, 5))
        self.meter_bar = self.meter_canvas.create_rectangle(0, 0, 0, 20, fill="#2ecc71")
        
        self.lbl_sys_res = ttk.Label(left_frame, text="CPU: 0% | RAM: 0 MB", font=("Helvetica", 9, "bold"), foreground="#4A90E2")
        self.lbl_sys_res.pack(anchor=tk.W, pady=(5, 0))
        
        self.lbl_status = ttk.Label(left_frame, text="Status: Idle", font=("Helvetica", 9, "italic"))
        self.lbl_status.pack(anchor=tk.W, pady=(5, 0))
        
        self.lbl_lc_status = ttk.Label(left_frame, text="LiveCaption: DISCONNECTED", font=("Helvetica", 9, "bold"), foreground="#95a5a6")
        self.lbl_lc_status.pack(anchor=tk.W, pady=(5, 0))
        
        # --- CENTER PANEL: TIMELINE ---
        self.txt_timeline = scrolledtext.ScrolledText(
            timeline_frame, 
            wrap=tk.WORD, 
            font=("Consolas", 10), 
            bg="#FFFFFF", 
            fg="#000000"
        )
        self.txt_timeline.pack(fill=tk.BOTH, expand=True)
        
        # Setup ScrolledText tags for colors
        for i, color in enumerate(self.speaker_colors):
            self.txt_timeline.tag_config(f"spk_{i}", foreground=color, font=("Consolas", 10, "bold"))
        self.txt_timeline.tag_config("timestamp", foreground="#888888")
        self.txt_timeline.tag_config("info", foreground="#A0A0A0", font=("Helvetica", 9, "italic"))
        
        # --- CENTER PANEL: SPEAKER ROSTER & PLAYBACK ---
        listbox_frame = ttk.Frame(profiles_frame)
        listbox_frame.pack(fill=tk.BOTH, expand=True)
        
        columns = ("UID", "Segments", "Identity")
        self.profile_treeview = ttk.Treeview(listbox_frame, columns=columns, show="headings", height=4)
        self.profile_treeview.heading("UID", text="Session UID")
        self.profile_treeview.column("UID", width=100)
        self.profile_treeview.heading("Segments", text="Segments")
        self.profile_treeview.column("Segments", width=80)
        self.profile_treeview.heading("Identity", text="Identity")
        self.profile_treeview.column("Identity", width=250)
        self.profile_treeview.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        
        scrollbar = ttk.Scrollbar(listbox_frame, orient="vertical", command=self.profile_treeview.yview)
        scrollbar.pack(side=tk.LEFT, fill=tk.Y)
        self.profile_treeview.config(yscrollcommand=scrollbar.set)
        
        self.profile_treeview.bind("<Double-1>", self.on_treeview_double_click)
        
        btn_frame = ttk.Frame(profiles_frame)
        btn_frame.pack(fill=tk.X, pady=(5, 0))
        
        btn_play = ttk.Button(btn_frame, text="▶ Play Best Audio", command=self.play_reference_audio)
        btn_play.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=(0, 5))
        
        btn_merge = ttk.Button(btn_frame, text="⭢ Merge Selected", command=self.merge_selected_profiles)
        btn_merge.pack(side=tk.LEFT, fill=tk.X, expand=True)
        
        # --- RIGHT PANEL: VISUALIZATION MATPLOTLIB ---
        self.fig, (self.ax_mds, self.ax_matrix) = plt.subplots(1, 2, figsize=(10, 4))
        self.fig.patch.set_facecolor('#F0F0F0')
        self.ax_mds.set_facecolor('#FFFFFF')
        self.ax_matrix.set_facecolor('#FFFFFF')
        self.fig.tight_layout(pad=2.0)
        
        self.canvas_plot = FigureCanvasTkAgg(self.fig, master=vis_frame)
        self.canvas_plot.get_tk_widget().pack(fill=tk.BOTH, expand=True)
        
        toolbar_frame = ttk.Frame(vis_frame)
        toolbar_frame.pack(fill=tk.X, side=tk.BOTTOM)
        toolbar = NavigationToolbar2Tk(self.canvas_plot, toolbar_frame)
        toolbar.update()
        
        # Merge suggestions Frame
        self.suggestions_frame = ttk.LabelFrame(vis_frame, text="Merge Suggestions")
        self.suggestions_frame.pack(fill=tk.BOTH, expand=False, pady=(10, 0))
        self.suggestion_listbox = tk.Listbox(self.suggestions_frame, height=3)
        self.suggestion_listbox.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        sbtn_frame = ttk.Frame(self.suggestions_frame)
        sbtn_frame.pack(side=tk.RIGHT, fill=tk.Y, padx=(5, 0))
        ttk.Button(sbtn_frame, text="Merge", command=self.on_merge_suggestion).pack(fill=tk.X, pady=(0, 2))
        ttk.Button(sbtn_frame, text="Dismiss", command=self.on_dismiss_suggestion).pack(fill=tk.X)
        self._current_suggestions = []
        
        # Context Menu for Rollback
        self.timeline_menu = tk.Menu(self.txt_timeline, tearoff=0)
        self.timeline_menu.add_command(label="Reassign segment...", command=self.on_reassign_segment)
        self.txt_timeline.bind("<Button-3>", self.show_timeline_menu)

    def show_timeline_menu(self, event):
        try:
            index = self.txt_timeline.index(f"@{event.x},{event.y}")
            self._clicked_timeline_index = index
            self.timeline_menu.post(event.x_root, event.y_root)
        except Exception:
            pass

    def _pid_to_uid(self, pid: str) -> str | None:
        for uid, p in self.uid_to_profile_id.items():
            if p == pid:
                return uid
        return None

    def _live_reassign(self, old_pid: str, new_pid: str, start_sec: float, end_sec: float):
        TOLERANCE = 0.5
        moved = []
        remaining = []
        
        old_embs = self._session_embeddings.get(self._pid_to_uid(old_pid), [])
        
        for item in old_embs:
            if abs(item['start'] - start_sec) < TOLERANCE and abs(item['end'] - end_sec) < TOLERANCE:
                item['session_id'] = self.session_id
                moved.append(item)
            else:
                remaining.append(item)
                
        if not moved:
            messagebox.showinfo("Reassign", "Không tìm thấy embedding tại timestamp này trong session hiện tại.")
            return
            
        old_uid = self._pid_to_uid(old_pid)
        new_uid = self._pid_to_uid(new_pid)
        if old_uid:
            self._session_embeddings[old_uid] = remaining
        if new_uid:
            if new_uid not in self._session_embeddings:
                self._session_embeddings[new_uid] = deque(maxlen=MAX_POOL_SIZE)
            self._session_embeddings[new_uid].extend(moved)
            
        for item in moved:
            self.db.add_embedding(new_pid, item['embedding'], 
                                  session_id=item['session_id'],
                                  start_sec=item['start'], end_sec=item['end'])
                                  
        self.db._remove_embeddings_by_timestamp(old_pid, start_sec, end_sec, TOLERANCE)
        messagebox.showinfo("Reassign", f"Đã reassign {len(moved)} embedding(s).")

    def on_reassign_segment(self):
        if not hasattr(self, '_clicked_timeline_index'): return
        idx = self._clicked_timeline_index
        line_text = self.txt_timeline.get(f"{idx} linestart", f"{idx} lineend")
        import re
        m = re.match(r'\[(\d+):(\d+\.\d+)\s*-\s*(\d+):(\d+\.\d+)\]\s+(Speaker-\d+|[0-9a-fA-F-]+)', line_text)
        if not m:
            messagebox.showinfo("Error", "Could not parse segment time from this line.")
            return
        m1, s1, m2, s2, uid = m.groups()
        start_sec = int(m1) * 60 + float(s1)
        end_sec = int(m2) * 60 + float(s2)
        
        if uid not in self.uid_to_profile_id:
            messagebox.showinfo("Info", "Segment belongs to an unsaved profile. Wait until session saves or manually label it.")
            return
            
        old_pid = self.uid_to_profile_id[uid]
        profiles = self.db.load_all_active()
        choices = [f"{p['profile_id'][:8]} - {p['display_name']}" for p in profiles if p['profile_id'] != old_pid]
        
        if not choices:
            messagebox.showinfo("Info", "No other profiles to reassign to.")
            return
            
        new_val = simpledialog.askstring("Reassign", f"Reassign from {uid} to:\n(Type prefix to select)\n" + "\n".join(choices))
        if not new_val: return
        
        new_pid = None
        for p in profiles:
            if new_val.startswith(p['profile_id'][:8]):
                new_pid = p['profile_id']
                break
                
        if new_pid:
            if getattr(self, 'is_recording', False):
                self._live_reassign(old_pid, new_pid, start_sec, end_sec)
            else:
                self.db.reassign_segment(self.session_id, start_sec, end_sec, old_pid, new_pid)
                messagebox.showinfo("Success", "Segment reassigned successfully in DB.")
            self.update_clustering()

    def refresh_merge_suggestions(self):
        self.suggestion_listbox.delete(0, tk.END)
        self._current_suggestions = self.db.get_merge_suggestions()
        for p1, n1, p2, n2, dist in self._current_suggestions:
            self.suggestion_listbox.insert(tk.END, f"{n1} <-> {n2} (dist={dist:.2f})")

    def on_merge_suggestion(self):
        sel = self.suggestion_listbox.curselection()
        if not sel: return
        p1, n1, p2, n2, dist = self._current_suggestions[sel[0]]
        if messagebox.askyesno("Confirm", f"Merge {n1} into {n2}?"):
            self.db.merge_profiles(p1, p2, self.session_id)
            self.refresh_merge_suggestions()
            self.update_clustering()
            
    def on_dismiss_suggestion(self):
        sel = self.suggestion_listbox.curselection()
        if not sel: return
        p1, n1, p2, n2, dist = self._current_suggestions[sel[0]]
        self.db.dismiss_merge_suggestion(p1, p2)
        self.refresh_merge_suggestions()

    def refresh_devices(self):
        try:
            p = pyaudio.PyAudio()
            wasapi_idx = None
            try:
                wasapi_idx = p.get_host_api_info_by_type(pyaudio.paWASAPI)['index']
            except Exception:
                pass
        except Exception as e:
            messagebox.showerror("Audio Device Error", f"Failed to initialize PyAudio: {e}")
            return
            
        self.device_list = []
        combobox_values = []
        
        try:
            device_count = p.get_device_count()
            for idx in range(device_count):
                dev = p.get_device_info_by_index(idx)
                host_api_info = p.get_host_api_info_by_index(dev['hostApi'])
                host_api_name = host_api_info['name']
                
                is_input = dev['maxInputChannels'] > 0
                is_loopback = dev.get('isLoopbackDevice', False)
                
                if is_input:
                    if is_loopback:
                        device_name = f"[{idx}] {dev['name']} (Windows WASAPI Loopback)"
                    else:
                        device_name = f"[{idx}] {dev['name']} ({host_api_name})"
                    self.device_list.append(idx)
                    combobox_values.append(device_name)
        except Exception as e:
            messagebox.showerror("Audio Device Error", f"Failed to list devices: {e}")
        finally:
            p.terminate()
            
        self.device_combo['values'] = combobox_values
        
        # Select default device (prioritize FxSound Loopback, then Stereo Mix)
        default_idx = 0
        for i, val in enumerate(combobox_values):
            if "fxsound" in val.lower() and "loopback" in val.lower():
                default_idx = i
                break
        else:
            for i, val in enumerate(combobox_values):
                if "stereo mix" in val.lower() and "mme" in val.lower():
                    default_idx = i
                    break
                elif "stereo mix" in val.lower() and "wasapi" in val.lower() and default_idx == 0:
                    default_idx = i
                elif "stereo mix" in val.lower() and default_idx == 0:
                    default_idx = i
                
        if combobox_values:
            self.device_combo.current(default_idx)
            self.start_monitoring(self.device_list[default_idx])

    def start_diarization(self):
        if self.is_recording:
            return
            
        self.stop_monitoring()
        
        sel_idx = self.device_combo.current()
        if sel_idx < 0:
            messagebox.showwarning("Warning", "Please select an audio input device.")
            return
        device_id = self.device_list[sel_idx]
        
        self.lbl_status.config(text="Status: Initializing ONNX models...")
        self.root.update_idletasks()
        
        try:
            self.vad = SileroVAD(self.vad_model_path)
            self.extractor = CamPlusExtractor(self.campplus_model_path)
        except Exception as e:
            messagebox.showerror("Model Load Error", f"Failed to initialize ONNX models: {e}")
            self.lbl_status.config(text="Status: Init failed")
            return
            
        # Reset state & queues
        self.rolling_buffer = np.zeros(0, dtype=np.float32)
        self.total_processed_samples = 0
        self.segment_registry.clear()
        
        # Open debug WAV file to verify exact captured sound quality
        try:
            self.wav_file = wave.open("loopback_debug.wav", "wb")
            self.wav_file.setnchannels(1)
            self.wav_file.setsampwidth(2) # 16-bit PCM
            self.wav_file.setframerate(self.sample_rate) # 16000Hz resampled rate
        except Exception as e:
            print(f"Failed to open debug WAV file: {e}", file=sys.stderr)
            self.wav_file = None
            
        while not self.chunk_queue.empty():
            try:
                self.chunk_queue.get_nowait()
            except queue.Empty:
                break
                
        while not self.segment_queue.empty():
            try:
                self.segment_queue.get_nowait()
            except queue.Empty:
                break
                
        self.txt_timeline.delete(1.0, tk.END)
        self.txt_timeline.insert(tk.END, ">>> Real-Time Speaker Diarization active (30s Rolling Buffer)...\n\n", "info")
        
        # Start sounddevice stream
        self.is_recording = True
        self.btn_start.config(state=tk.DISABLED)
        self.btn_stop.config(state=tk.NORMAL)
        self.device_combo.config(state=tk.DISABLED)
        self.lbl_status.config(text="Status: Listening...")
        
        self.uid_to_profile_id.clear()
        self.manual_merges.clear()
        self._segs_since_recognition = {}
        self._session_embeddings = {}
        self._session_emb_ids = set()
        self._known_profiles = self.db.load_all_active()
        print(f"[DB] Loaded {len(self._known_profiles)} known profiles from DB.")
        
        # Sync epoch for LiveCaption
        self._recording_start_ticks = ctypes.windll.kernel32.GetTickCount64()
        self._lc_epoch_offset = - (self._recording_start_ticks / 1000.0)
        
        # Open main stream safely using pyaudiowpatch
        try:
            self.stream, self.stream_samplerate, self.stream_channels = self.open_stream_safely(
                device_id=device_id,
                target_sr=self.sample_rate,
                callback=self.audio_callback
            )
            self.stream.start_stream()
        except Exception as e:
            messagebox.showerror("Audio Error", f"Failed to start audio stream:\n{e}")
            self.stop_diarization()
            return
            
        # Start pipeline worker threads
        self.vad_thread = threading.Thread(target=self.vad_processing_loop, daemon=True)
        self.embedding_thread = threading.Thread(target=self.embedding_processing_loop, daemon=True)
        
        self.vad_thread.start()
        self.embedding_thread.start()

    def audio_callback(self, in_data, frame_count, time_info, status):
        # Convert bytes buffer to float32 numpy array
        indata = np.frombuffer(in_data, dtype=np.float32)
        
        # Convert multi-channel signal to mono (take first channel)
        if self.stream_channels > 1:
            audio_mono = indata.reshape(-1, self.stream_channels)[:, 0]
        else:
            audio_mono = indata
            
        # Calculate volume level for GUI meter based on mono signal
        self.current_vol = np.sqrt(np.mean(audio_mono**2))
        
        # Resample mono signal to 16000Hz using numpy linear interpolation if needed
        if self.stream_samplerate != self.sample_rate:
            duration = len(audio_mono) / self.stream_samplerate
            num_target_samples = int(duration * self.sample_rate)
            x_orig = np.linspace(0, duration, len(audio_mono))
            x_target = np.linspace(0, duration, num_target_samples)
            audio_mono = np.interp(x_target, x_orig, audio_mono)
            
        # Write to debug WAV file (clip to safe ranges before converting to 16-bit PCM)
        if hasattr(self, 'wav_file') and self.wav_file:
            try:
                audio_int16 = (np.clip(audio_mono, -1.0, 1.0) * 32767.0).astype(np.int16)
                self.wav_file.writeframes(audio_int16.tobytes())
            except Exception as e:
                print(f"Error writing to WAV file: {e}", file=sys.stderr)
            
        # Accumulate resampled audio and group into precise 512-sample chunks for VAD ONNX
        self.callback_resample_buffer = np.append(self.callback_resample_buffer, audio_mono)
        while len(self.callback_resample_buffer) >= self.chunk_size:
            chunk = self.callback_resample_buffer[:self.chunk_size].copy()
            self.callback_resample_buffer = self.callback_resample_buffer[self.chunk_size:]
            # Reshaping to (1, 512) for the ONNX model
            self.chunk_queue.put(chunk.reshape(1, -1))

    def vad_processing_loop(self):
        """
        Stage 1 Thread: Runs Silero VAD, maintains 30s rolling buffer,
        and pushes raw audio of speech segments to Segment Queue.
        """
        sr = self.sample_rate
        chunk_size = self.chunk_size
        
        while self.is_recording:
            try:
                chunk_data = self.chunk_queue.get(timeout=0.1)
            except queue.Empty:
                continue
                
            threshold = self.threshold_var.get()
            min_speech_duration = self.min_speech_var.get()
            
            min_speech_chunks = int(min_speech_duration * sr / chunk_size)
            min_silence_chunks = int(400 / (chunk_size / sr * 1000))
            max_speech_duration = 15.0 # Max segment length limit
            
            # Initializing local static variables for the loop state
            if not hasattr(self, '_vad_is_speech'):
                self._vad_is_speech = False
                self._vad_start_chunk = 0
                self._vad_silence_counter = 0
                self._vad_chunk_index = 0
                
            chunk_flat = chunk_data.squeeze()
            
            # Maintain 30s rolling buffer
            self.rolling_buffer = np.append(self.rolling_buffer, chunk_flat)
            self.total_processed_samples += len(chunk_flat)
            
            if len(self.rolling_buffer) > self.rolling_buffer_max_samples:
                self.rolling_buffer = self.rolling_buffer[-self.rolling_buffer_max_samples:]
                
            # Run VAD on chunk
            prob = self.vad(chunk_data)
            
            if prob >= threshold:
                if not self._vad_is_speech:
                    self._vad_is_speech = True
                    self._vad_start_chunk = self._vad_chunk_index
                self._vad_silence_counter = 0
            else:
                if self._vad_is_speech:
                    self._vad_silence_counter += 1
                    if self._vad_silence_counter >= min_silence_chunks:
                        # Segment ended naturally
                        end_chunk = self._vad_chunk_index - self._vad_silence_counter + 1
                        duration_chunks = end_chunk - self._vad_start_chunk
                        
                        if duration_chunks >= min_speech_chunks:
                            self.extract_and_queue_segment(self._vad_start_chunk, end_chunk)
                            
                        self._vad_is_speech = False
                        
            # Force split segment if it exceeds maximum duration
            if self._vad_is_speech:
                current_duration = (self._vad_chunk_index - self._vad_start_chunk) * chunk_size / sr
                if current_duration >= max_speech_duration:
                    end_chunk = self._vad_chunk_index
                    self.extract_and_queue_segment(self._vad_start_chunk, end_chunk)
                    
                    self._vad_start_chunk = self._vad_chunk_index + 1
                    self._vad_silence_counter = 0
                    
            self._vad_chunk_index += 1
            
        # Clean up local static variables when stream stops
        if hasattr(self, '_vad_is_speech'):
            del self._vad_is_speech
            del self._vad_start_chunk
            del self._vad_silence_counter
            del self._vad_chunk_index

    def extract_and_queue_segment(self, start_chunk, end_chunk):
        sr = self.sample_rate
        chunk_size = self.chunk_size
        
        start_sample_abs = start_chunk * chunk_size
        end_sample_abs = end_chunk * chunk_size
        
        first_sample_abs = self.total_processed_samples - len(self.rolling_buffer)
        start_idx_rel = start_sample_abs - first_sample_abs
        end_idx_rel = end_sample_abs - first_sample_abs
        
        if start_idx_rel < 0:
            start_idx_rel = 0
        if end_idx_rel > len(self.rolling_buffer):
            end_idx_rel = len(self.rolling_buffer)
            
        segment_audio = self.rolling_buffer[start_idx_rel:end_idx_rel].copy()
        
        start_sec = start_sample_abs / sr
        end_sec = end_sample_abs / sr
        
        self.segment_queue.put({
            'start': start_sec,
            'end': end_sec,
            'audio': segment_audio
        })

    def embedding_processing_loop(self):
        """
        Stage 2 Thread: Consumes speech segments, extracts embeddings,
        runs Agglomerative Clustering on Registry, and invokes GUI updates.
        """
        while self.is_recording:
            try:
                seg_task = self.segment_queue.get(timeout=0.1)
            except queue.Empty:
                continue
                
            start_sec = seg_task['start']
            end_sec = seg_task['end']
            audio_data = seg_task['audio']
            
            # Extract embedding using CAM++ ONNX
            emb = self.extractor(audio_data)
            
            if np.allclose(emb, 0):
                continue
                
            # Store in registry (raw audio now saved for reference playback!)
            self.segment_registry.append({
                'start': start_sec,
                'end': end_sec,
                'embedding': emb,
                'raw_audio': audio_data
            })
            
            # Prevent O(N^3) CPU explosion by maintaining a sliding window of max 300 segments (~10 mins)
            if len(self.segment_registry) > 300:
                self.segment_registry.pop(0)
            
            # Run clustering
            self.update_clustering()
            
            # Tích lũy embeddings theo uid vào session buffer
            if getattr(self, '_session_embeddings', None) is None:
                self._session_embeddings = {}
            if not hasattr(self, '_session_emb_ids'):
                self._session_emb_ids = set()
                
            for seg in self.segment_registry:
                uid = seg.get('uuid')
                emb_id = id(seg['embedding'])
                if uid and emb_id not in self._session_emb_ids:
                    if uid not in self._session_embeddings:
                        self._session_embeddings[uid] = deque(maxlen=MAX_POOL_SIZE)
                    self._session_embeddings[uid].append({
                        'embedding': seg['embedding'],
                        'session_id': self.session_id,
                        'start': seg['start'],
                        'end': seg['end']
                    })
                    self._session_emb_ids.add(emb_id)
                    if uid in self.uid_to_profile_id:
                        self.db.add_embedding(self.uid_to_profile_id[uid], seg['embedding'], session_id=self.session_id, start_sec=seg['start'], end_sec=seg['end'])
            
            # Cross-session recognition pass
            MIN_SEGS_BEFORE_RECOGNIZE = 3
            REEVAL_INTERVAL = 10
            
            if not hasattr(self, '_segs_since_recognition'):
                self._segs_since_recognition = {}
                
            active_uids = {seg['uuid'] for seg in self.segment_registry if 'uuid' in seg}
            
            for uid, data in self.speaker_profiles_data.items():
                if uid in self.uid_to_profile_id:
                    segs_since_assign = self._segs_since_recognition.get(uid, 0) + 1
                    self._segs_since_recognition[uid] = segs_since_assign
                    if segs_since_assign < REEVAL_INTERVAL:
                        continue
                    # Đủ evidence -> re-evaluate
                    self._segs_since_recognition[uid] = 0
                
                min_segs = MIN_SEGS_BEFORE_RECOGNIZE
                if hasattr(self, 'lc_buffer') and uid in self._session_embeddings and self._session_embeddings[uid]:
                    first_seg = self._session_embeddings[uid][0]
                    t_start = first_seg['start'] - 0.5
                    t_end = first_seg['start'] + 0.5
                    signals = self.lc_buffer.get_window(t_start, t_end, self._lc_epoch_offset)
                    has_hard_or_merge = any(s.get('reason') == 'HardCommit' or s.get('was_merged') for s in signals)
                    if has_hard_or_merge:
                        min_segs = 1
                
                if data['count'] < min_segs:
                    continue
                    
                MIN_EMB_FOR_CONSISTENCY = 3
                INTRA_CONSISTENCY_RATIO = 0.50
                
                uid_pool = [item['embedding'] for item in self._session_embeddings.get(uid, [])]
                
                if len(uid_pool) >= MIN_EMB_FOR_CONSISTENCY:
                    new_pid, new_dist, consistency = self.db.recognize_from_pool(uid_pool, consistency_ratio=INTRA_CONSISTENCY_RATIO)
                    log_suffix = f"consistency={consistency:.0%}"
                else:
                    centroid = data['centroid']
                    new_pid, new_dist = self.db.recognize(centroid)
                    log_suffix = "centroid-fallback"
                
                if uid in self.uid_to_profile_id:
                    if new_pid and new_pid != self.uid_to_profile_id[uid]:
                        print(f"[DB] REASSIGN CANDIDATE: {uid} was {self.uid_to_profile_id[uid]}, now matches {new_pid} (dist={new_dist:.3f}, {log_suffix})")
                else:
                    if new_pid:
                        self.uid_to_profile_id[uid] = new_pid
                        display = self.db.get_metadata(new_pid, 'display_name') or new_pid[:8]
                        print(f"[DB] RECOGNIZED: {uid} -> profile {display} (dist={new_dist:.3f}, {log_suffix})")
                        for item in self._session_embeddings[uid]:
                            self.db.add_embedding(new_pid, item['embedding'], session_id=item['session_id'], start_sec=item['start'], end_sec=item['end'])
                    else:
                        print(f"[DB] NEW SPEAKER: {uid} (closest dist={new_dist:.3f}, {log_suffix}, no match)")

    def update_clustering(self):
        if not self.segment_registry:
            return
        limit_str = self.speakers_limit_combo.get()
        expected_speakers = 0 if limit_str == "Auto-Estimate" else int(limit_str)
        
        result = self.clustering_engine.process(self.segment_registry, expected_speakers, lc_gate_func=self._apply_livecaption_gate)
        
        self.segment_registry = result['segment_registry']
        self.speaker_profiles_data = result['speaker_profiles_data']
        self.persistent_id_counter = result['persistent_id_counter']
        
        # Post-process manual merges
        for source_uid, target_uid in self.manual_merges.items():
            if source_uid in self.speaker_profiles_data and target_uid in self.speaker_profiles_data:
                self.speaker_profiles_data[target_uid]['count'] += self.speaker_profiles_data[source_uid]['count']
                del self.speaker_profiles_data[source_uid]
                
        # Aliasing in timeline_lines
        processed_timeline = []
        for start, end, uid in result['timeline_lines']:
            if uid in self.manual_merges:
                uid = self.manual_merges[uid]
            processed_timeline.append((start, end, uid))
        
        self.root.after(0, self.refresh_timeline_ui, 
                        processed_timeline, 
                        self.speaker_profiles_data, 
                        result['vis_nodes'], 
                        [], 
                        result['natural_variance'], 
                        result['dynamic_threshold'], 
                        result['c_dist'], 
                        result['uids'])

    def refresh_timeline_ui(self, timeline_lines, profiles_data, vis_nodes, suspicious_merges, natural_variance=0.1, dynamic_threshold=0.15, c_dist=None, uids=None):
        try:
            cpu_usage = psutil.cpu_percent()
            ram_mb = psutil.Process().memory_info().rss / (1024 * 1024)
            self.lbl_sys_res.config(text=f"CPU: {cpu_usage:.1f}% | RAM: {ram_mb:.1f} MB")
        except Exception:
            pass
            
        self.txt_timeline.delete(1.0, tk.END)
        self.txt_timeline.insert(tk.END, ">>> Real-Time Speaker Diarization active (30s Rolling Buffer):\n\n", "info")
        
        TIMELINE_MIN_SEGS = 2
        for start, end, uuid_str in timeline_lines:
            seg_count = profiles_data.get(uuid_str, {}).get('count', 0)
            is_recognized = uuid_str in self.uid_to_profile_id
            
            if seg_count < TIMELINE_MIN_SEGS and not is_recognized:
                continue
                
            start_m, start_s = divmod(start, 60)
            end_m, end_s = divmod(end, 60)
            
            ts_str = f"[{int(start_m):02d}:{start_s:05.2f} - {int(end_m):02d}:{end_s:05.2f}] "
            self.txt_timeline.insert(tk.END, ts_str, "timestamp")
            
            # Print Identity alongside UID if available
            identity_str = ""
            if uuid_str in self.uid_to_profile_id:
                pid = self.uid_to_profile_id[uuid_str]
                name = self.db.get_metadata(pid, 'display_name')
                if name:
                    identity_str = f" ({name})"
            
            try:
                # Extract number from "Speaker-01" to keep consistent color
                color_idx = int(uuid_str.split('-')[1]) % len(self.speaker_colors)
            except:
                color_idx = 0
            
            self.txt_timeline.insert(tk.END, uuid_str, f"spk_{color_idx}")
            self.txt_timeline.insert(tk.END, f"{identity_str}\n", "info")
            
        self.txt_timeline.see(tk.END)
        
        # Update Profiles Roster Treeview
        selected_items = self.profile_treeview.selection()
        selected_uids = [self.profile_treeview.item(item, "values")[0] for item in selected_items]
        
        self.profile_treeview.delete(*self.profile_treeview.get_children())
        for uid, data in profiles_data.items():
            if uid in self.uid_to_profile_id:
                pid = self.uid_to_profile_id[uid]
                name = self.db.get_metadata(pid, 'display_name')
                
                seg_count = 0
                import sqlite3
                from contextlib import closing
                try:
                    with closing(sqlite3.connect(self.db.db_path)) as conn:
                        c = conn.cursor()
                        c.execute('SELECT segment_count FROM speaker_profiles WHERE profile_id=?', (pid,))
                        r = c.fetchone()
                        if r: seg_count = r[0]
                except Exception:
                    pass
                
                if name:
                    identity = name
                    tier = ""
                else:
                    identity = pid[:8]
                    is_dangling = self.db.get_metadata(pid, 'dangling') == 'true'
                    
                    if is_dangling:
                        tier = " [? dangling]"
                    elif seg_count >= 5:
                        tier = ""
                    elif seg_count >= 3:
                        tier = " [~tentative]"
                    else:
                        tier = " [?]"
                        
                identity_disp = f"{identity}{tier}"
            else:
                identity_disp = "[NEW]"
                
            item_id = self.profile_treeview.insert("", tk.END, values=(uid, data['count'], identity_disp))
            if uid in selected_uids:
                self.profile_treeview.selection_add(item_id)
            
        # Update Visualization using Matplotlib
        if not hasattr(self, 'enable_viz_var') or not self.enable_viz_var.get():
            return
            
        self.ax_mds.clear()
        self.ax_matrix.clear()
        
        self.ax_mds.set_title(f"MDS Cluster Map (Var: {natural_variance:.3f}, Thresh: {dynamic_threshold:.3f})", color="black", fontsize=9)
        self.ax_matrix.set_title("Inter-cluster Distance Matrix", color="black", fontsize=9)
        
        for ax in [self.ax_mds, self.ax_matrix]:
            ax.tick_params(colors='black', labelsize=8)
            for spine in ax.spines.values():
                spine.set_color('#DDDDDD')
                
        # --- Plot MDS Scatter ---
        uid_to_pos = {}
        for node in vis_nodes:
            uid_to_pos[node['uid']] = (node['x'], node['y'])
            
        # Draw suspicious edges
        for u1, u2, dist in suspicious_merges:
            if u1 in uid_to_pos and u2 in uid_to_pos:
                x1, y1 = uid_to_pos[u1]
                x2, y2 = uid_to_pos[u2]
                self.ax_mds.plot([x1, x2], [y1, y2], color="red", linestyle="--", linewidth=1.5, zorder=1)
                self.ax_mds.text((x1+x2)/2, (y1+y2)/2, f"d={dist:.2f}", color="red", fontsize=8, ha="center", zorder=2)
                
        # Draw nodes
        for node in vis_nodes:
            cx, cy = node['x'], node['y']
            # Scale point size based on count
            size = min(800, 100 + node['count'] * 20)
            try:
                color_idx = int(node['uid'].split('-')[1]) % len(self.speaker_colors)
            except:
                color_idx = 0
            color = self.speaker_colors[color_idx]
            
            self.ax_mds.scatter(cx, cy, s=size, c=color, edgecolors='black', linewidth=1, zorder=5)
            self.ax_mds.text(cx, cy, f"{node['uid']}\nN={node['count']}", color='black', 
                             fontsize=8, ha='center', va='center', fontweight='bold', zorder=10)
                             
        self.ax_mds.set_xlim(-0.1, 1.1)
        self.ax_mds.set_ylim(-0.1, 1.1)
        self.ax_mds.set_xticks([])
        self.ax_mds.set_yticks([])
        
        # --- Plot Distance Matrix Heatmap ---
        if c_dist is not None and uids is not None and len(uids) >= 2:
            im = self.ax_matrix.imshow(c_dist, cmap='viridis', vmin=0.0, vmax=1.0)
            self.ax_matrix.set_xticks(range(len(uids)))
            self.ax_matrix.set_yticks(range(len(uids)))
            self.ax_matrix.set_xticklabels(uids, rotation=45, ha='right', fontsize=7)
            self.ax_matrix.set_yticklabels(uids, fontsize=7)
            
            # Annotate distances inside heatmap
            for i in range(len(uids)):
                for j in range(len(uids)):
                    text_color = "black" if c_dist[i, j] > 0.5 else "white"
                    self.ax_matrix.text(j, i, f"{c_dist[i, j]:.2f}", ha="center", va="center", color=text_color, fontsize=7)
        else:
            self.ax_matrix.text(0.5, 0.5, "Not enough speakers", color='white', ha='center', va='center', transform=self.ax_matrix.transAxes)
            self.ax_matrix.set_xticks([])
            self.ax_matrix.set_yticks([])
            
        self.fig.tight_layout(pad=2.0)
        self.canvas_plot.draw()
        self.refresh_merge_suggestions()

    def on_treeview_double_click(self, event):
        selection = self.profile_treeview.selection()
        if not selection: return
        item = selection[0]
        uid = self.profile_treeview.item(item, "values")[0]
        
        if uid not in self.uid_to_profile_id:
            centroid = self.speaker_profiles_data[uid]['centroid']
            initial_embs = getattr(self, '_session_embeddings', {}).get(uid, [])
            new_id = self.db.create_profile(centroid, initial_embeddings=initial_embs)
            self.uid_to_profile_id[uid] = new_id
            
        pid = self.uid_to_profile_id[uid]
        current_name = self.db.get_metadata(pid, 'display_name')
        
        new_name = simpledialog.askstring("Label Speaker", f"Nhập tên cho {uid}:", initialvalue=current_name)
        if new_name is not None:
            self.db.set_display_name(pid, new_name.strip())
            self.db.set_metadata(pid, 'display_name', new_name.strip())
            try:
                self.db.set_user_confirmed(pid, True)
            except AttributeError:
                pass
            self.update_clustering()

    def merge_selected_profiles(self):
        selection = self.profile_treeview.selection()
        if len(selection) != 2:
            messagebox.showinfo("Merge", "Please select exactly 2 rows to merge.")
            return
            
        uid1 = self.profile_treeview.item(selection[0], "values")[0]
        uid2 = self.profile_treeview.item(selection[1], "values")[0]
        
        for uid in [uid1, uid2]:
            if uid not in self.uid_to_profile_id:
                centroid = self.speaker_profiles_data[uid]['centroid']
                initial_embs = getattr(self, '_session_embeddings', {}).get(uid, [])
                new_id = self.db.create_profile(centroid, initial_embeddings=initial_embs)
                self.uid_to_profile_id[uid] = new_id
                
        pid1 = self.uid_to_profile_id[uid1]
        pid2 = self.uid_to_profile_id[uid2]
        
        name1 = self.db.get_metadata(pid1, 'display_name') or pid1[:8]
        name2 = self.db.get_metadata(pid2, 'display_name') or pid2[:8]
        
        if messagebox.askyesno("Confirm Merge", f"Merge {uid1} ({name1}) into {uid2} ({name2})?"):
            self.db.merge_profiles(source_id=pid1, target_id=pid2, session_id=self.session_id)
            self.manual_merges[uid1] = uid2
            self.uid_to_profile_id[uid1] = pid2
            self.update_clustering()

    def play_reference_audio(self):
        selection = self.profile_treeview.selection()
        if not selection:
            messagebox.showinfo("Select", "Please select a speaker profile to play.")
            return
            
        uid = self.profile_treeview.item(selection[0], "values")[0]
        
        if hasattr(self, 'speaker_profiles_data') and uid in self.speaker_profiles_data:
            audio_data = self.speaker_profiles_data[uid]['best_audio']
            if audio_data is not None:
                threading.Thread(target=self._play_audio, args=(audio_data,), daemon=True).start()
                
    def _play_audio(self, audio_data):
        try:
            sd.play(audio_data, samplerate=self.sample_rate)
            sd.wait()
        except Exception as e:
            print(f"Error playing audio: {e}", file=sys.stderr)

    def stop_diarization(self):
        if not self.is_recording:
            return
            
        self.is_recording = False
        self.lbl_status.config(text="Status: Stopping stream...")
        self.root.update_idletasks()
        
        if self.stream:
            try:
                self.stream.stop_stream()
                self.stream.close()
            except Exception:
                pass
            self.stream = None
            
        if hasattr(self, 'wav_file') and self.wav_file:
            try:
                self.wav_file.close()
            except Exception as e:
                print(f"Failed to close WAV file: {e}", file=sys.stderr)
            self.wav_file = None
            self.txt_timeline.insert(tk.END, ">>> Saved loopback audio capture to: loopback_debug.wav\n(Open and play it to verify captured sound quality!)\n\n", "info")
            
        if self.p:
            try:
                self.p.terminate()
            except Exception:
                pass
            self.p = None
            
        # Join worker threads
        if self.vad_thread:
            self.vad_thread.join(timeout=3.0)
            self.vad_thread = None
        if self.embedding_thread:
            self.embedding_thread.join(timeout=3.0)
            self.embedding_thread = None
            
        self.btn_start.config(state=tk.NORMAL)
        self.btn_stop.config(state=tk.DISABLED)
        self.device_combo.config(state="readonly")
        self.lbl_status.config(text="Status: Saving... (background)")
        
        flush_snapshot = {
            'session_embeddings': dict(getattr(self, '_session_embeddings', {})),
            'speaker_profiles_data': dict(self.speaker_profiles_data),
            'uid_to_profile_id': dict(self.uid_to_profile_id),
            'session_id': self.session_id,
            'lc_buffer': getattr(self, 'lc_buffer', None),
            'lc_epoch_offset': getattr(self, '_lc_epoch_offset', 0.0),
        }
        
        def _flush_worker(snapshot):
            try:
                # Tạo profiles cho speakers chưa được label/recognized
                for uid, data in snapshot['speaker_profiles_data'].items():
                    if uid not in snapshot['uid_to_profile_id']:
                        embs = snapshot['session_embeddings'].get(uid, [])
                        pid = self.db.create_profile(data['centroid'], initial_embeddings=list(embs))
                        self.uid_to_profile_id[uid] = pid

                # Check dangling signals for TENTATIVE speakers
                if snapshot['lc_buffer']:
                    for uid, data in snapshot['speaker_profiles_data'].items():
                        if data['count'] < 5:  # TENTATIVE or SUSPECT
                            embs = snapshot['session_embeddings'].get(uid, [])
                            if not embs: continue
                            
                            is_all_dangling = True
                            for item in embs:
                                t_start = item['start'] - 0.2
                                t_end = item['end'] + 0.2
                                signals = snapshot['lc_buffer'].get_window(t_start, t_end, snapshot['lc_epoch_offset'])
                                if not signals or not all(s.get('is_dangling') for s in signals):
                                    is_all_dangling = False
                                    break
                                    
                            if is_all_dangling:
                                pid = self.uid_to_profile_id.get(uid)
                                if pid:
                                    self.db.set_metadata(pid, "dangling", "true")
                                    print(f"[LiveCaption] Flagged {pid} ({uid}) as SUSPECT (dangling noise).")

                # Flush toàn bộ dirty profiles xuống SQLite
                self.db.flush_to_db(snapshot['session_id'])
                print(f"[DB] Session flushed. uid_to_profile_id: {self.uid_to_profile_id}")
            except Exception as e:
                import traceback
                traceback.print_exc()
                print(f"[DB] Flush error: {e}", file=sys.stderr)
            finally:
                self.root.after(0, lambda: self.lbl_status.config(text="Status: Stopped (Idle)"))
                self.root.after(0, lambda: self.txt_timeline.insert(tk.END, "\n>>> Diarization session stopped.\n", "info"))
                
                sel_idx = self.device_combo.current()
                if sel_idx >= 0:
                    self.root.after(0, lambda: self.start_monitoring(self.device_list[sel_idx]))

        threading.Thread(target=_flush_worker, args=(flush_snapshot,), daemon=True).start()

    def on_device_selected(self, event):
        sel_idx = self.device_combo.current()
        if sel_idx >= 0:
            self.start_monitoring(self.device_list[sel_idx])

    def start_monitoring(self, device_id):
        self.stop_monitoring()
        if self.is_recording:
            return
            
        def monitor_callback(in_data, frame_count, time_info, status):
            indata = np.frombuffer(in_data, dtype=np.float32)
            if self.monitor_channels > 1:
                mono = indata.reshape(-1, self.monitor_channels)[:, 0]
            else:
                mono = indata
            self.current_vol = np.sqrt(np.mean(mono**2))
            return (None, pyaudio.paContinue)
            
        try:
            self.monitor_p = pyaudio.PyAudio()
            dev_info = self.monitor_p.get_device_info_by_index(device_id)
            sr = int(dev_info['defaultSampleRate'])
            ch = int(loopback_channels) if (loopback_channels := dev_info.get('maxInputChannels', 1)) > 0 else 1
            self.monitor_channels = ch
            
            blocksize = int(sr * 0.032)
            if blocksize <= 0:
                blocksize = 512
                
            self.monitor_stream = self.monitor_p.open(
                format=pyaudio.paFloat32,
                channels=ch,
                rate=sr,
                input=True,
                input_device_index=device_id,
                frames_per_buffer=blocksize,
                stream_callback=monitor_callback
            )
            self.monitor_stream.start_stream()
        except Exception as e:
            print(f"Error starting monitor stream: {e}", file=sys.stderr)
            self.stop_monitoring()

    def open_stream_safely(self, device_id, target_sr, callback):
        """
        Robustly attempts to open an InputStream by trying combinations of sample rates.
        Uses pyaudiowpatch capabilities to interface loopback endpoints cleanly.
        """
        time.sleep(0.1) # Yield time for PortAudio driver to release old streams
        
        self.p = pyaudio.PyAudio()
        try:
            dev_info = self.p.get_device_info_by_index(device_id)
            default_sr = int(dev_info['defaultSampleRate'])
            ch = int(loopback_channels) if (loopback_channels := dev_info.get('maxInputChannels', 1)) > 0 else 1
        except Exception as e:
            raise Exception(f"Failed to query device info: {e}")
            
        rate_candidates = [target_sr, default_sr, 48000, 44100, 16000]
        rate_candidates = list(dict.fromkeys([r for r in rate_candidates if r > 0]))
        
        def pyaudio_callback(in_data, frame_count, time_info, status):
            callback(in_data, frame_count, time_info, status)
            return (None, pyaudio.paContinue)
            
        last_error = ""
        for sr in rate_candidates:
            blocksize = int(sr * 0.032) # 32ms
            if blocksize <= 0:
                blocksize = 512
                
            try:
                stream = self.p.open(
                    format=pyaudio.paFloat32,
                    channels=ch,
                    rate=sr,
                    input=True,
                    input_device_index=device_id,
                    frames_per_buffer=blocksize,
                    stream_callback=pyaudio_callback
                )
                return stream, sr, ch
            except Exception as e:
                last_error = str(e)
                print(f"[DEBUG OPEN_STREAM] Failed combo: device={device_id}, sr={sr}, ch={ch} | Error: {e}", file=sys.stderr)
                continue
                
        raise Exception(f"All combinations failed. Last error: {last_error}. Rates tried: {rate_candidates}")

    def stop_monitoring(self):
        if self.monitor_stream:
            try:
                self.monitor_stream.stop_stream()
                self.monitor_stream.close()
            except Exception:
                pass
            self.monitor_stream = None
            
        if self.monitor_p:
            try:
                self.monitor_p.terminate()
            except Exception:
                pass
            self.monitor_p = None

    def update_meter_loop(self):
        if self.is_recording or self.monitor_stream:
            vol = min(1.0, self.current_vol * 15)
            width = self.meter_canvas.winfo_width()
            target_x = int(vol * width)
            
            coords = self.meter_canvas.coords(self.meter_bar)
            current_x = coords[2] if coords else 0
            new_x = current_x + (target_x - current_x) * 0.3
            
            self.meter_canvas.coords(self.meter_bar, 0, 0, new_x, 20)
            
            if vol < 0.6:
                color = "#2ecc71"
            elif vol < 0.85:
                color = "#f39c12"
            else:
                color = "#e74c3c"
            self.meter_canvas.itemconfig(self.meter_bar, fill=color)
        else:
            self.meter_canvas.coords(self.meter_bar, 0, 0, 0, 20)
            
        if hasattr(self, 'lc_buffer'):
            lc_state = self.lc_buffer.get_state_at(tolerance_ms=1500.0)
            if lc_state == 'UNKNOWN':
                self.lbl_lc_status.config(text="LiveCaption: DISCONNECTED", foreground="#95a5a6")
            else:
                color = "#2ecc71" if lc_state == "IDLE" else ("#e67e22" if lc_state == "PENDING" else "#9b59b6")
                self.lbl_lc_status.config(text=f"LiveCaption: {lc_state}", foreground=color)
            
        self.root.after(50, self.update_meter_loop)

    def _apply_livecaption_gate(self, seg_start: float, seg_end: float) -> str:
        if not hasattr(self, 'lc_buffer'):
            return 'allow'
            
        signals = self.lc_buffer.get_window(seg_start, seg_end, self._lc_epoch_offset)
        
        if not signals:
            state = self.lc_buffer.get_state_at()
            if state == 'PENDING':
                return 'suppress'
            return 'allow'

        has_hard_commit = any(s.get('reason') == 'HardCommit' for s in signals)
        all_dangling    = all(s.get('is_dangling') for s in signals)
        was_merged      = any(s.get('was_merged') for s in signals)
        
        action = 'allow'
        if all_dangling:
            action = 'suppress'
        elif has_hard_commit:
            action = 'reinforce'
        elif was_merged:
            action = 'reinforce'

        if action != 'allow':
            print(f"[LiveCaption] Gate decision at {seg_start:.2f}s - {seg_end:.2f}s: {action.upper()} (Signals: {len(signals)})")
        return action

    def on_closing(self):
        try:
            if self.is_recording:
                self.stop_diarization()
            self.stop_monitoring()
        except Exception:
            pass
        self.root.destroy()
        import os
        os._exit(0)

def main():
    root = tk.Tk()
    app = RealTimeDiarizerGUI(root)
    root.mainloop()

if __name__ == "__main__":
    main()
