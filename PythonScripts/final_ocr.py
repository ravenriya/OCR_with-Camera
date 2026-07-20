# -*- coding: utf-8 -*-
"""
final_ocr.py  –  PixTech OCR v8.0  (works out-of-the-box)
===========================================================

WORKFLOW:
  Hit Run → reads text immediately (EasyOCR + preprocessing)
  Correct mistakes → those corrections become training data
  Train → builds template library + CNN
  Next Run → template matching + CNN + EasyOCR consensus

FALLBACK CHAIN:
  1. Template matching (if templates exist)  — Cognex-style NCC
  2. CNN classifier  (if trained model exists)
  3. EasyOCR         (always available, no training needed)

OUTPUT: result.json with per-character boxes for C# green rectangles:
  {
    "text": "AB100-3161109",
    "confidence": 72.5,
    "char_count": 13,
    "boxes": [
      {"x": 10, "y": 5, "width": 22, "height": 35, "label": "A", "confidence": 85.0},
      ...
    ]
  }

Usage:
  python final_ocr.py <image> [model_dir] [output_dir] [--roi x,y,w,h,angle]
"""

import torch, torch.nn as nn, cv2, json, sys, os, io, time
import numpy as np
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8', errors='replace')
os.environ['TQDM_DISABLE'] = '1'

IMG_SIZE = 48
TEMPLATE_SIZE = 64
FULL_CHARS = list("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ/-.,()#:&?")
N_CLASSES = len(FULL_CHARS)


class TinyCNN(nn.Module):
    def __init__(self, n=N_CLASSES):
        super().__init__()
        self.backbone = nn.Sequential(
            nn.Conv2d(1, 32, 3, padding=1), nn.BatchNorm2d(32), nn.ReLU(), nn.MaxPool2d(2),
            nn.Conv2d(32, 64, 3, padding=1), nn.BatchNorm2d(64), nn.ReLU(), nn.MaxPool2d(2),
            nn.Conv2d(64, 128, 3, padding=1), nn.BatchNorm2d(128), nn.ReLU(), nn.AdaptiveAvgPool2d(4),
        )
        self.head = nn.Sequential(
            nn.Flatten(),
            nn.Linear(128 * 16, 256), nn.ReLU(), nn.Dropout(0.4),
            nn.Linear(256, n),
        )
    def forward(self, x): return self.head(self.backbone(x))


# ════════════════════════════════════════════════════════════════════
# METAL PREPROCESSING
# ════════════════════════════════════════════════════════════════════
def preprocess_metal(gray, level='standard'):
    if gray.size == 0 or min(gray.shape[:2]) < 4:
        return gray

    if level == 'light':
        clahe = cv2.createCLAHE(clipLimit=3.0, tileGridSize=(4, 4))
        return cv2.bilateralFilter(clahe.apply(gray), 7, 50, 50)

    if level == 'standard':
        clahe = cv2.createCLAHE(clipLimit=5.0, tileGridSize=(4, 4))
        out = cv2.bilateralFilter(clahe.apply(gray), 9, 75, 75)
        blur = cv2.GaussianBlur(out, (0, 0), 2.0)
        out = cv2.addWeighted(out, 1.8, blur, -0.8, 0)
        return cv2.normalize(out, None, 0, 255, cv2.NORM_MINMAX).astype(np.uint8)

    if level == 'aggressive':
        clahe = cv2.createCLAHE(clipLimit=8.0, tileGridSize=(3, 3))
        out = cv2.bilateralFilter(clahe.apply(gray), 9, 75, 75)
        blur = cv2.GaussianBlur(out, (0, 0), 3.0)
        out = cv2.addWeighted(out, 2.2, blur, -1.2, 0)
        return cv2.normalize(out, None, 0, 255, cv2.NORM_MINMAX).astype(np.uint8)

    if level == 'edge':
        blurred = cv2.GaussianBlur(gray, (3, 3), 0)
        sx = cv2.Sobel(blurred, cv2.CV_64F, 1, 0, ksize=3)
        sy = cv2.Sobel(blurred, cv2.CV_64F, 0, 1, ksize=3)
        mag = cv2.normalize(np.sqrt(sx**2 + sy**2), None, 0, 255, cv2.NORM_MINMAX).astype(np.uint8)
        return cv2.addWeighted(cv2.createCLAHE(clipLimit=4.0, tileGridSize=(4, 4)).apply(gray),
                               0.6, mag, 0.4, 0)

    return gray

def crop_roi(image_path, roi_str):
    """Crops image_path to roi_str='x,y,w,h,angle'. Returns (path_to_use, tmp_path_or_None)."""
    import tempfile
    parts = list(map(int, roi_str.split(',')))
    x, y, w, h = parts[0], parts[1], parts[2], parts[3]
    angle = parts[4] if len(parts) > 4 else 0

    img = cv2.imread(image_path)
    if img is None:
        return image_path, None

    if angle != 0:
        cx, cy = x + w / 2.0, y + h / 2.0
        diagonal = int(np.sqrt(w**2 + h**2))
        pad = int(diagonal / 2)
        px1, py1 = max(0, int(cx - pad)), max(0, int(cy - pad))
        px2, py2 = min(img.shape[1], int(cx + pad)), min(img.shape[0], int(cy + pad))
        pad_img = img[py1:py2, px1:px2]
        ncx, ncy = cx - px1, cy - py1
        M = cv2.getRotationMatrix2D((ncx, ncy), angle, 1.0)
        rotated_pad = cv2.warpAffine(pad_img, M, (pad_img.shape[1], pad_img.shape[0]),
                                      flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)
        crop = cv2.getRectSubPix(rotated_pad, (int(w), int(h)), (ncx, ncy))
    else:
        ih, iw = img.shape[:2]
        x, y = max(0, min(x, iw - 1)), max(0, min(y, ih - 1))
        w, h = min(w, iw - x), min(h, ih - y)
        crop = img[y:y + h, x:x + w]

    tmp_fd, tmp_path = tempfile.mkstemp(suffix='_roi.jpg', prefix='pixtech_')
    os.close(tmp_fd)
    cv2.imwrite(tmp_path, crop)
    return tmp_path, tmp_path
# ════════════════════════════════════════════════════════════════════
# TEMPLATE MATCHER
# ════════════════════════════════════════════════════════════════════
class TemplateMatcher:
    def __init__(self, model_dir=None):
        self.templates = {}
        self.is_real = {}
        self.loaded = False
        if model_dir:
            self._load(model_dir)

    def _load(self, model_dir):
        npz_path = os.path.join(model_dir, 'templates.npz')
        json_path = os.path.join(model_dir, 'template_library.json')

        if os.path.exists(npz_path):
            try:
                data = np.load(npz_path)
                by_char = {}
                for key in data.files:
                    parts = key.rsplit('_', 1)
                    if len(parts) == 2:
                        char = parts[0]
                        if char.startswith('sym_'):
                            char = chr(int(char[4:]))
                        by_char.setdefault(char, []).append(data[key])
                self.templates = by_char
                if os.path.exists(json_path):
                    with open(json_path, 'r') as f:
                        self.is_real = json.load(f).get('is_real', {})
                else:
                    self.is_real = {ch: True for ch in self.templates}
                self.loaded = True
                total = sum(len(v) for v in self.templates.values())
                print(f"[TPL] {total} templates loaded", file=sys.stderr, flush=True)
            except Exception as ex:
                print(f"[WARN] template load failed: {ex}", file=sys.stderr, flush=True)

    def match(self, crop_gray, top_n=3):
        if not self.loaded or crop_gray.size == 0:
            return []
        crop = cv2.resize(crop_gray, (TEMPLATE_SIZE, TEMPLATE_SIZE), interpolation=cv2.INTER_AREA)
        crop_f = crop.astype(np.float32)
        crop_std = max(crop_f.std(), 1.0)
        crop_norm = (crop_f - crop_f.mean()) / crop_std

        results = []
        for char, tmpls in self.templates.items():
            best = -1.0
            for tpl in tmpls:
                tpl_f = tpl.astype(np.float32)
                tpl_norm = (tpl_f - tpl_f.mean()) / max(tpl_f.std(), 1.0)
                ncc = np.mean(crop_norm * tpl_norm)
                try:
                    cv_score = cv2.matchTemplate(crop, tpl, cv2.TM_CCOEFF_NORMED).max()
                    score = max(ncc, cv_score)
                except:
                    score = ncc
                best = max(best, score)
            results.append((char, best * 100.0, self.is_real.get(char, False)))

        results.sort(key=lambda r: r[1], reverse=True)
        return results[:top_n]


# ════════════════════════════════════════════════════════════════════
# EASYOCR WRAPPER (always available, no training needed)
# ════════════════════════════════════════════════════════════════════
class EasyOCREngine:
    def __init__(self):
        self._reader = None

    def _ensure_loaded(self):
        if self._reader is None:
            import easyocr
            print("[INFO] Loading EasyOCR engine...", file=sys.stderr, flush=True)
            self._reader = easyocr.Reader(['en'], gpu=torch.cuda.is_available(), verbose=False)

    def read_with_boxes(self, img, gray):
        self._ensure_loaded()
        clahe = cv2.createCLAHE(clipLimit=5.0, tileGridSize=(4, 4))

        def make_variant(name):
            if name == 'original':
                return img
            if name == 'clahe_sharp':
                v1 = clahe.apply(gray)
                v1 = cv2.bilateralFilter(v1, 9, 75, 75)
                blur = cv2.GaussianBlur(v1, (0, 0), 2.0)
                v1 = cv2.addWeighted(v1, 2.0, blur, -1.0, 0)
                return cv2.cvtColor(v1, cv2.COLOR_GRAY2BGR)
            if name == 'adaptive':
                v2 = cv2.bilateralFilter(clahe.apply(gray), 7, 50, 50)
                v2 = cv2.adaptiveThreshold(v2, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C,
                                            cv2.THRESH_BINARY, 31, 8)
                return cv2.cvtColor(v2, cv2.COLOR_GRAY2BGR)
            if name == 'inverted':
                v3 = clahe.apply(cv2.bitwise_not(gray))
                return cv2.cvtColor(v3, cv2.COLOR_GRAY2BGR)

        # Try the variant that works best on stamped metal FIRST.
        # If it's already confident, skip the other 3 passes entirely.
        order = ['original', 'clahe_sharp', 'adaptive', 'inverted']
        best_result, best_score = None, -1

        for name in order:
            t0 = time.time()
            try:
                results = self._reader.readtext(make_variant(name), detail=1)
            except Exception as ex:
                print(f"[WARN] {name} failed: {ex}", file=sys.stderr, flush=True)
                continue
            print(f"[TIMING] {name} pass took {time.time()-t0:.2f}s", file=sys.stderr, flush=True)
            if not results:
                continue

            n_chars = sum(len(r[1]) for r in results)
            total_conf = sum(r[2] for r in results)
            score = total_conf * (1.0 + 0.1 * n_chars)
            avg_conf = total_conf / max(len(results), 1)

            print(f"[OCR] {name}: '{' '.join(r[1] for r in results)}' score={score:.1f}",
                  file=sys.stderr, flush=True)

            if score > best_score:
                best_score, best_result = score, results

            if n_chars >= 1 and avg_conf >= 0.55:   # good enough, stop early — was 0.80
                break

        if not best_result:
            return [], "", 0.0

        char_boxes = []
        full_text = ""
        total_conf = 0.0

        for ri, (bbox, text, conf) in enumerate(best_result):
            xs = [p[0] for p in bbox]; ys = [p[1] for p in bbox]
            x_min, y_min = int(min(xs)), int(min(ys))
            x_max, y_max = int(max(xs)), int(max(ys))
            word_w, word_h = x_max - x_min, y_max - y_min
            char_w = word_w / max(len(text), 1)

            if ri > 0:
                prev_bbox = best_result[ri - 1][0]
                prev_xs = [p[0] for p in prev_bbox]; prev_ys = [p[1] for p in prev_bbox]
                y_diff = y_min - min(prev_ys)
                avg_h = (word_h + max(prev_ys) - min(prev_ys)) / 2
                if y_diff > avg_h * 0.5:
                    full_text += "\n"
                else:
                    gap = x_min - max(prev_xs)
                    if gap > char_w * 0.5:
                        full_text += " "

            for ci, ch in enumerate(text):
                cx = x_min + int(ci * char_w)
                char_boxes.append({
                    'x': cx, 'y': y_min, 'width': max(int(char_w), 1), 'height': word_h,
                    'label': ch, 'confidence': round(conf * 100, 1), 'source': 'easyocr',
                })
                full_text += ch
                total_conf += conf * 100

        avg_conf = total_conf / max(len(char_boxes), 1)
        return char_boxes, full_text, avg_conf


# ════════════════════════════════════════════════════════════════════
# MAIN OCR ENGINE
# ════════════════════════════════════════════════════════════════════
class PixTechOCR:
    def __init__(self, model_dir=None):
        self.device = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
        print(f"[INFO] CUDA available: {torch.cuda.is_available()}", file=sys.stderr, flush=True)
        self.easyocr = EasyOCREngine()
        self.matcher = TemplateMatcher()
        self.classifier = None
        self.classifier_chars = FULL_CHARS
        self.has_model = False
        self.has_templates = False
        self.annotated_chars = set()

        if model_dir and os.path.isdir(model_dir):
            self._load_model(model_dir)

    def _load_model(self, model_dir):
        cp = os.path.join(model_dir, 'model_config.json')
        if not os.path.exists(cp):
            print("[INFO] No trained model — using EasyOCR only", file=sys.stderr, flush=True)
            return

        config = json.load(open(cp, 'r'))

        # Load templates
        self.matcher = TemplateMatcher(model_dir)
        self.has_templates = self.matcher.loaded

        # Load CNN
        mf = config.get('model_file', 'char_classifier.pth')
        model_path = os.path.join(model_dir, mf)
        if os.path.exists(model_path):
            try:
                ckpt = torch.load(model_path, map_location=self.device, weights_only=False)
                n_classes = ckpt.get('n_classes', N_CLASSES)
                self.classifier_chars = ckpt.get('chars', FULL_CHARS)
                self.classifier = TinyCNN(n_classes).to(self.device)
                self.classifier.load_state_dict(ckpt['model_state'])
                self.classifier.eval()
                self.has_model = True
            except Exception as ex:
                print(f"[WARN] CNN load failed: {ex}", file=sys.stderr, flush=True)

        self.annotated_chars = set(c.upper() for c in config.get('annotated_chars', []))
        mode = "template+CNN" if self.has_templates and self.has_model else \
               "templates" if self.has_templates else \
               "CNN" if self.has_model else "EasyOCR"
        print(f"[INFO] Mode: {mode} | Annotated: {sorted(self.annotated_chars)}",
              file=sys.stderr, flush=True)

    def _classify_cnn(self, crop_gray):
        """CNN single-char classification."""
        if self.classifier is None or crop_gray.size == 0 or min(crop_gray.shape[:2]) < 4:
            return None, 0.0, 0.0
        t = torch.tensor(
            cv2.resize(crop_gray, (IMG_SIZE, IMG_SIZE)).astype(np.float32) / 127.5 - 1.0
        ).unsqueeze(0).unsqueeze(0).to(self.device)
        with torch.no_grad():
            probs = torch.softmax(self.classifier(t), dim=1)
            top2 = torch.topk(probs, 2, dim=1)
            conf1 = top2.values[0][0].item() * 100.0
            conf2 = top2.values[0][1].item() * 100.0
            idx = top2.indices[0][0].item()
            if idx < len(self.classifier_chars):
                return self.classifier_chars[idx], conf1, conf1 - conf2
        return None, 0.0, 0.0

    def process(self, image_path):
        img = cv2.imread(image_path)
        if img is None:
            return {"error": "Cannot load image", "text": "", "confidence": 0.0,
                    "char_count": 0, "boxes": []}

        gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
        H, W = gray.shape[:2]

        # ═══════════════════════════════════════════════════════════════
        # STEP 1: EasyOCR reads the text (always works, no training)
        # ═══════════════════════════════════════════════════════════════
        print("[INFO] Running EasyOCR...", file=sys.stderr, flush=True)
        ocr_boxes, ocr_text, ocr_conf = self.easyocr.read_with_boxes(img, gray)

        if not ocr_boxes:
            return {"error": "No text detected", "text": "", "confidence": 0.0,
                    "char_count": 0, "boxes": []}

        print(f"[INFO] EasyOCR: '{ocr_text}' ({ocr_conf:.1f}%)",
              file=sys.stderr, flush=True)

        # ═══════════════════════════════════════════════════════════════
        # STEP 2: If we have templates or CNN, try to improve each char
        # ═══════════════════════════════════════════════════════════════
        if not self.has_templates and not self.has_model:
            # No training done yet — return EasyOCR results directly
            print("[INFO] No trained model — returning EasyOCR results",
                  file=sys.stderr, flush=True)
            return {
                "text": ocr_text,
                "original_text": ocr_text,
                "confidence": round(ocr_conf, 1),
                "char_count": len(ocr_boxes),
                "boxes": ocr_boxes,
                "method": "easyocr",
            }

        # We have trained data — try to correct each character
        print("[INFO] Applying template/CNN corrections...", file=sys.stderr, flush=True)
        corrected_boxes = []
        corrections = 0
        total_conf = 0.0

        for box in ocr_boxes:
            bx, by = box['x'], box['y']
            bw, bh = box['width'], box['height']
            ocr_char = box['label']
            ocr_char_conf = box['confidence']

            # Extract crop with padding
            pad_x = max(3, bw // 4)
            pad_y = max(3, bh // 6)
            x1, y1 = max(0, bx - pad_x), max(0, by - pad_y)
            x2, y2 = min(W, bx + bw + pad_x), min(H, by + bh + pad_y)
            crop = gray[y1:y2, x1:x2]

            if crop.size == 0 or min(crop.shape[:2]) < 4:
                corrected_boxes.append(box)
                total_conf += ocr_char_conf
                continue

            crop_enh = preprocess_metal(crop, 'standard')

            # Template matching
            tpl_results = self.matcher.match(crop_enh) if self.has_templates else []

            # CNN classification
            cnn_char, cnn_conf, cnn_margin = self._classify_cnn(crop_enh)

            # ── Decision logic ───────────────────────────────────────
            final_char = ocr_char
            final_conf = ocr_char_conf
            source = 'easyocr'

            # Template with real data: trust if confident
            if tpl_results:
                tpl_char, tpl_score, tpl_real = tpl_results[0]

                if tpl_real and tpl_score >= 60:
                    # Real template, good match
                    if cnn_char and cnn_char == tpl_char and cnn_conf >= 70:
                        # Template + CNN agree → very confident
                        final_char = tpl_char
                        final_conf = min(99.0, (tpl_score + cnn_conf) / 2 * 1.2)
                        source = 'template+cnn'
                    elif tpl_score >= 70:
                        # Template alone is strong enough
                        final_char = tpl_char
                        final_conf = tpl_score
                        source = 'template'

            # CNN only: override EasyOCR if very confident on annotated char
            if source == 'easyocr' and cnn_char and self.has_model:
                if (cnn_char.upper() in self.annotated_chars and
                        cnn_conf >= 99 and cnn_margin >= 60):
                    final_char = cnn_char
                    final_conf = cnn_conf
                    source = 'cnn'

            if final_char != ocr_char:
                corrections += 1
                print(f"  [FIX] '{ocr_char}'->'{final_char}' via {source} "
                      f"({final_conf:.0f}%)", file=sys.stderr, flush=True)

            corrected_boxes.append({
                'x': bx, 'y': by, 'width': bw, 'height': bh,
                'label': final_char,
                'confidence': round(final_conf, 1),
                'source': source,
            })
            total_conf += final_conf

        # ═══════════════════════════════════════════════════════════════
        # STEP 3: Build final text from corrected boxes
        # ═══════════════════════════════════════════════════════════════
        text_parts = []
        for i, box in enumerate(corrected_boxes):
            if i > 0:
                prev = corrected_boxes[i - 1]
                y_diff = box['y'] - prev['y']
                avg_h = (prev['height'] + box['height']) / 2
                
                # If Y drops by more than half a character's height, insert newline
                if y_diff > avg_h * 0.5:
                    text_parts.append('\n')
                else:
                    # Otherwise, check X gap for a space
                    gap = box['x'] - (prev['x'] + prev['width'])
                    avg_w = (prev['width'] + box['width']) / 2
                    if gap > avg_w * 0.35:
                        text_parts.append(' ')
                        
            text_parts.append(box['label'])

        final_text = ''.join(text_parts).strip()
        avg_conf = total_conf / max(len(corrected_boxes), 1)

        method = "template+cnn+easyocr" if self.has_templates else \
                 "cnn+easyocr" if self.has_model else "easyocr"

        print(f"\n{'=' * 50}", file=sys.stderr, flush=True)
        print(f"EasyOCR:   '{ocr_text}'", file=sys.stderr, flush=True)
        print(f"Corrected: '{final_text}'  ({corrections} fixes)",
              file=sys.stderr, flush=True)
        print(f"Confidence: {avg_conf:.1f}%", file=sys.stderr, flush=True)
        print(f"{'=' * 50}", file=sys.stderr, flush=True)

        return {
            "text": final_text,
            "original_text": ocr_text,
            "confidence": round(avg_conf, 1),
            "char_count": len(corrected_boxes),
            "corrections_applied": corrections,
            "boxes": corrected_boxes,
            "method": method,
        }

    def save_result(self, result, output_dir):
        output_dir = Path(output_dir)
        output_dir.mkdir(parents=True, exist_ok=True)

        # Convert numpy types to native Python for JSON serialization
        def convert(obj):
            if isinstance(obj, dict):
                return {k: convert(v) for k, v in obj.items()}
            if isinstance(obj, list):
                return [convert(v) for v in obj]
            if isinstance(obj, (np.integer,)):
                return int(obj)
            if isinstance(obj, (np.floating,)):
                return float(obj)
            if isinstance(obj, np.ndarray):
                return obj.tolist()
            return obj

        with open(output_dir / "result.json", 'w') as f:
            json.dump(convert(result), f, indent=2)


# ════════════════════════════════════════════════════════════════════
# CLI
# ════════════════════════════════════════════════════════════════════
if __name__ == '__main__':
    import argparse

    parser = argparse.ArgumentParser()
    parser.add_argument('image_path')
    parser.add_argument('model_path', nargs='?', default=None)
    parser.add_argument('output_dir', nargs='?', default='output')
    parser.add_argument('--roi', type=str, default=None)
    args = parser.parse_args()

    image_path = args.image_path
    model_path = args.model_path if args.model_path and args.model_path not in ('""', '') else None
    output_dir = args.output_dir

    # ── ROI crop ─────────────────────────────────────────────────────
    # ── ROI crop ─────────────────────────────────────────────────────
    tmp_path = None
    if args.roi:
        try:
            import tempfile
            parts = list(map(int, args.roi.split(',')))
            x, y, w, h = parts[0], parts[1], parts[2], parts[3]
            angle = parts[4] if len(parts) > 4 else 0
            
            img = cv2.imread(image_path)
            if img is not None:
                if angle != 0:
                    # 1. Find the true center of the ROI
                    cx = x + w / 2.0
                    cy = y + h / 2.0
                    
                    # 2. Extract a padded region so we don't cut off corners during rotation
                    diagonal = int(np.sqrt(w**2 + h**2))
                    pad = int(diagonal / 2)
                    
                    px1 = max(0, int(cx - pad))
                    py1 = max(0, int(cy - pad))
                    px2 = min(img.shape[1], int(cx + pad))
                    py2 = min(img.shape[0], int(cy + pad))
                    
                    pad_img = img[py1:py2, px1:px2]
                    
                    # 3. Calculate the new center relative to the padded region
                    ncx = cx - px1
                    ncy = cy - py1
                    
                    # 4. Rotate the padded region to flatten the text
                    # (OpenCV positive angle = counter-clockwise, perfectly un-doing WPF's clockwise rotation)
                    M = cv2.getRotationMatrix2D((ncx, ncy), angle, 1.0)
                    rotated_pad = cv2.warpAffine(pad_img, M, (pad_img.shape[1], pad_img.shape[0]), 
                                                 flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)
                    
                    # 5. Crop the exact width and height from the flattened image
                    crop = cv2.getRectSubPix(rotated_pad, (int(w), int(h)), (ncx, ncy))
                else:
                    # Flat crop (No rotation)
                    ih, iw = img.shape[:2]
                    x, y = max(0, min(x, iw-1)), max(0, min(y, ih-1))
                    w, h = min(w, iw-x), min(h, ih-y)
                    crop = img[y:y+h, x:x+w]

                tmp_fd, tmp_path = tempfile.mkstemp(suffix='_roi.jpg', prefix='pixtech_')
                os.close(tmp_fd)
                cv2.imwrite(tmp_path, crop)
                image_path = tmp_path
        except Exception as ex:
            print(f'ROI crop failed: {ex}', file=sys.stderr)

    # ── Run ──────────────────────────────────────────────────────────
    ocr = PixTechOCR(model_path)
    result = ocr.process(image_path)

    if tmp_path and os.path.exists(tmp_path):
        try: os.remove(tmp_path)
        except: pass

    ocr.save_result(result, output_dir)