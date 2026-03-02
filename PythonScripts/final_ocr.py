# -*- coding: utf-8 -*-
"""
final_ocr.py  -  PixTech OCR Inference v3
===========================================
1. EasyOCR reads the full image
2. For each char EasyOCR outputs:
   - If it's in a confusion group WITH a resolver -> visually verify
   - If it's in a confusion group WITHOUT resolver -> use replacement map
   - Otherwise -> trust EasyOCR
3. Line breaks detected from Y-coordinate gaps
4. Spaces detected from X-coordinate gaps
"""
import easyocr, torch, torch.nn as nn, cv2, json, sys, os, io
import numpy as np
from pathlib import Path

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8', errors='replace')
os.environ['TQDM_DISABLE'] = '1'

IMG_SIZE = 48
RESOLVER_MIN_CONF = 55.0


class TinyCNN(nn.Module):
    def __init__(self, n):
        super().__init__()
        self.net = nn.Sequential(
            nn.Conv2d(1,32,3,padding=1), nn.BatchNorm2d(32), nn.ReLU(), nn.MaxPool2d(2),
            nn.Conv2d(32,64,3,padding=1), nn.BatchNorm2d(64), nn.ReLU(), nn.MaxPool2d(2),
            nn.Conv2d(64,128,3,padding=1), nn.BatchNorm2d(128), nn.ReLU(), nn.AdaptiveAvgPool2d(4),
            nn.Flatten(),
            nn.Linear(128*16, 256), nn.ReLU(), nn.Dropout(0.4),
            nn.Linear(256, n)
        )
    def forward(self, x):
        return self.net(x)


def to_tensor(crop):
    img = cv2.resize(crop, (IMG_SIZE, IMG_SIZE)).astype(np.float32) / 127.5 - 1.0
    return torch.tensor(img, dtype=torch.float32).unsqueeze(0)


class GenericCorrectionOCR:
    def __init__(self, model_dir=None):
        self.device = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
        print("[INFO] Loading EasyOCR...", file=sys.stderr, flush=True)
        self.reader = easyocr.Reader(['en'], gpu=torch.cuda.is_available(), verbose=False)
        self.char_to_resolver = {}
        self.replacement_map = {}
        self.has_corrections = False

        if model_dir and os.path.isdir(model_dir):
            self._load(model_dir)

    def _load(self, model_dir):
        cp = os.path.join(model_dir, 'model_config.json')
        if not os.path.exists(cp):
            print("[INFO] No model - pure EasyOCR", file=sys.stderr, flush=True)
            return

        config = json.load(open(cp, 'r'))
        self.replacement_map = config.get('replacement_map', {})

        # Load resolvers
        for grp in config.get('groups', []):
            mf = os.path.join(model_dir, grp['model_file'])
            if not os.path.exists(mf):
                continue
            ckpt = torch.load(mf, map_location=self.device, weights_only=False)
            model = TinyCNN(ckpt['n_classes']).to(self.device)
            model.load_state_dict(ckpt['model_state'])
            model.eval()
            info = {'model': model, 'chars': ckpt['chars']}
            for ch in ckpt['chars']:
                self.char_to_resolver[ch] = info
            print(f"[INFO] Resolver: {ckpt['chars']}", file=sys.stderr, flush=True)

        if self.replacement_map:
            print(f"[INFO] Replacements: {self.replacement_map}", file=sys.stderr, flush=True)

        self.has_corrections = bool(self.char_to_resolver) or bool(self.replacement_map)

    def _ask_resolver(self, char_img, resolver_info):
        if len(char_img.shape) == 3:
            char_img = cv2.cvtColor(char_img, cv2.COLOR_BGR2GRAY)
        if char_img.size == 0 or min(char_img.shape[:2]) < 2:
            return None, 0.0
        model = resolver_info['model']
        chars = resolver_info['chars']
        t = to_tensor(char_img).unsqueeze(0).to(self.device)
        with torch.no_grad():
            probs = torch.softmax(model(t), dim=1)
            conf, pred = torch.max(probs, 1)
            idx = pred.item()
            if idx < len(chars):
                return chars[idx], conf.item() * 100
        return None, 0.0

    def process(self, image_path):
        img = cv2.imread(image_path)
        if img is None:
            return {"error": "Cannot load image", "text": "", "confidence": 0.0}
        gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)

        # ── STAGE 1: EasyOCR ─────────────────────────────────────────
        print("[INFO] Stage 1: EasyOCR...", file=sys.stderr, flush=True)
        results = self.reader.readtext(img, detail=1)
        if not results:
            return {"error": "No text detected", "text": "", "confidence": 0.0}

        # Sort by Y then X
        results = sorted(results, key=lambda r: (
            min(p[1] for p in r[0]), min(p[0] for p in r[0])
        ))

        # ── STAGE 2: Build word-level entries with char positions ─────
        # Keep word-level info so we can reconstruct proper spacing
        word_entries = []
        for (bbox, text, conf) in results:
            if not text.strip():
                continue
            xs = [p[0] for p in bbox]
            ys = [p[1] for p in bbox]
            x_min, y_min = int(min(xs)), int(min(ys))
            x_max, y_max = int(max(xs)), int(max(ys))
            w = x_max - x_min
            h = y_max - y_min
            y_center = y_min + h / 2
            char_w = w / max(len(text), 1)

            chars = []
            for i, ch in enumerate(text):
                cx = x_min + int(i * char_w)
                cw = max(int(char_w), 1)
                chars.append({
                    'char': ch,
                    'x': cx, 'y': y_min, 'w': cw, 'h': h,
                })
            word_entries.append({
                'text': text,
                'conf': conf,
                'x': x_min, 'y': y_min, 'w': w, 'h': h,
                'y_center': y_center,
                'chars': chars,
            })

        # ── STAGE 3: Group into lines by Y, add spaces/newlines ──────
        if not word_entries:
            return {"error": "No text detected", "text": "", "confidence": 0.0}

        # Group words into lines based on Y overlap
        lines = []
        current_line = [word_entries[0]]
        for we in word_entries[1:]:
            prev = current_line[-1]
            # Same line if Y centers are close
            if abs(we['y_center'] - prev['y_center']) < prev['h'] * 0.5:
                current_line.append(we)
            else:
                lines.append(current_line)
                current_line = [we]
        lines.append(current_line)

        # Sort words within each line by X
        for line in lines:
            line.sort(key=lambda w: w['x'])

        # Build char list with line breaks and spaces
        char_entries = []
        for li, line in enumerate(lines):
            if li > 0:
                char_entries.append({'char': '\n', 'conf': 1.0, 'bbox': None})

            for wi, word in enumerate(line):
                # Add space between words on same line
                if wi > 0:
                    prev_word = line[wi - 1]
                    gap = word['x'] - (prev_word['x'] + prev_word['w'])
                    avg_char_w = word['w'] / max(len(word['text']), 1)
                    if gap > avg_char_w * 0.3:
                        char_entries.append({'char': ' ', 'conf': word['conf'], 'bbox': None})

                for ch_info in word['chars']:
                    char_entries.append({
                        'char': ch_info['char'],
                        'conf': word['conf'],
                        'bbox': (ch_info['x'], ch_info['y'], ch_info['w'], ch_info['h']),
                    })

        easyocr_text = ''.join(e['char'] for e in char_entries).strip()
        print(f"[INFO] EasyOCR: {easyocr_text}", file=sys.stderr, flush=True)

        # ── STAGE 4: Apply corrections ────────────────────────────────
        if not self.has_corrections:
            print("[INFO] No corrections - returning EasyOCR as-is",
                  file=sys.stderr, flush=True)
            real = [e for e in char_entries if e['char'] not in ('\n', ' ')]
            n = len(real)
            avg = sum(e['conf'] for e in real)
            return {
                "text": easyocr_text,
                "original_text": easyocr_text,
                "confidence": round(avg / max(n, 1) * 100, 1),
                "char_count": n,
                "corrections_applied": 0,
            }

        print("[INFO] Stage 4: Applying corrections...", file=sys.stderr, flush=True)
        output = []
        corrections = 0

        for entry in char_entries:
            ch = entry['char']
            if ch in ('\n', ' '):
                output.append(ch)
                continue

            upper_ch = ch.upper()
            corrected = False

            # Tier 1: Visual resolver
            if upper_ch in self.char_to_resolver and entry['bbox']:
                ri = self.char_to_resolver[upper_ch]
                cx, cy, cw, c_h = entry['bbox']
                pad_x = max(4, cw // 3)
                pad_y = max(4, c_h // 5)
                y1 = max(0, cy - pad_y)
                y2 = min(gray.shape[0], cy + c_h + pad_y)
                x1 = max(0, cx - pad_x)
                x2 = min(gray.shape[1], cx + cw + pad_x)
                crop = gray[y1:y2, x1:x2]

                if crop.size > 0 and min(crop.shape[:2]) >= 4:
                    resolved, conf = self._ask_resolver(crop, ri)
                    if resolved and conf >= RESOLVER_MIN_CONF:
                        if resolved != upper_ch:
                            out_ch = resolved.lower() if ch.islower() else resolved
                            print(f"[FIX] '{ch}'->'{out_ch}' (resolver {conf:.1f}%)",
                                  file=sys.stderr, flush=True)
                            output.append(out_ch)
                            corrections += 1
                        else:
                            output.append(ch)
                        corrected = True
                    else:
                        print(f"[INFO] '{ch}' resolver uncertain ({conf:.1f}%)",
                              file=sys.stderr, flush=True)

            # Tier 2: Replacement map
            if not corrected and upper_ch in self.replacement_map:
                correct_ch = self.replacement_map[upper_ch]
                out_ch = correct_ch.lower() if ch.islower() else correct_ch
                print(f"[FIX] '{ch}'->'{out_ch}' (replacement rule)",
                      file=sys.stderr, flush=True)
                output.append(out_ch)
                corrections += 1
                corrected = True

            if not corrected:
                output.append(ch)

        final_text = ''.join(output).strip()

        real = [e for e in char_entries if e['char'] not in ('\n', ' ')]
        n = len(real)
        avg = sum(e['conf'] for e in real)

        print(f"\n{'='*60}", file=sys.stderr, flush=True)
        print(f"EasyOCR:   {easyocr_text}", file=sys.stderr, flush=True)
        print(f"Corrected: {final_text}", file=sys.stderr, flush=True)
        print(f"Fixes:     {corrections}", file=sys.stderr, flush=True)
        print(f"{'='*60}", file=sys.stderr, flush=True)

        return {
            "text": final_text,
            "original_text": easyocr_text,
            "confidence": round(avg / max(n, 1) * 100, 1),
            "char_count": len(final_text.replace('\n','').replace(' ','')),
            "corrections_applied": corrections,
        }

    def save_result(self, result, output_dir):
        output_dir = Path(output_dir)
        output_dir.mkdir(parents=True, exist_ok=True)
        with open(output_dir / "result.json", 'w') as f:
            json.dump(result, f, indent=2)


if __name__ == '__main__':
    if len(sys.argv) < 2:
        print("Usage: python final_ocr.py <image> [model_dir] [output_dir]")
        sys.exit(1)

    image_path = sys.argv[1]
    model_path = sys.argv[2] if len(sys.argv) > 2 and sys.argv[2] != '""' else None
    output_dir = sys.argv[3] if len(sys.argv) > 3 else 'output'

    ocr = GenericCorrectionOCR(model_path)
    result = ocr.process(image_path)
    if 'error' not in result:
        ocr.save_result(result, output_dir)