import numpy as np
import soundfile as sf
import scipy.signal

def load_audio(file_path, target_sr=16000):
    """
    Loads audio file, downmixes to mono, and resamples to target_sr (16kHz).
    Returns a float32 numpy array with values in [-1.0, 1.0].
    """
    data, sr = sf.read(file_path, dtype='float32')
    
    # Downmix to mono if multi-channel
    if len(data.shape) > 1:
        data = np.mean(data, axis=1)
        
    # Resample if sample rate doesn't match
    if sr != target_sr:
        num_samples = int(len(data) * target_sr / sr)
        data = scipy.signal.resample(data, num_samples)
        
    return data.astype(np.float32)
