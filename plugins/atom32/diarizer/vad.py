import numpy as np
import onnxruntime as ort

class SileroVAD:
    def __init__(self, model_path):
        # Disable verbose logging in ONNX Runtime
        opts = ort.SessionOptions()
        opts.log_severity_level = 3
        opts.intra_op_num_threads = 1
        opts.inter_op_num_threads = 1
        self.session = ort.InferenceSession(model_path, sess_options=opts)
        self.reset()
        
    def reset(self):
        self._state = np.zeros((2, 1, 128), dtype=np.float32)
        self._context = np.zeros((1, 64), dtype=np.float32)
        
    def __call__(self, chunk_samples):
        """
        Processes a chunk of 512 samples at 16kHz.
        Returns speech probability.
        """
        # Support various input formats ([512], [1, 512], [512, 1])
        if len(chunk_samples.shape) == 2:
            if chunk_samples.shape[0] == 1:
                chunk_samples = chunk_samples[0]
            elif chunk_samples.shape[1] == 1:
                chunk_samples = chunk_samples[:, 0]
                
        # Now chunk_samples is 1D, expand to [1, chunk_size]
        chunk_samples = np.expand_dims(chunk_samples, axis=0)
            
        # Prepend context to the input
        x = np.concatenate([self._context, chunk_samples], axis=1).astype(np.float32)
        
        sr_tensor = np.array(16000, dtype=np.int64)
        
        # Run model
        outputs = self.session.run(
            ['output', 'stateN'],
            {
                'input': x,
                'state': self._state,
                'sr': sr_tensor
            }
        )
        
        prob = outputs[0][0, 0]
        self._state = outputs[1]
        
        # Save the last 64 samples of x as the context for the next call
        self._context = x[:, -64:]
        return prob
