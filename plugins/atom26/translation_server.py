import os
import sys
import time
import torch # Nạp trước để tự động liên kết các CUDA DLLs trên Windows
from fastapi import FastAPI, HTTPException
from fastapi.responses import FileResponse
from pydantic import BaseModel
import uvicorn
import ctranslate2
from transformers import AutoTokenizer

app = FastAPI(title="ATOM26 Offline Translation Server", description="CTranslate2 NMT Inference Engine")

# Cấu hình đường dẫn mô hình mặc định
# Server sẽ ưu tiên tìm mô hình NLLB-600M trước (chất lượng cao), sau đó là OPUS-MT
DEFAULT_NLLB_PATH = "./models/nllb-600m-int8"
DEFAULT_OPUS_PATH = "./models/opus-en-vi-int8"

model_path = os.environ.get("MODEL_PATH", "")
if not model_path or not os.path.exists(model_path):
    if os.path.exists(DEFAULT_NLLB_PATH):
        model_path = DEFAULT_NLLB_PATH
    elif os.path.exists(DEFAULT_OPUS_PATH):
        model_path = DEFAULT_OPUS_PATH
    else:
        # Dự phòng tìm bất kỳ mô hình nào trong thư mục models/
        models_dir = "./models"
        if os.path.exists(models_dir):
            subdirs = [os.path.join(models_dir, d) for d in os.listdir(models_dir) if os.path.isdir(os.path.join(models_dir, d))]
            if subdirs:
                model_path = subdirs[0]

# Khởi tạo Translator và Tokenizer
translator = None
tokenizer = None
model_type = "opus" # "opus" hoặc "nllb"
device = "cpu"

def init_engine():
    global translator, tokenizer, model_type, model_path, device
    if not model_path or not os.path.exists(model_path):
        print(f"WARNING: Model path '{model_path}' not found. Please run model_downloader.py first.", file=sys.stderr)
        return False
        
    print(f"Initializing Translation Engine using model: {model_path}")
    
    # Xác định loại mô hình (NLLB hay OPUS) để cấu hình tokenizer & special tokens
    if "nllb" in model_path.lower():
        model_type = "nllb"
    else:
        model_type = "opus"
        
    # Phát hiện phần cứng (mặc định ưu tiên GPU/CUDA để giải phóng CPU cho ASR theo ATOM20)
    force_cpu = os.environ.get("FORCE_CPU", "0") == "1"
    device = "cuda" if (not force_cpu and ctranslate2.get_cuda_device_count() > 0) else "cpu"
    
    # Lựa chọn kiểu lượng tử phù hợp với thiết bị
    compute_type = "int8"
    if device == "cuda":
        # Trên CUDA, FP16 hoặc INT8_FLOAT16 chạy cực nhanh trên Tensor Cores và tiết kiệm VRAM
        compute_type = "float16"

        
    print(f"Hardware Device: {device.upper()} | Compute Type: {compute_type}")
    
    try:
        # Load mô hình vào CTranslate2
        translator = ctranslate2.Translator(
            model_path,
            device=device,
            compute_type=compute_type,
            inter_threads=2, # Số luồng chạy đồng thời cho suy luận
            intra_threads=4  # Số luồng CPU tối đa cho mỗi luồng suy luận
        )
        
        # Load tokenizer thông qua thư viện Transformers
        if model_type == "opus":
            from transformers import MarianTokenizer
            tokenizer = MarianTokenizer.from_pretrained(model_path, local_files_only=True)
        else:
            tokenizer = AutoTokenizer.from_pretrained(model_path, local_files_only=True)

        print("Engine initialized successfully!")
        return True
    except Exception as e:
        print(f"Error initializing engine: {e}", file=sys.stderr)
        return False

# Cố gắng khởi tạo động khi start server
engine_loaded = init_engine()

@app.get("/", response_class=FileResponse)
async def read_gui():
    gui_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "gui", "index.html")
    if os.path.exists(gui_path):
        return FileResponse(gui_path)
    raise HTTPException(status_code=404, detail="GUI HTML file not found.")

@app.get("/gui", response_class=FileResponse)
async def read_gui_alias():
    return await read_gui()

class TranslationRequest(BaseModel):
    text: str
    source_lang: str = "eng_Latn" # Chỉ dùng khi model là NLLB
    target_lang: str = "vie_Latn" # Chỉ dùng khi model là NLLB

class TranslationResponse(BaseModel):
    original_text: str
    translated_text: str
    latency_ms: float
    model_used: str

@app.post("/translate", response_model=TranslationResponse)
async def translate(request: TranslationRequest):
    global translator, tokenizer, engine_loaded
    
    if not engine_loaded:
        # Cố gắng load lại phòng trường hợp tải mô hình sau khi start server
        engine_loaded = init_engine()
        if not engine_loaded:
            raise HTTPException(status_code=503, detail="Translation engine is not initialized. Make sure a model exists in ./models/.")
            
    if not request.text or not request.text.strip():
        raise HTTPException(status_code=400, detail="Input text cannot be empty.")
        
    start_time = time.perf_counter()
    
    try:
        translated_text = ""
        
        if model_type == "nllb":
            # Xử lý cho mô hình NLLB
            # Thiết lập ngôn ngữ nguồn trong tokenizer
            tokenizer.src_lang = request.source_lang
            source_tokens = tokenizer.convert_ids_to_tokens(tokenizer.encode(request.text))
            
            # Cần chỉ định mã ngôn ngữ đích làm tiền tố giải mã (target_prefix)
            target_prefix = [request.target_lang]
            
            # Gọi suy luận
            results = translator.translate_batch([source_tokens], target_prefix=[target_prefix])
            target_tokens = results[0].hypotheses[0]
            
            # Giải mã kết quả (loại bỏ token ngôn ngữ đích và các token đặc biệt)
            # NLLB Tokenizer decode xử lý việc này tự động
            translated_ids = tokenizer.convert_tokens_to_ids(target_tokens)
            translated_text = tokenizer.decode(translated_ids, skip_special_tokens=True)
            
        else:
            # Xử lý cho mô hình OPUS-MT (English -> Vietnamese)
            # Tokenize văn bản nguồn
            source_tokens = tokenizer.convert_ids_to_tokens(tokenizer.encode(request.text))
            
            # Gọi suy luận (OPUS-MT song ngữ không cần mã ngôn ngữ tiền tố)
            results = translator.translate_batch([source_tokens])
            target_tokens = results[0].hypotheses[0]
            
            # Giải mã
            translated_ids = tokenizer.convert_tokens_to_ids(target_tokens)
            translated_text = tokenizer.decode(translated_ids, skip_special_tokens=True)
            
        latency_ms = (time.perf_counter() - start_time) * 1000.0
        
        return TranslationResponse(
            original_text=request.text,
            translated_text=translated_text.strip(),
            latency_ms=round(latency_ms, 2),
            model_used=os.path.basename(model_path)
        )
        
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Inference error: {str(e)}")

@app.get("/status")
async def status():
    return {
        "status": "ready" if engine_loaded else "model_missing",
        "model_path": model_path,
        "model_type": model_type,
        "device": device,
        "has_cuda": ctranslate2.get_cuda_device_count() > 0
    }


if __name__ == "__main__":
    # Nhận cổng port từ biến môi trường hoặc mặc định 11435
    port = int(os.environ.get("PORT", 11435))
    uvicorn.run(app, host="127.0.0.1", port=port)
