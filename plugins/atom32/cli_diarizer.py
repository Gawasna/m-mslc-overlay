import os
import sys
import time
import argparse
import queue
import threading
import wave
import numpy as np
import pyaudiowpatch as pyaudio
from diarizer import SileroVAD, CamPlusExtractor, SpeakerClusteringEngine

# Configure paths
MODELS_DIR = os.path.join(os.path.dirname(__file__), "models")
VAD_MODEL = os.path.join(MODELS_DIR, "silero_vad.onnx")
CAMPPLUS_MODEL = os.path.join(MODELS_DIR, "campplus.onnx")
LOG_FILE = os.path.join(os.path.dirname(__file__), "cli_diarizer_test.log")

class CLIDiarizer:
    def __init__(self, device_idx, duration=25, threshold=0.5, expected_speakers=2, dist_threshold=0.50, min_speech_duration=1.2, max_speech_duration=15.0):
        self.device_idx = device_idx
        self.duration = duration
        self.threshold = threshold
        self.expected_speakers = expected_speakers
        self.dist_threshold = dist_threshold
        self.min_speech_duration = min_speech_duration
        self.max_speech_duration = max_speech_duration
        
        self.sample_rate = 16000
        self.chunk_size = 512
        
        # Open log file
        self.log_file = open(LOG_FILE, "w", encoding="utf-8")
        self.log("[INIT] Initializing CLI Diarizer with 30s Rolling Buffer & Multi-Thread Pipeline...")
        self.log(f"[CONFIG] Device Index: {device_idx}")
        self.log(f"[CONFIG] Duration: {duration}s | VAD Threshold: {threshold} | Expected Speakers: {expected_speakers} | Min Speech Duration: {min_speech_duration}s | Max Speech Duration: {max_speech_duration}s")
        
        # Load models
        try:
            self.vad = SileroVAD(VAD_MODEL)
            self.extractor = CamPlusExtractor(CAMPPLUS_MODEL)
            self.log("[INIT] ONNX Models loaded successfully.")
        except Exception as e:
            self.log(f"[ERROR] Failed to load ONNX models: {e}")
            raise e
            
        # Pipeline communication queues
        self.chunk_queue = queue.Queue()
        self.segment_queue = queue.Queue()
        
        # Diarizer state & Feature registry
        self.is_running = False
        self.segment_registry = []  # List of dicts: {'start': s, 'end': e, 'embedding': emb, 'uuid': str}
        self.clustering_engine = SpeakerClusteringEngine()
        
        # Rolling audio buffer (holds max 30 seconds of raw audio)
        self.rolling_buffer = np.zeros(0, dtype=np.float32)
        self.total_processed_samples = 0
        self.rolling_buffer_max_samples = 30 * self.sample_rate # 30 seconds
        self.callback_resample_buffer = np.zeros(0, dtype=np.float32)
        
        # PyAudio components
        self.p = None
        self.stream = None
        
        # Threads
        self.vad_thread = None
        self.embedding_thread = None

    def log(self, message):
        timestamp = time.strftime("%Y-%m-%d %H:%M:%S")
        log_str = f"[{timestamp}] {message}"
        print(log_str)
        self.log_file.write(log_str + "\n")
        self.log_file.flush()

    def run(self):
        self.is_running = True
        self.log("[RUN] Starting multi-thread audio pipeline using pyaudiowpatch...")
        
        # Start Threads
        self.vad_thread = threading.Thread(target=self.vad_processing_loop, daemon=True)
        self.embedding_thread = threading.Thread(target=self.embedding_processing_loop, daemon=True)
        
        self.vad_thread.start()
        self.embedding_thread.start()
        
        # Initialize PyAudio
        self.p = pyaudio.PyAudio()
        try:
            dev_info = self.p.get_device_info_by_index(self.device_idx)
            samplerate = int(dev_info['defaultSampleRate'])
            channels = int(loopback_channels) if (loopback_channels := dev_info.get('maxInputChannels', 1)) > 0 else 1
            self.log(f"[RUN] Selected device: {dev_info['name']} | Native Samplerate: {samplerate}Hz | Channels: {channels}")
        except Exception as e:
            self.log(f"[FATAL] Failed to query device info: {e}")
            self.stop()
            return False

        # Open debug WAV file
        try:
            self.wav_file = wave.open("cli_loopback_debug.wav", "wb")
            self.wav_file.setnchannels(1)
            self.wav_file.setsampwidth(2) # 16-bit
            self.wav_file.setframerate(self.sample_rate) # 16000Hz
        except Exception as e:
            self.log(f"[WARNING] Failed to open debug WAV file: {e}")
            self.wav_file = None

        self.callback_resample_buffer = np.zeros(0, dtype=np.float32)

        # PyAudio callback function
        def pyaudio_callback(in_data, frame_count, time_info, status):
            if status:
                self.log(f"[WARNING] pyaudiowpatch status: {status}")
            
            # Convert bytes buffer to float32 numpy array
            chunk = np.frombuffer(in_data, dtype=np.float32)
            
            # Convert multi-channel to mono (take first channel)
            if channels > 1:
                chunk = chunk.reshape(-1, channels)[:, 0]
            
            # Resample to 16000Hz using numpy linear interpolation if needed
            if samplerate != self.sample_rate:
                duration = len(chunk) / samplerate
                num_target_samples = int(duration * self.sample_rate)
                x_orig = np.linspace(0, duration, len(chunk))
                x_target = np.linspace(0, duration, num_target_samples)
                chunk_resampled = np.interp(x_target, x_orig, chunk)
            else:
                chunk_resampled = chunk
                
            # Write raw captured audio to debug WAV
            if hasattr(self, 'wav_file') and self.wav_file:
                try:
                    audio_int16 = (np.clip(chunk_resampled, -1.0, 1.0) * 32767.0).astype(np.int16)
                    self.wav_file.writeframes(audio_int16.tobytes())
                except Exception:
                    pass
                
            # Accumulate and group into precise 512-sample chunks for VAD ONNX
            self.callback_resample_buffer = np.append(self.callback_resample_buffer, chunk_resampled)
            while len(self.callback_resample_buffer) >= self.chunk_size:
                chunk_vad = self.callback_resample_buffer[:self.chunk_size].copy()
                self.callback_resample_buffer = self.callback_resample_buffer[self.chunk_size:]
                self.chunk_queue.put(chunk_vad.reshape(1, -1))
                
            return (None, pyaudio.paContinue)

        try:
            self.stream = self.p.open(
                format=pyaudio.paFloat32,
                channels=channels,
                rate=samplerate,
                input=True,
                input_device_index=self.device_idx,
                frames_per_buffer=self.chunk_size,
                stream_callback=pyaudio_callback
            )
            
            self.stream.start_stream()
            self.log(f"[RUN] Recording active for {self.duration} seconds...")
            
            # Sleep to let recording run
            time.sleep(self.duration)
                
        except Exception as e:
            self.log(f"[FATAL] Audio InputStream error: {e}")
            self.stop()
            return False
            
        self.log("[RUN] Recording duration finished. Stopping pipeline...")
        self.stop()
        return True

    def vad_processing_loop(self):
        """
        Stage 1: Process raw audio chunks from callback queue, manage rolling buffer,
        run VAD, and push speech segments to Segment Queue.
        """
        sr = self.sample_rate
        chunk_size = self.chunk_size
        threshold = self.threshold
        
        min_speech_chunks = int(self.min_speech_duration * sr / chunk_size)
        min_silence_chunks = int(400 / (chunk_size / sr * 1000))
        
        is_speech = False
        start_chunk = 0
        silence_counter = 0
        chunk_index = 0
        
        while self.is_running or not self.chunk_queue.empty():
            try:
                chunk_data = self.chunk_queue.get(timeout=0.1)
            except queue.Empty:
                continue
                
            # Flatten chunk data
            chunk_flat = chunk_data.squeeze()
            
            # Maintain 30s rolling buffer
            self.rolling_buffer = np.append(self.rolling_buffer, chunk_flat)
            self.total_processed_samples += len(chunk_flat)
            
            if len(self.rolling_buffer) > self.rolling_buffer_max_samples:
                self.rolling_buffer = self.rolling_buffer[-self.rolling_buffer_max_samples:]
                
            # Run VAD on chunk
            prob = self.vad(chunk_data)
            
            # Periodic debug log
            if chunk_index % 30 == 0:
                self.log(f"[DEBUG VAD] Chunk {chunk_index} | VAD Prob: {prob:.4f} | Registry size: {len(self.segment_registry)}")
                
            if prob >= threshold:
                if not is_speech:
                    is_speech = True
                    start_chunk = chunk_index
                    self.log(f"[VAD STATE] Speech started at {start_chunk * chunk_size / sr:.2f}s")
                silence_counter = 0
            else:
                if is_speech:
                    silence_counter += 1
                    if silence_counter >= min_silence_chunks:
                        # Segment ended naturally
                        end_chunk = chunk_index - silence_counter + 1
                        duration_chunks = end_chunk - start_chunk
                        
                        if duration_chunks >= min_speech_chunks:
                            self.extract_and_queue_segment(start_chunk, end_chunk)
                            
                        is_speech = False
                        
            # Force split segment if it exceeds maximum duration limit
            if is_speech:
                current_duration = (chunk_index - start_chunk) * chunk_size / sr
                if current_duration >= self.max_speech_duration:
                    end_chunk = chunk_index
                    self.extract_and_queue_segment(start_chunk, end_chunk)
                    
                    # Start a new segment immediately
                    start_chunk = chunk_index + 1
                    silence_counter = 0
                    
            chunk_index += 1

    def extract_and_queue_segment(self, start_chunk, end_chunk):
        """
        Extracts speech segment raw audio from rolling buffer using relative indices
        and pushes it to Segment Queue for embedding extraction.
        """
        sr = self.sample_rate
        chunk_size = self.chunk_size
        
        start_sample_abs = start_chunk * chunk_size
        end_sample_abs = end_chunk * chunk_size
        
        # Calculate indices relative to the current rolling buffer
        first_sample_abs = self.total_processed_samples - len(self.rolling_buffer)
        start_idx_rel = start_sample_abs - first_sample_abs
        end_idx_rel = end_sample_abs - first_sample_abs
        
        # Ensure indices are within the rolling buffer
        if start_idx_rel < 0:
            self.log(f"[WARNING] Segment start sample {start_sample_abs} was discarded from 30s rolling buffer. Capping start index.")
            start_idx_rel = 0
            
        if end_idx_rel > len(self.rolling_buffer):
            end_idx_rel = len(self.rolling_buffer)
            
        segment_audio = self.rolling_buffer[start_idx_rel:end_idx_rel].copy()
        
        start_sec = start_sample_abs / sr
        end_sec = end_sample_abs / sr
        
        self.log(f"[VAD STATE] Pushing speech segment [{start_sec:.2f}s - {end_sec:.2f}s] (Size: {len(segment_audio)} samples) to Segment Queue.")
        self.segment_queue.put({
            'start': start_sec,
            'end': end_sec,
            'audio': segment_audio
        })

    def embedding_processing_loop(self):
        """
        Stage 2: Process segments from Segment Queue, run CAM++ speaker embedding ONNX,
        register features, and run clustering to update timeline.
        """
        while self.is_running or not self.segment_queue.empty():
            try:
                seg_task = self.segment_queue.get(timeout=0.1)
            except queue.Empty:
                continue
                
            start_sec = seg_task['start']
            end_sec = seg_task['end']
            audio_data = seg_task['audio']
            
            # Extract embedding using CAM++ ONNX
            self.log(f"[EMBEDDING] Extracting speaker embedding for segment [{start_sec:.2f}s - {end_sec:.2f}s]...")
            start_time = time.time()
            emb = self.extractor(audio_data)
            duration_ms = (time.time() - start_time) * 1000
            
            if np.allclose(emb, 0):
                self.log(f"[EMBEDDING] [WARNING] Null embedding extracted for segment [{start_sec:.2f}s - {end_sec:.2f}s]. Skipping.")
                continue
                
            self.log(f"[EMBEDDING] Feature extracted in {duration_ms:.2f} ms.")
            
            # Store feature vector in Registry (No raw audio stored here!)
            self.segment_registry.append({
                'start': start_sec,
                'end': end_sec,
                'embedding': emb
            })
            
            # Run clustering on the entire feature registry
            self.run_clustering()

    def run_clustering(self):
        if not self.segment_registry:
            return
            
        self.log(f"[CLUSTERING] Re-clustering {len(self.segment_registry)} segments in Registry...")
        
        # Run clustering engine
        res = self.clustering_engine.process(self.segment_registry, self.expected_speakers)
        self.segment_registry = res['segment_registry']
        
        self.log("=== SPEAKER DIARIZATION TIMELINE ===")
        for i, seg in enumerate(self.segment_registry):
            self.log(f"  Segment {i:02d}: [{seg['start']:05.2f}s - {seg['end']:05.2f}s] -> {seg['uuid']}")
        self.log("=====================================")

    def stop(self):
        self.is_running = False
        
        # Close PyAudio stream safely
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
                self.log("[STOP] Saved loopback audio capture to: cli_loopback_debug.wav")
            except Exception:
                pass
            self.wav_file = None
            
        if self.p:
            try:
                self.p.terminate()
            except Exception:
                pass
            self.p = None
            
        # Wait for threads to finish processing remaining queues
        if self.vad_thread:
            self.vad_thread.join(timeout=1.0)
        if self.embedding_thread:
            self.embedding_thread.join(timeout=2.0)
            
        self.log("[STOP] Pipeline stopped.")
        self.log("SESSION_COMPLETE")
        self.close()

    def close(self):
        if self.log_file:
            self.log_file.close()

def main():
    parser = argparse.ArgumentParser(description="ATOM32 CLI Diarizer with 30s Rolling Buffer & Multi-Thread Pipeline")
    parser.add_argument("--device", type=int, required=True, help="Audio input device index")
    parser.add_argument("--duration", type=int, default=25, help="Recording duration in seconds")
    parser.add_argument("--threshold", type=float, default=0.5, help="VAD probability threshold")
    parser.add_argument("--speakers", type=int, default=2, help="Expected speakers limit")
    parser.add_argument("--min_speech", type=float, default=1.2, help="Min speech duration in seconds")
    parser.add_argument("--max_speech", type=float, default=15.0, help="Max speech duration in seconds")
    args = parser.parse_args()
    
    diarizer = CLIDiarizer(
        device_idx=args.device,
        duration=args.duration,
        threshold=args.threshold,
        expected_speakers=args.speakers,
        min_speech_duration=args.min_speech,
        max_speech_duration=args.max_speech
    )
    diarizer.run()

if __name__ == "__main__":
    main()
