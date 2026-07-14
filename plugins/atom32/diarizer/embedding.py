import numpy as np
import onnxruntime as ort
from .fbank import compute_fbank

class CamPlusExtractor:
    def __init__(self, model_path):
        opts = ort.SessionOptions()
        opts.log_severity_level = 3
        opts.intra_op_num_threads = 1
        opts.inter_op_num_threads = 1
        self.session = ort.InferenceSession(model_path, sess_options=opts)
        
    def __call__(self, audio_segment):
        """
        Extracts 192-dim speaker embedding from a raw audio segment.
        """
        fbank = compute_fbank(audio_segment)
        if len(fbank) == 0:
            return np.zeros(192, dtype=np.float32)
            
        # Add batch dimension [1, num_frames, 80]
        fbank_tensor = np.expand_dims(fbank, axis=0).astype(np.float32)
        
        # Run CAM++
        outputs = self.session.run(
            ['output'],
            {'input': fbank_tensor}
        )
        
        emb = outputs[0][0]
        # L2 Normalize the embedding vector
        norm = np.linalg.norm(emb)
        if norm > 1e-6:
            emb = emb / norm
            
        return emb
