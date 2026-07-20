# -*- coding: utf-8 -*-
"""
train_last_layer.py  -  PixTech OCR Trainer v5.1 (Cognex-style)
================================================================
Always N classes (A-Z, 0-9, special chars). Never fewer.

1. Load pretrained_base.pth (knows all chars from synthetic)
2. Your real annotation crops get mixed in for annotated chars
3. Unannotated chars get synthetic data (prevents forgetting)
4. Full network trains — learns your metal/reflections/textures
5. Saves which chars have real data so inference knows what to trust
"""
import argparse, json, os, random, sys, io
import cv2, numpy as np
import torch, torch.nn as nn, torch.optim as optim
from torch.utils.data import Dataset, DataLoader

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8', errors='replace')
os.environ['TQDM_DISABLE'] = '1'
random.seed(42); np.random.seed(42); torch.manual_seed(42)

IMG_SIZE = 48
FULL_CHARS = list("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ/-.,()#:&")
N_CLASSES = len(FULL_CHARS)
CHAR_TO_IDX = {c: i for i, c in enumerate(FULL_CHARS)}


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


# ── Rendering + Augmentation ──────────────────────────────────────────

HERSHEY_FONTS = [
    cv2.FONT_HERSHEY_SIMPLEX, cv2.FONT_HERSHEY_PLAIN,
    cv2.FONT_HERSHEY_DUPLEX, cv2.FONT_HERSHEY_COMPLEX,
    cv2.FONT_HERSHEY_TRIPLEX, cv2.FONT_HERSHEY_COMPLEX_SMALL,
]
try:
    from PIL import Image, ImageDraw, ImageFont
    HAS_PIL = True
except ImportError:
    HAS_PIL = False

PIL_FONTS = [
    "arial.ttf", "arialbd.ttf", "times.ttf", "timesbd.ttf",
    "cour.ttf", "courbd.ttf", "verdana.ttf", "verdanab.ttf",
    "calibri.ttf", "calibrib.ttf", "consola.ttf", "consolab.ttf",
    "impact.ttf", "tahoma.ttf", "tahomabd.ttf",
]


def _pil_render(char, size=IMG_SIZE):
    bg = random.randint(20, 80)
    img = Image.new('L', (size, size), color=bg)
    draw = ImageDraw.Draw(img)
    fs = random.randint(int(size * 0.5), int(size * 0.85))
    font = None
    for fn in random.sample(PIL_FONTS, len(PIL_FONTS)):
        try: font = ImageFont.truetype(fn, fs); break
        except: continue
    if font is None: font = ImageFont.load_default()
    fg = random.choice([random.randint(140, 240), random.randint(0, 50)])
    try:
        bb = draw.textbbox((0, 0), char, font=font); tw, th = bb[2]-bb[0], bb[3]-bb[1]
    except: tw, th = draw.textsize(char, font=font)
    draw.text(((size-tw)//2+random.randint(-3,3), (size-th)//2+random.randint(-3,3)),
              char, fill=fg, font=font)
    return np.array(img, dtype=np.uint8)


def _cv2_render(char, size=IMG_SIZE):
    img = np.full((size, size), random.randint(20, 80), dtype=np.uint8)
    font = random.choice(HERSHEY_FONTS)
    sc, th = random.uniform(0.8, 1.8), random.randint(1, 3)
    fg = random.choice([random.randint(140, 240), random.randint(0, 50)])
    (tw, tht), _ = cv2.getTextSize(char, font, sc, th)
    cv2.putText(img, char, ((size-tw)//2+random.randint(-4,4), (size+tht)//2+random.randint(-4,4)),
                font, sc, fg, th, cv2.LINE_AA)
    return img


def render_synthetic(char):
    return _pil_render(char) if (HAS_PIL and random.random() < 0.7) else _cv2_render(char)


def augment(img):
    h, w = img.shape
    M = cv2.getRotationMatrix2D((w/2, h/2), random.uniform(-15, 15), 1.0)
    img = cv2.warpAffine(img, M, (w, h), borderMode=cv2.BORDER_REPLICATE)
    if random.random() < 0.5:
        mg = max(1, int(min(h, w) * 0.1))
        src = np.float32([[0,0],[w,0],[w,h],[0,h]])
        dst = np.float32([[random.randint(0,mg), random.randint(0,mg)],
                          [w-random.randint(0,mg), random.randint(0,mg)],
                          [w-random.randint(0,mg), h-random.randint(0,mg)],
                          [random.randint(0,mg), h-random.randint(0,mg)]])
        img = cv2.warpPerspective(img, cv2.getPerspectiveTransform(src, dst), (w,h), borderMode=cv2.BORDER_REPLICATE)
    img = np.clip(img.astype(np.int32) * random.uniform(0.6, 1.5) + random.randint(-30, 30), 0, 255).astype(np.uint8)
    if random.random() < 0.5: img = cv2.GaussianBlur(img, (random.choice([3,5]),)*2, 0)
    if random.random() < 0.4:
        img = np.clip(img.astype(np.float32) + np.random.normal(0, random.uniform(5,15), img.shape), 0, 255).astype(np.uint8)
    return img


def to_tensor(crop):
    return torch.tensor(cv2.resize(crop, (IMG_SIZE, IMG_SIZE)).astype(np.float32) / 127.5 - 1.0).unsqueeze(0)

class DS(Dataset):
    def __init__(self, s): self.s = s
    def __len__(self): return len(self.s)
    def __getitem__(self, i): return to_tensor(self.s[i][0]), self.s[i][1]


# ── Collect real crops ────────────────────────────────────────────────

def collect_real_crops(annotations):
    raw = {}
    skipped = 0
    for a in annotations:
        lbl = a.get('Label', '').strip().upper()
        if not lbl or len(lbl) != 1 or lbl not in CHAR_TO_IDX:
            continue
        img_path = a.get('ImagePath', '')
        bgr = cv2.imread(img_path)
        if bgr is None:
            print(f"[WARN] Cannot read: {img_path}", flush=True)
            skipped += 1; continue
        ih, iw = bgr.shape[:2]
        gray = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)
        ax, ay, aw, ah = float(a['X']), float(a['Y']), float(a['Width']), float(a['Height'])
        x, y, w, h = int(ax*iw), int(ay*ih), int(aw*iw), int(ah*ih)
        if w < 4 or h < 4:
            skipped += 1; continue
        pad = max(4, min(w, h) // 5)
        crop = gray[max(0,y-pad):min(ih,y+h+pad), max(0,x-pad):min(iw,x+w+pad)]
        if crop.size == 0 or min(crop.shape) < 4:
            skipped += 1; continue
        raw.setdefault(lbl, []).append(crop)
    print(f"[INFO] Real crops: {sum(len(v) for v in raw.values())} across {len(raw)} chars. Skipped: {skipped}", flush=True)
    for c in sorted(raw.keys()):
        print(f"  '{c}': {len(raw[c])} real crops", flush=True)
    return raw


def build_dataset(real_crops, synth_per_class=80):
    samples = []
    annotated_chars = set(real_crops.keys())

    for ci, char in enumerate(FULL_CHARS):
        if char in annotated_chars:
            crops = real_crops[char]
            n_real = len(crops)
            for crop in crops:
                samples.append((crop, ci))
            n_aug = max(synth_per_class, synth_per_class * 2)
            for _ in range(n_aug):
                samples.append((augment(random.choice(crops).copy()), ci))
            n_synth = max(10, synth_per_class // 4)
            for _ in range(n_synth):
                samples.append((augment(render_synthetic(char)), ci))
            print(f"  '{char}': {n_real} real + {n_aug} aug + {n_synth} synth", flush=True)
        else:
            for _ in range(synth_per_class):
                samples.append((augment(render_synthetic(char)), ci))

    random.shuffle(samples)
    print(f"[INFO] Total dataset: {len(samples)} ({len(annotated_chars)} chars have real data)", flush=True)
    return samples


def main():
    p = argparse.ArgumentParser()
    p.add_argument('annotations')
    p.add_argument('output_dir')
    p.add_argument('--pretrained', default=None)
    p.add_argument('--epochs', type=int, default=80)
    p.add_argument('--augments', type=int, default=80)
    p.add_argument('--batch', type=int, default=32)
    p.add_argument('--lr', type=float, default=0.0005)
    args = p.parse_args()

    raw_text = open(args.annotations, 'r', encoding='utf-8-sig').read()
    anns = json.loads(raw_text)
    anns = [a for a in anns if a.get('Label', '').strip() not in ('', '?')]
    print(f"[INFO] {len(anns)} valid annotations", flush=True)
    if not anns:
        print("[ERROR] No annotations!", file=sys.stderr, flush=True)
        sys.exit(1)

    device = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
    print(f"[INFO] Device: {device}", flush=True)

    real_crops = collect_real_crops(anns)
    if not real_crops:
        print("[ERROR] No crops collected!", file=sys.stderr, flush=True)
        sys.exit(1)

    print(f"\n[INFO] Building balanced {N_CLASSES}-class dataset...", flush=True)
    samples = build_dataset(real_crops, synth_per_class=args.augments)

    split = max(1, int(len(samples) * 0.85))
    train_s = samples[:split]
    val_s = samples[split:] if split < len(samples) else samples[-max(1, len(samples)//10):]

    bs = min(args.batch, max(2, len(train_s) // 4))
    tr_dl = DataLoader(DS(train_s), bs, shuffle=True, drop_last=len(train_s) > bs, num_workers=0)
    va_dl = DataLoader(DS(val_s), min(bs, len(val_s)), shuffle=False, num_workers=0)

    model = TinyCNN(N_CLASSES).to(device)

    # Find pretrained base
    pretrained_path = args.pretrained
    if pretrained_path is None:
        for candidate in [
            os.path.join(os.path.dirname(args.annotations), '..', 'Models', 'pretrained_base.pth'),
            os.path.join(os.path.dirname(args.annotations), '..', '..', 'Models', 'pretrained_base.pth'),
            os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'Models', 'pretrained_base.pth'),
            os.path.join(os.path.dirname(os.path.abspath(__file__)), 'pretrained_base.pth'),
            'Models/pretrained_base.pth', 'pretrained_base.pth',
        ]:
            if os.path.exists(candidate):
                pretrained_path = candidate; break

    if pretrained_path and os.path.exists(pretrained_path):
        print(f"[INFO] Loading pretrained base: {pretrained_path}", flush=True)
        ckpt = torch.load(pretrained_path, map_location=device, weights_only=False)
        # Handle charset size mismatch (old 36 vs new 44)
        saved_chars = ckpt.get('chars', FULL_CHARS)
        saved_n = ckpt.get('n_classes', len(saved_chars))
        if saved_n == N_CLASSES:
            model.load_state_dict(ckpt['model_state'])
            print(f"[INFO] Pretrained base loaded ({saved_n} classes)", flush=True)
        else:
            # Load backbone only, skip head (different output size)
            saved_sd = ckpt['model_state']
            backbone_sd = {k: v for k, v in saved_sd.items() if k.startswith('backbone.')}
            model.load_state_dict(backbone_sd, strict=False)
            print(f"[INFO] Pretrained backbone loaded (base had {saved_n} classes, now {N_CLASSES})", flush=True)
    else:
        print(f"[WARN] No pretrained_base.pth found — training from scratch", flush=True)
        print(f"[WARN] Run generate_pretrained.py first for best results!", flush=True)

    crit = nn.CrossEntropyLoss(label_smoothing=0.1)
    opt = optim.AdamW(model.parameters(), lr=args.lr, weight_decay=1e-4)
    sch = optim.lr_scheduler.CosineAnnealingLR(opt, T_max=args.epochs)

    best_val, best_sd = 0.0, None
    for ep in range(1, args.epochs + 1):
        model.train(); tc = tn = 0
        for imgs, lbls in tr_dl:
            imgs, lbls = imgs.to(device), lbls.to(device)
            opt.zero_grad(); loss = crit(model(imgs), lbls); loss.backward(); opt.step()
            with torch.no_grad(): tc += (model(imgs).argmax(1)==lbls).sum().item(); tn += len(lbls)
        model.eval(); vc = vn = 0
        with torch.no_grad():
            for imgs, lbls in va_dl:
                imgs, lbls = imgs.to(device), lbls.to(device)
                vc += (model(imgs).argmax(1)==lbls).sum().item(); vn += len(lbls)
        sch.step()
        ta = tc/max(tn,1)*100; va = vc/max(vn,1)*100
        print(f"  Epoch {ep:3d}/{args.epochs} | Train:{ta:.1f}% | Val:{va:.1f}%", flush=True)
        if va >= best_val:
            best_val = va; best_sd = {k: v.clone() for k, v in model.state_dict().items()}

    if best_sd: model.load_state_dict(best_sd)

    os.makedirs(args.output_dir, exist_ok=True)
    model_path = os.path.join(args.output_dir, 'char_classifier.pth')
    torch.save({
        'model_state': model.state_dict(),
        'chars': FULL_CHARS,
        'n_classes': N_CLASSES,
        'img_size': IMG_SIZE,
        'annotated_chars': sorted(real_crops.keys()),
        'annotation_count': sum(len(v) for v in real_crops.values()),
    }, model_path)

    config = {
        'mode': 'direct_classifier',
        'model_file': 'char_classifier.pth',
        'chars': FULL_CHARS,
        'n_classes': N_CLASSES,
        'all_trained_chars': FULL_CHARS,
        'annotated_chars': sorted(real_crops.keys()),
        'annotation_count': sum(len(v) for v in real_crops.values()),
        'img_size': IMG_SIZE,
    }
    json.dump(config, open(os.path.join(args.output_dir, 'model_config.json'), 'w'), indent=2)

    print(f"\n{'='*60}", flush=True)
    print(f"[DONE] {N_CLASSES}-class model trained", flush=True)
    print(f"[DONE] Real data for: {sorted(real_crops.keys())}", flush=True)
    print(f"[DONE] Best val: {best_val:.1f}%", flush=True)
    print(f"{'='*60}", flush=True)
    print(f"TRAINING_COMPLETE:{best_val:.1f}", flush=True)

if __name__ == '__main__':
    main()