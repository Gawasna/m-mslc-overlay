import numpy as np
from sklearn.cluster import SpectralClustering
from .vad import SileroVAD
from .embedding import CamPlusExtractor

class LocalSpeakerDiarizer:
    def __init__(self, vad_model_path, campplus_model_path):
        self.vad = SileroVAD(vad_model_path)
        self.extractor = CamPlusExtractor(campplus_model_path)
        
    def segment_speech(self, audio, threshold=0.5, min_speech_ms=250, min_silence_ms=300):
        """
        Detects active speech segments (start_sec, end_sec) in the audio.
        """
        self.vad.reset()
        chunk_size = 512
        sr = 16000
        
        # Convert ms limits to chunks
        min_speech_chunks = int(min_speech_ms / (chunk_size / sr * 1000))
        min_silence_chunks = int(min_silence_ms / (chunk_size / sr * 1000))
        
        num_samples = len(audio)
        num_chunks = num_samples // chunk_size
        
        segments = []
        is_speech = False
        start_chunk = 0
        silence_counter = 0
        
        for i in range(num_chunks):
            start_idx = i * chunk_size
            end_idx = start_idx + chunk_size
            chunk = audio[start_idx:end_idx]
            
            prob = self.vad(chunk)
            
            if prob >= threshold:
                if not is_speech:
                    is_speech = True
                    start_chunk = i
                silence_counter = 0
            else:
                if is_speech:
                    silence_counter += 1
                    if silence_counter >= min_silence_chunks:
                        # End of speech segment
                        end_chunk = i - silence_counter + 1
                        duration_chunks = end_chunk - start_chunk
                        if duration_chunks >= min_speech_chunks:
                            segments.append((start_chunk * chunk_size, end_chunk * chunk_size))
                        is_speech = False
                        
        # Handle trailing segment
        if is_speech:
            duration_chunks = num_chunks - start_chunk
            if duration_chunks >= min_speech_chunks:
                segments.append((start_chunk * chunk_size, num_chunks * chunk_size))
                
        # Convert sample indexes to seconds
        sec_segments = [(start / sr, end / sr) for start, end in segments]
        return sec_segments

    def run(self, audio, n_clusters=2, threshold=0.5):
        """
        Executes complete diarization: VAD segmentation -> Embedding -> Clustering.
        Returns a list of dicts: [{'start': s, 'end': e, 'speaker': id}]
        """
        # 1. Segment Speech
        segments = self.segment_speech(audio, threshold=threshold)
        if not segments:
            print("No speech detected.")
            return []
            
        print(f"Detected {len(segments)} speech segments.")
        
        # 2. Extract Embeddings
        embeddings = []
        valid_segments = []
        sr = 16000
        
        for start_sec, end_sec in segments:
            start_sample = int(start_sec * sr)
            end_sample = int(end_sec * sr)
            segment_audio = audio[start_sample:end_sample]
            
            emb = self.extractor(segment_audio)
            # Skip invalid segment embeddings (all zeros)
            if not np.allclose(emb, 0):
                embeddings.append(emb)
                valid_segments.append((start_sec, end_sec))
                
        if not embeddings:
            return []
            
        embeddings = np.array(embeddings)
        
        # 3. Spectral Clustering
        # If there's only 1 segment, or we request 1 cluster, assign all to Speaker 0
        if len(valid_segments) == 1 or n_clusters == 1:
            labels = np.zeros(len(valid_segments), dtype=int)
        else:
            # Clip n_clusters to the number of segments
            n_clusters = min(n_clusters, len(valid_segments))
            
            # Compute cosine similarity matrix
            # embeddings are already L2 normalized, so similarity is dot product
            similarity_matrix = np.dot(embeddings, embeddings.T)
            # Convert similarity (-1 to 1) to affinity (0 to 1)
            affinity_matrix = (similarity_matrix + 1.0) / 2.0
            
            # Apply Spectral Clustering
            sc = SpectralClustering(
                n_clusters=n_clusters, 
                affinity='precomputed',
                random_state=42
            )
            labels = sc.fit_predict(affinity_matrix)
            
        # 4. Format Results
        results = []
        for (start_sec, end_sec), label in zip(valid_segments, labels):
            results.append({
                'start': round(start_sec, 2),
                'end': round(end_sec, 2),
                'speaker': int(label)
            })
            
        return results
