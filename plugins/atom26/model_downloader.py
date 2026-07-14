import os
import sys
import argparse
import json
from huggingface_hub import snapshot_download, model_info
import ctranslate2

def convert_model(model_name: str, output_dir: str, quantization: str = "int8"):
    print(f"=== Starting Download & Conversion ===")
    print(f"Model Source: {model_name}")
    print(f"Target Directory: {output_dir}")
    print(f"Quantization: {quantization}")
    
    # Tạo đường dẫn thư mục cache Hugging Face tạm thời cục bộ để dễ dàng dọn dẹp
    models_dir = os.path.dirname(os.path.abspath(output_dir))
    local_temp_cache = os.path.join(models_dir, "temp_hf_cache")
    os.makedirs(local_temp_cache, exist_ok=True)
    
    try:
        # Tải mô hình gốc từ Hugging Face Hub (lưu vào cache tạm cục bộ)
        print(f"Downloading source model from Hugging Face Hub (using temp cache: {local_temp_cache})...")
        cache_dir = snapshot_download(
            repo_id=model_name,
            cache_dir=local_temp_cache,
            ignore_patterns=["*.msgpack", "*.h5", "*.ot", "rust_model.ot"]
        )
        print(f"Source model downloaded to temp cache: {cache_dir}")
        
        # Khởi tạo bộ chuyển đổi của CTranslate2
        print("Converting model weights using CTranslate2 converter...")
        
        # Cấu hình danh sách file cần sao chép thêm để phục vụ Tokenizer
        copy_files = ["tokenizer.json", "tokenizer_config.json", "special_tokens_map.json"]
        if "nllb" in model_name.lower():
            copy_files.append("sentencepiece.bpe.model")
        elif "opus" in model_name.lower():
            copy_files.extend(["source.spm", "target.spm", "vocab.json"])
            
        # Lọc các file thực sự tồn tại trong thư mục cache
        existing_copy_files = [f for f in copy_files if os.path.exists(os.path.join(cache_dir, f))]
        
        converter = ctranslate2.converters.TransformersConverter(
            model_name_or_path=cache_dir,
            copy_files=existing_copy_files
        )
        
        # Chuyển đổi và lưu
        converter.convert(
            output_dir=output_dir,
            quantization=quantization,
            force=True
        )
        
        # Copy các file tokenizer sang thư mục output để tiện sử dụng trực tiếp
        import shutil
        for f in existing_copy_files:
            src = os.path.join(cache_dir, f)
            dst = os.path.join(output_dir, f)
            shutil.copy2(src, dst)
            
        # Lấy thông tin commit hash của mô hình trên Hugging Face
        try:
            print("Fetching commit hash from Hugging Face Hub to save metadata...")
            info = model_info(model_name)
            commit_hash = info.sha
            
            # Lưu tệp metadata phiên bản
            version_file = os.path.join(output_dir, "model_version.json")
            version_data = {
                "repo_id": model_name,
                "commit_hash": commit_hash,
                "quantization": quantization
            }
            with open(version_file, "w", encoding="utf-8") as f:
                json.dump(version_data, f, indent=4)
            print(f"Saved version metadata (Commit: {commit_hash}) to: {version_file}")
        except Exception as meta_err:
            print(f"Warning: Failed to save version metadata: {meta_err}", file=sys.stderr)

        print(f"Success! Model converted and saved to: {output_dir}")
        return True
    except Exception as e:
        print(f"Error during model download/conversion: {e}", file=sys.stderr)
        import traceback
        traceback.print_exc()
        return False
    finally:
        # Dọn dẹp cache tải về tạm thời để tiết kiệm dung lượng ổ cứng (Under job dọn dẹp)
        if os.path.exists(local_temp_cache):
            print(f"Cleaning up Hugging Face download cache: {local_temp_cache}...")
            import shutil
            try:
                shutil.rmtree(local_temp_cache, ignore_errors=True)
                print("Hugging Face download cache cleaned successfully.")
            except Exception as clean_err:
                print(f"Warning: Failed to clean temp cache: {clean_err}", file=sys.stderr)
        print(f"======================================")

def check_update(model_name: str, output_dir: str):
    print(f"=== Checking for Updates ===")
    print(f"Model ID: {model_name}")
    print(f"Local Directory: {output_dir}")
    
    version_file = os.path.join(output_dir, "model_version.json")
    if not os.path.exists(version_file):
        print("RESULT: UPDATE_REQUIRED (No version metadata found locally)")
        return
        
    try:
        with open(version_file, "r", encoding="utf-8") as f:
            local_data = json.load(f)
            
        local_hash = local_data.get("commit_hash", "")
        if not local_hash:
            print("RESULT: UPDATE_REQUIRED (Local commit hash is empty)")
            return
            
        # Lấy thông tin hash mới nhất trên Hugging Face Hub
        print("Contacting Hugging Face Hub for remote model info...")
        info = model_info(model_name)
        remote_hash = info.sha
        
        print(f"Local Commit Hash:  {local_hash}")
        print(f"Remote Commit Hash: {remote_hash}")
        
        if local_hash == remote_hash:
            print("RESULT: UP_TO_DATE")
        else:
            print("RESULT: UPDATE_AVAILABLE")
    except Exception as e:
        print(f"RESULT: ERROR (Failed to check update: {str(e)})", file=sys.stderr)

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Download, convert and check update for NMT models.")
    parser.add_argument(
        "--model", 
        type=str, 
        default="Helsinki-NLP/opus-mt-en-vi",
        help="Hugging Face model ID"
    )
    parser.add_argument(
        "--output", 
        type=str, 
        default="./models/opus-en-vi-int8",
        help="Output directory for the model"
    )
    parser.add_argument(
        "--quantization", 
        type=str, 
        default="int8",
        choices=["int8", "float16", "int16"],
        help="Quantization type"
    )
    parser.add_argument(
        "--check-only",
        action="store_true",
        help="Only check for updates on Hugging Face Hub without downloading"
    )
    
    args = parser.parse_args()
    
    if args.check_only:
        check_update(args.model, args.output)
    else:
        # Đảm bảo thư mục cha của output tồn tại
        os.makedirs(os.path.dirname(os.path.abspath(args.output)), exist_ok=True)
        convert_model(args.model, args.output, args.quantization)
