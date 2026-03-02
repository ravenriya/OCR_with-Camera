# -*- coding: utf-8 -*-
"""
train_last_layer.py  -  PixTech OCR Trainer v3
================================================
HOW IT WORKS:
1. You annotate characters with their CORRECT label on the image
2. Trainer runs EasyOCR on the FULL IMAGE to see what EasyOCR reads
3. Matches annotations to the nearest EasyOCR character by position
4. If they disagree -> that's a real confusion -> train a resolver

ANNOTATION TIPS:
  - Annotate chars EasyOCR gets WRONG with the CORRECT label
  - ALSO annotate some correctly-read chars of the same type
    e.g. if 'D' is misread as '0', annotate the wrong 'D' AND a real '0'
  - This gives the resolver examples of both sides
"""
import argparse, json, os, random, sys, time, io
import cv2, numpy as np
import torch
import torch.nn as nn
import torch.optim as optim
from torch.utils.data import Dataset, DataLoader

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8', errors='replace')
os.environ['TQDM_DISABLE'] = '1'
random.seed(42); np.random.seed(42); torch.manual_seed(42)

IMG_SIZE = 48


class TinyCNN(nn.Module):
    def __init__(self, n):
        super().__init__()
        self.net = nn.Sequential(
            nn.Conv2d(1, 32, 3, padding=1), nn.BatchNorm2d(32), nn.ReLU(), nn.MaxPool2d(2),
            nn.Conv2d(32, 64, 3, padding=1), nn.BatchNorm2d(64), nn.ReLU(), nn.MaxPool2d(2),
            nn.Conv2d(64, 128, 3, padding=1), nn.BatchNorm2d(128), nn.ReLU(), nn.AdaptiveAvgPool2d(4),
            nn.Flatten(),
            nn.Linear(128 * 16, 256), nn.ReLU(), nn.Dropout(0.4),
            nn.Linear(256, n)
        )
    def forward(self, x):
        return self.net(x)


def augment(img):
    h, w = img.shape
    M = cv2.getRotationMatrix2D((w/2, h/2), random.uniform(-20, 20), 1.0)
    img = cv2.warpAffine(img, M, (w, h), borderMode=cv2.BORDER_REPLICATE)
    if random.random() < 0.5:
        mg = max(1, int(min(h, w) * 0.12))
        src = np.float32([[0,0],[w,0],[w,h],[0,h]])
        dst = np.float32([
            [random.randint(0,mg), random.randint(0,mg)],
            [w-random.randint(0,mg), random.randint(0,mg)],
            [w-random.randint(0,mg), h-random.randint(0,mg)],
            [random.randint(0,mg), h-random.randint(0,mg)]
        ])
        img = cv2.warpPerspective(img, cv2.getPerspectiveTransform(src, dst),
                                  (w, h), borderMode=cv2.BORDER_REPLICATE)
    img = np.clip(img.astype(np.int32)*random.uniform(0.5,1.6)
                  + random.randint(-40,40), 0, 255).astype(np.uint8)
    if random.random() < 0.5:
        img = cv2.GaussianBlur(img, (random.choice([3,5]),)*2, 0)
    if random.random() < 0.5:
        noise = np.random.normal(0, random.uniform(5,20), img.shape)
        img = np.clip(img.astype(np.int32)+noise, 0, 255).astype(np.uint8)
    if random.random() < 0.3:
        k = np.ones((random.randint(1,2),)*2, np.uint8)
        img = cv2.erode(img, k) if random.random() < 0.5 else cv2.dilate(img, k)
    return img


def to_tensor(crop):
    img = cv2.resize(crop, (IMG_SIZE, IMG_SIZE)).astype(np.float32) / 127.5 - 1.0
    return torch.tensor(img).unsqueeze(0)


class DS(Dataset):
    def __init__(self, s):
        self.s = s
    def __len__(self):
        return len(self.s)
    def __getitem__(self, i):
        img, lbl = self.s[i]
        return to_tensor(img), lbl


def find_confusion_groups(annotations):
    """
    Run EasyOCR on full images, match annotations by position,
    find what EasyOCR actually gets wrong.
    """
    import easyocr
    print("[INFO] Running EasyOCR on full images to detect confusions...", flush=True)
    reader = easyocr.Reader(['en'], gpu=torch.cuda.is_available(), verbose=False)

    by_image = {}
    for a in annotations:
        ip = a.get('ImagePath', '')
        if ip:
            by_image.setdefault(ip, []).append(a)

    confusion_pairs = []

    for img_path, img_anns in by_image.items():
        bgr = cv2.imread(img_path)
        if bgr is None:
            print(f"[WARN] Cannot read: {img_path}", flush=True)
            continue

        results = reader.readtext(bgr, detail=1)
        if not results:
            print(f"[INFO] No OCR text in {os.path.basename(img_path)}", flush=True)
            continue

        # Build per-character positions from EasyOCR
        ocr_chars = []
        for (bbox, text, conf) in results:
            if not text.strip():
                continue
            xs = [p[0] for p in bbox]
            ys = [p[1] for p in bbox]
            x_min, y_min = min(xs), min(ys)
            w = max(xs) - x_min
            h = max(ys) - y_min
            char_w = w / max(len(text), 1)
            for i, ch in enumerate(text):
                ocr_chars.append({
                    'char': ch.upper(),
                    'cx': x_min + i * char_w + char_w / 2,
                    'cy': y_min + h / 2,
                })

        print(f"[INFO] {os.path.basename(img_path)}: "
              f"EasyOCR='{(''.join(oc['char'] for oc in ocr_chars))}'", flush=True)

        for a in img_anns:
            correct = a.get('Label', '').strip().upper()
            if not correct or len(correct) != 1:
                continue
            ax = float(a['X']) + float(a['Width']) / 2
            ay = float(a['Y']) + float(a['Height']) / 2

            best_dist = float('inf')
            best_ocr = None
            for oc in ocr_chars:
                d = ((ax - oc['cx'])**2 + (ay - oc['cy'])**2)**0.5
                if d < best_dist:
                    best_dist = d
                    best_ocr = oc

            max_dist = max(float(a['Width']), float(a['Height'])) * 2.0
            if best_ocr and best_dist < max_dist:
                ocr_char = best_ocr['char']
                if ocr_char != correct:
                    confusion_pairs.append((correct, ocr_char))
                    print(f"[INFO]   '{correct}' annotated, EasyOCR='{ocr_char}' -> CONFUSED!",
                          flush=True)
                else:
                    print(f"[INFO]   '{correct}' annotated, EasyOCR='{ocr_char}' -> correct",
                          flush=True)
            else:
                print(f"[INFO]   '{correct}' annotated, no nearby EasyOCR match", flush=True)

    # Merge into groups
    groups = []
    for correct, wrong in confusion_pairs:
        pair = {correct, wrong}
        merged = False
        for i, g in enumerate(groups):
            if g & pair:
                groups[i] = g | pair
                merged = True
                break
        if not merged:
            groups.append(pair)
    changed = True
    while changed:
        changed = False
        new_groups = []
        for g in groups:
            merged = False
            for i, ng in enumerate(new_groups):
                if ng & g:
                    new_groups[i] = ng | g
                    merged = True
                    changed = True
                    break
            if not merged:
                new_groups.append(g)
        groups = new_groups

    char_to_group = {}
    for group in groups:
        for ch in group:
            char_to_group[ch] = sorted(group)

    print(f"\n[INFO] Confusion groups: {[sorted(g) for g in groups]}", flush=True)
    return groups, char_to_group, confusion_pairs


def collect_crops(annotations):
    raw = {}
    for a in annotations:
        lbl = a.get('Label', '').strip().upper()
        if not lbl or len(lbl) != 1:
            continue
        bgr = cv2.imread(a['ImagePath'])
        if bgr is None:
            continue
        gray = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)
        x, y = int(float(a['X'])), int(float(a['Y']))
        w, h = int(float(a['Width'])), int(float(a['Height']))
        pad = max(4, min(w, h) // 6)
        y1, y2 = max(0, y-pad), min(gray.shape[0], y+h+pad)
        x1, x2 = max(0, x-pad), min(gray.shape[1], x+w+pad)
        crop = gray[y1:y2, x1:x2]
        if crop.size > 0 and min(crop.shape) >= 4:
            raw.setdefault(lbl, []).append(crop)
    return raw


def train_resolver(chars, raw, args, device):
    available = [c for c in chars if c in raw]
    if len(available) < 2:
        return None, None
    chars = sorted(available)
    cidx = {c: i for i, c in enumerate(chars)}
    n = len(chars)
    print(f"\n[INFO] Training resolver for: {chars}", flush=True)

    samples = []
    for lbl in chars:
        idx = cidx[lbl]
        for c in raw[lbl]:
            samples.append((c, idx))
        for _ in range(args.augments):
            samples.append((augment(random.choice(raw[lbl]).copy()), idx))
        print(f"[INFO]   '{lbl}': {len(raw[lbl])} real + {args.augments} aug", flush=True)

    random.shuffle(samples)
    split = max(1, int(len(samples) * 0.85))
    val_s = samples[split:] if split < len(samples) else samples[-max(1, len(samples)//10):]
    bs = min(args.batch, max(2, len(samples[:split])//2))

    tr_dl = DataLoader(DS(samples[:split]), bs, shuffle=True, drop_last=True, num_workers=0)
    va_dl = DataLoader(DS(val_s), bs, shuffle=False, drop_last=False, num_workers=0)

    model = TinyCNN(n).to(device)
    crit = nn.CrossEntropyLoss(label_smoothing=0.1)
    opt = optim.AdamW(model.parameters(), lr=args.lr, weight_decay=1e-4)
    sch = optim.lr_scheduler.CosineAnnealingLR(opt, T_max=args.epochs)

    best = 0.0; best_sd = None
    for ep in range(1, args.epochs+1):
        model.train(); tc = tn = 0
        for imgs, lbls in tr_dl:
            imgs, lbls = imgs.to(device), lbls.to(device)
            opt.zero_grad()
            out = model(imgs)
            crit(out, lbls).backward()
            opt.step()
            tc += (out.argmax(1)==lbls).sum().item(); tn += len(lbls)
        model.eval(); vc = vn = 0
        with torch.no_grad():
            for imgs, lbls in va_dl:
                imgs, lbls = imgs.to(device), lbls.to(device)
                vc += (model(imgs).argmax(1)==lbls).sum().item(); vn += len(lbls)
        sch.step()
        ta, va = tc/max(tn,1)*100, vc/max(vn,1)*100
        print(f"  Epoch {ep:3d}/{args.epochs} | Train:{ta:.1f}% | Val:{va:.1f}%", flush=True)
        if va >= best:
            best = va
            best_sd = {k: v.clone() for k, v in model.state_dict().items()}

    if best_sd:
        model.load_state_dict(best_sd)
    print(f"[INFO] Resolver {chars} best val: {best:.1f}%", flush=True)
    return model, chars


def main():
    p = argparse.ArgumentParser()
    p.add_argument('annotations')
    p.add_argument('output_dir')
    p.add_argument('--epochs', type=int, default=80)
    p.add_argument('--augments', type=int, default=120)
    p.add_argument('--batch', type=int, default=32)
    p.add_argument('--lr', type=float, default=0.001)
    args = p.parse_args()

    anns = json.load(open(args.annotations, 'r'))
    if not anns:
        print("[ERROR] No annotations!", file=sys.stderr, flush=True); sys.exit(1)
    print(f"[INFO] {len(anns)} annotations loaded", flush=True)

    device = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
    print(f"[INFO] Device: {device}", flush=True)

    confusion_groups, char_to_group, confusion_pairs = find_confusion_groups(anns)
    raw = collect_crops(anns)
    if not raw:
        print("[ERROR] No crops!", file=sys.stderr, flush=True); sys.exit(1)
    print(f"[INFO] Annotated chars: {sorted(raw.keys())}", flush=True)

    os.makedirs(args.output_dir, exist_ok=True)

    saved_groups = []
    replacement_map = {}

    for group in confusion_groups:
        group_chars = sorted(group)
        available = [c for c in group_chars if c in raw]
        if len(available) >= 2:
            model, chars = train_resolver(group_chars, raw, args, device)
            if model is not None:
                gn = '_'.join(sorted(chars))
                mp = os.path.join(args.output_dir, f'resolver_{gn}.pth')
                torch.save({
                    'model_state': model.state_dict(),
                    'chars': chars,
                    'n_classes': len(chars),
                    'img_size': IMG_SIZE,
                }, mp)
                saved_groups.append({'model_file': f'resolver_{gn}.pth',
                                     'chars': chars, 'n_classes': len(chars)})
                print(f"[INFO] Saved resolver: {gn}", flush=True)
        else:
            print(f"[WARN] Group {group_chars}: only crops for {available}, "
                  f"need both sides for resolver", flush=True)

    # Build replacement map for chars without resolvers
    from collections import Counter
    w2c = {}
    for correct, wrong in confusion_pairs:
        w2c.setdefault(wrong, []).append(correct)
    resolver_chars = {ch for sg in saved_groups for ch in sg['chars']}
    for wrong_ch, corrs in w2c.items():
        if wrong_ch not in resolver_chars:
            best, cnt = Counter(corrs).most_common(1)[0]
            replacement_map[wrong_ch] = best
            print(f"[INFO] Replacement (no resolver): '{wrong_ch}'->'{best}' ({cnt}x)",
                  flush=True)

    config = {
        'groups': saved_groups,
        'all_trained_chars': sorted(raw.keys()),
        'confusion_groups': [sorted(g) for g in confusion_groups],
        'char_to_group': char_to_group,
        'replacement_map': replacement_map,
        'img_size': IMG_SIZE,
    }
    json.dump(config, open(os.path.join(args.output_dir, 'model_config.json'), 'w'), indent=2)
    json.dump(anns, open(os.path.join(args.output_dir, 'annotations_ref.json'), 'w'), indent=2)

    print(f"\n{'='*60}", flush=True)
    print(f"[DONE] Training complete!", flush=True)
    print(f"[DONE] Resolvers: {len(saved_groups)}", flush=True)
    for sg in saved_groups:
        print(f"[DONE]   {sg['chars']}", flush=True)
    if replacement_map:
        print(f"[DONE] Replacements:", flush=True)
        for w, c in replacement_map.items():
            print(f"[DONE]   '{w}'->'{c}'", flush=True)
    if not confusion_groups:
        print(f"[DONE]   No confusions found - annotate chars EasyOCR gets WRONG", flush=True)
    print(f"{'='*60}", flush=True)
    print(f"TRAINING_COMPLETE:100.0", flush=True)


if __name__ == '__main__':
    main()