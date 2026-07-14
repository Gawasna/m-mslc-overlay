import os
import time
import numpy as np
import soundfile as sf
from diarizer import LocalSpeakerDiarizer, load_audio

def generate_mixed_audio(spk1_path, spk2_path, out_path, segment_len_sec=4.0, silence_len_sec=1.0, sr=16000):
    """
    Creates a mixed audio file by alternating segments of spk1 and spk2 with silence gaps.
    Allows testing speaker diarization with known ground truth speaker turns.
    """
    audio1 = load_audio(spk1_path, target_sr=sr)
    audio2 = load_audio(spk2_path, target_sr=sr)
    
    seg_samples = int(segment_len_sec * sr)
    silence_samples = int(silence_len_sec * sr)
    silence = np.zeros(silence_samples, dtype=np.float32)
    
    mixed_audio = []
    ground_truth = []
    current_time = 0.0
    
    # Spk1 Segment 1
    s1 = audio1[0:seg_samples]
    mixed_audio.append(s1)
    ground_truth.append((current_time, current_time + segment_len_sec, "Female (Spk1)"))
    current_time += segment_len_sec
    
    # Silence gap
    mixed_audio.append(silence)
    current_time += silence_len_sec
    
    # Spk2 Segment 1
    s2 = audio2[0:seg_samples]
    mixed_audio.append(s2)
    ground_truth.append((current_time, current_time + segment_len_sec, "Male (Spk2)"))
    current_time += segment_len_sec
    
    # Silence gap
    mixed_audio.append(silence)
    current_time += silence_len_sec
    
    # Spk1 Segment 2
    s3 = audio1[seg_samples:seg_samples * 2]
    mixed_audio.append(s3)
    ground_truth.append((current_time, current_time + segment_len_sec, "Female (Spk1)"))
    current_time += segment_len_sec
    
    # Silence gap
    mixed_audio.append(silence)
    current_time += silence_len_sec
    
    # Spk2 Segment 2
    s4 = audio2[seg_samples:seg_samples * 2]
    mixed_audio.append(s4)
    ground_truth.append((current_time, current_time + segment_len_sec, "Male (Spk2)"))
    current_time += segment_len_sec
    
    # Concatenate and save
    full_audio = np.concatenate(mixed_audio)
    sf.write(out_path, full_audio, sr)
    print(f"Generated test mixed audio file: {out_path}")
    print("Ground Truth Speaker Turns:")
    for start, end, label in ground_truth:
        print(f"  [{start:.1f}s - {end:.1f}s] {label}")
        
    return full_audio, ground_truth

def run_diarization_demo():
    models_dir = os.path.join(os.path.dirname(__file__), "models")
    spk1_path = os.path.join(models_dir, "spk1.wav")
    spk2_path = os.path.join(models_dir, "spk2.wav")
    mixed_path = os.path.join(models_dir, "mixed.wav")
    
    vad_model = os.path.join(models_dir, "silero_vad.onnx")
    campplus_model = os.path.join(models_dir, "campplus.onnx")
    
    print("\n=======================================================")
    print("STEP 1: Generating Mixed Multi-Speaker Audio File")
    print("=======================================================")
    audio, ground_truth = generate_mixed_audio(spk1_path, spk2_path, mixed_path, segment_len_sec=4.0)
    
    print("\n=======================================================")
    print("STEP 2: Initializing Local Speaker Diarizer Pipeline")
    print("=======================================================")
    start_init = time.time()
    diarizer = LocalSpeakerDiarizer(vad_model, campplus_model)
    init_time = time.time() - start_init
    print(f"Pipeline initialized in {init_time * 1000:.2f} ms")
    
    print("\n=======================================================")
    print("STEP 3: Running Diarization (VAD + Embedding + Cluster)")
    print("=======================================================")
    start_proc = time.time()
    
    # We expect 2 clusters (spk1 and spk2)
    results = diarizer.run(audio, n_clusters=2)
    
    total_time = time.time() - start_proc
    print(f"Diarization completed in {total_time * 1000:.2f} ms (Audio duration: {len(audio)/16000:.2f}s)")
    print(f"Real-time Factor (RTF): {total_time / (len(audio)/16000):.4f}x")
    
    print("\n=======================================================")
    print("STEP 4: Output Diarization Timeline")
    print("=======================================================")
    for res in results:
        print(f"  [{res['start']:.2f}s - {res['end']:.2f}s] Speaker {res['speaker']}")
        
    print("\n=======================================================")
    print("STEP 5: Evaluating Diarization Accuracy")
    print("=======================================================")
    # Evaluate matches
    # Since clustering labels (0, 1) are arbitrary, let's map them to ground truth
    # Ground Truth layout with 1s silence:
    # Spk1 is active in [0-4] and [10-14] seconds.
    # Spk2 is active in [5-9] and [15-19] seconds.
    spk1_labels = []
    spk2_labels = []
    
    for res in results:
        mid_point = (res['start'] + res['end']) / 2.0
        if (0.0 <= mid_point <= 4.0) or (10.0 <= mid_point <= 14.0):
            spk1_labels.append(res['speaker'])
        elif (5.0 <= mid_point <= 9.0) or (15.0 <= mid_point <= 19.0):
            spk2_labels.append(res['speaker'])
            
    # Voting for Speaker 1 label
    if spk1_labels:
        spk1_mapped = max(set(spk1_labels), key=spk1_labels.count)
        spk1_acc = spk1_labels.count(spk1_mapped) / len(spk1_labels) * 100
    else:
        spk1_mapped, spk1_acc = -1, 0.0
        
    # Voting for Speaker 2 label
    if spk2_labels:
        spk2_mapped = max(set(spk2_labels), key=spk2_labels.count)
        spk2_acc = spk2_labels.count(spk2_mapped) / len(spk2_labels) * 100
    else:
        spk2_mapped, spk2_acc = -1, 0.0
        
    print(f"Clustering Label Mapping:")
    print(f"  - Ground Truth Female (Spk1) mapped to Cluster {spk1_mapped} (Segment Accuracy: {spk1_acc:.1f}%)")
    print(f"  - Ground Truth Male (Spk2) mapped to Cluster {spk2_mapped} (Segment Accuracy: {spk2_acc:.1f}%)")
    
    if spk1_mapped == spk2_mapped and spk1_mapped != -1:
        print("Result: Failed to distinguish speakers (both mapped to same cluster).")
    else:
        print("Result: Success! Distinct speakers separated correctly.")

if __name__ == "__main__":
    run_diarization_demo()
