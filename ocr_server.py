# -*- coding: utf-8 -*-
"""
Persistent OCR worker. Loads EasyOCR/CNN/templates ONCE at startup, then
answers requests over stdin/stdout — no more per-click process spawn + model reload.
Protocol: one JSON object per line in, one JSON object per line out.
"""
import sys, os, io, json
import numpy as np

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
sys.stdin = io.TextIOWrapper(sys.stdin.buffer, encoding='utf-8', errors='replace')
sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8', errors='replace')

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from final_ocr import PixTechOCR, crop_roi

_ocr = None
_ocr_model_dir = None


def convert(obj):
    if isinstance(obj, dict): return {k: convert(v) for k, v in obj.items()}
    if isinstance(obj, list): return [convert(v) for v in obj]
    if isinstance(obj, np.integer): return int(obj)
    if isinstance(obj, np.floating): return float(obj)
    if isinstance(obj, np.ndarray): return obj.tolist()
    return obj


def get_ocr(model_dir):
    global _ocr, _ocr_model_dir
    if _ocr is None or _ocr_model_dir != model_dir:
        print(f"[SERVER] loading model_dir={model_dir}", file=sys.stderr, flush=True)
        _ocr = PixTechOCR(model_dir)
        _ocr_model_dir = model_dir
    return _ocr


def handle(req):
    image_path = req.get("image_path")
    model_dir = req.get("model_dir") or None
    roi = req.get("roi") or None
    real_path, tmp_path = image_path, None
    try:
        if roi:
            real_path, tmp_path = crop_roi(image_path, roi)
        return get_ocr(model_dir).process(real_path)
    except Exception as ex:
        return {"error": str(ex), "text": "", "confidence": 0.0, "char_count": 0, "boxes": []}
    finally:
        if tmp_path and os.path.exists(tmp_path):
            try: os.remove(tmp_path)
            except Exception: pass


def main():
    get_ocr(None)  # warm up immediately so the first real click is fast too
    print("READY", file=sys.stderr, flush=True)
    for line in sys.stdin:
        line = line.strip()
        if not line: continue
        if line == "PING":
            print(json.dumps({"pong": True}), flush=True)
            continue
        try:
            result = handle(json.loads(line))
        except Exception as ex:
            result = {"error": str(ex), "text": "", "confidence": 0.0, "char_count": 0, "boxes": []}
        print(json.dumps(convert(result)), flush=True)


if __name__ == '__main__':
    main()