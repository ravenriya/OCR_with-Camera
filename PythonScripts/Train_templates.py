# -*- coding: utf-8 -*-
"""
train_templates.py  -  PixTech Template Matching Training v1.0
"""

import json, os, sys, io, random, math, time, pickle
import numpy as np
import cv2
from pathlib import Path
from collections import Counter, defaultdict

import torch
import torch.nn as nn
import torch.optim as optim
from torch.utils.data import Dataset, DataLoader

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8', errors='replace')

IMG_SIZE = 48
TEMPLATE_SIZE = 64
FULL_CHARS = list("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ/-.,()#:&?")
N_CLASSES = len(FULL_CHARS)
MIN_TEMPLATES_PER_CHAR = 3
MAX_TEMPLATES_PER_CHAR = 10


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

    def forward(self, x):
        return self.head(self.backbone(x))


def preprocess_metal(gray, level='standard'):
    if gray.size == 0 or min(gray.shape[:2]) < 4:
        return gray

    if level == 'light':
        clahe = cv2.createCLAHE(clipLimit=3.0, tileGridSize=(4, 4))
        out = clahe.apply(gray)
        out = cv2.bilateralFilter(out, 7, 50, 50)
        return out

    if level == 'standard':
        clahe = cv2.createCLAHE(clipLimit=5.0, tileGridSize=(4, 4))
        out = clahe.apply(gray)
        out = cv2.bilateralFilter(out, 9, 75, 75)
        blur = cv2.GaussianBlur(out, (0, 0), 2.0)
        out = cv2.addWeighted(out, 1.8, blur, -0.8, 0)
        return cv2.normalize(out, None, 0, 255, cv2.NORM_MINMAX).astype(np.uint8)

    if level == 'aggressive':
        clahe = cv2.createCLAHE(clipLimit=8.0, tileGridSize=(3, 3))
        out = clahe.apply(gray)
        out = cv2.bilateralFilter(out, 9, 75, 75)
        blur = cv2.GaussianBlur(out, (0, 0), 3.0)
        out = cv2.addWeighted(out, 2.2, blur, -1.2, 0)
        out = cv2.normalize(out, None, 0, 255, cv2.NORM_MINMAX).astype(np.uint8)
        return out

    if level == 'edge':
        blurred = cv2.GaussianBlur(gray, (3, 3), 0)
        sx = cv2.Sobel(blurred, cv2.CV_64F, 1, 0, ksize=3)
        sy = cv2.Sobel(blurred, cv2.CV_64F, 0, 1, ksize=3)
        mag = np.sqrt(sx**2 + sy**2)
        mag = cv2.normalize(mag, None, 0, 255, cv2.NORM_MINMAX).astype(np.uint8)
        clahe = cv2.createCLAHE(clipLimit=4.0, tileGridSize=(4, 4))
        enhanced = clahe.apply(gray)
        return cv2.addWeighted(enhanced, 0.6, mag, 0.4, 0)

    return gray


def extract_crop(image_path, x_frac, y_frac, w_frac, h_frac, angle=0,
                 target_size=None, pad_factor=0.15, preprocess='standard'):
    img = cv2.imread(image_path)
    if img is None:
        return None

    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    ih, iw = gray.shape[:2]

    x = int(x_frac * iw)
    y = int(y_frac * ih)
    w = int(w_frac * iw)
    h = int(h_frac * ih)

    if w < 3 or h < 3:
        return None

    pad_x = max(3, int(w * pad_factor))
    pad_y = max(3, int(h * pad_factor))
    x1 = max(0, x - pad_x)
    y1 = max(0, y - pad_y)
    x2 = min(iw, x + w + pad_x)
    y2 = min(ih, y + h + pad_y)

    crop = gray[y1:y2, x1:x2]
    if crop.size == 0 or min(crop.shape[:2]) < 4:
        return None

    if abs(angle) > 1:
        center = (crop.shape[1] // 2, crop.shape[0] // 2)
        M = cv2.getRotationMatrix2D(center, -angle, 1.0)
        cos_a = abs(M[0, 0])
        sin_a = abs(M[0, 1])
        new_w = int(crop.shape[0] * sin_a + crop.shape[1] * cos_a)
        new_h = int(crop.shape[0] * cos_a + crop.shape[1] * sin_a)
        M[0, 2] += (new_w - crop.shape[1]) / 2
        M[1, 2] += (new_h - crop.shape[0]) / 2
        crop = cv2.warpAffine(crop, M, (new_w, new_h), borderMode=cv2.BORDER_REPLICATE)

    crop = preprocess_metal(crop, level=preprocess)

    if target_size:
        crop = cv2.resize(crop, (target_size, target_size), interpolation=cv2.INTER_AREA)

    return crop


def build_templates(crops_by_label):
    templates = {}

    for label, crops in crops_by_label.items():
        label_templates = []

        if len(crops) == 0:
            continue

        resized = []
        for c in crops:
            r = cv2.resize(c, (TEMPLATE_SIZE, TEMPLATE_SIZE), interpolation=cv2.INTER_AREA)
            resized.append(r.astype(np.float32))

        if len(resized) <= 3:
            for r in resized:
                label_templates.append(r.astype(np.uint8))
                edge_var = preprocess_metal(r.astype(np.uint8), level='edge')
                label_templates.append(edge_var)
        else:
            stack = np.stack(resized, axis=0)
            avg = np.mean(stack, axis=0).astype(np.uint8)
            label_templates.append(avg)

            med = np.median(stack, axis=0).astype(np.uint8)
            label_templates.append(med)

            edge_avg = preprocess_metal(avg, level='edge')
            label_templates.append(edge_avg)

            for r in resized[:MAX_TEMPLATES_PER_CHAR - 3]:
                label_templates.append(r.astype(np.uint8))

        templates[label] = label_templates[:MAX_TEMPLATES_PER_CHAR]
        print(f"  '{label}': {len(crops)} crops -> {len(templates[label])} templates", flush=True)

    return templates


def create_synthetic_templates(chars_to_generate):
    synth = {}
    fonts = [
        cv2.FONT_HERSHEY_SIMPLEX,
        cv2.FONT_HERSHEY_DUPLEX,
        cv2.FONT_HERSHEY_COMPLEX,
    ]

    for char in chars_to_generate:
        templates = []
        for font in fonts:
            for thickness in [2, 3]:
                canvas = np.zeros((TEMPLATE_SIZE, TEMPLATE_SIZE), dtype=np.uint8)
                canvas[:] = random.randint(20, 50)
                scale = TEMPLATE_SIZE / 40.0
                (tw, th), _ = cv2.getTextSize(char, font, scale, thickness)
                tx = (TEMPLATE_SIZE - tw) // 2
                ty = (TEMPLATE_SIZE + th) // 2
                cv2.putText(canvas, char, (tx + 1, ty + 1), font, scale, 50, thickness, cv2.LINE_AA)
                cv2.putText(canvas, char, (tx, ty), font, scale, 200, thickness, cv2.LINE_AA)
                noise = np.random.normal(0, 8, canvas.shape).astype(np.float32)
                canvas = np.clip(canvas.astype(np.float32) + noise, 0, 255).astype(np.uint8)
                canvas = cv2.GaussianBlur(canvas, (3, 3), 0)
                templates.append(canvas)

        synth[char] = templates[:MIN_TEMPLATES_PER_CHAR]

    return synth


def augment_crop(gray_img):
    h, w = gray_img.shape[:2]
    img = gray_img.copy().astype(np.float32)

    if random.random() < 0.7:
        img = np.clip(img + random.uniform(-40, 40), 0, 255)

    if random.random() < 0.7:
        factor = random.uniform(0.6, 1.6)
        mean = img.mean()
        img = np.clip((img - mean) * factor + mean, 0, 255)

    if random.random() < 0.5:
        noise = np.random.normal(0, random.uniform(5, 25), img.shape)
        img = np.clip(img + noise, 0, 255)

    if random.random() < 0.5:
        angle = random.uniform(-8, 8)
        M = cv2.getRotationMatrix2D((w // 2, h // 2), angle, 1.0)
        img = cv2.warpAffine(img.astype(np.uint8), M, (w, h),
                             borderMode=cv2.BORDER_REPLICATE).astype(np.float32)

    if random.random() < 0.4:
        scale = random.uniform(0.85, 1.15)
        nw, nh = max(8, int(w * scale)), max(8, int(h * scale))
        resized = cv2.resize(img.astype(np.uint8), (nw, nh))
        canvas = np.full((h, w), img.mean(), dtype=np.float32)
        yo, xo = max(0, (h - nh) // 2), max(0, (w - nw) // 2)
        sy, sx = max(0, (nh - h) // 2), max(0, (nw - w) // 2)
        ph, pw = min(nh, h), min(nw, w)
        canvas[yo:yo + ph, xo:xo + pw] = resized[sy:sy + ph, sx:sx + pw]
        img = canvas

    if random.random() < 0.3:
        img = cv2.GaussianBlur(img.astype(np.uint8),
                               (random.choice([3, 5]),) * 2, 0).astype(np.float32)

    if random.random() < 0.25:
        kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (2, 2))
        if random.random() < 0.5:
            img = cv2.erode(img.astype(np.uint8), kernel).astype(np.float32)
        else:
            img = cv2.dilate(img.astype(np.uint8), kernel).astype(np.float32)

    if random.random() < 0.4:
        gamma = random.uniform(0.6, 1.5)
        table = np.array([((i / 255.0) ** gamma) * 255 for i in range(256)]).astype(np.uint8)
        img = cv2.LUT(img.astype(np.uint8), table).astype(np.float32)

    return np.clip(img, 0, 255).astype(np.uint8)


class CharDataset(Dataset):
    def __init__(self, samples, chars, augments_per_real=0):
        self.chars = chars
        self.char_to_idx = {c: i for i, c in enumerate(chars)}
        self.items = []

        real = [(img, lbl) for img, lbl, is_real in samples if is_real]
        synth = [(img, lbl) for img, lbl, is_real in samples if not is_real]

        for img, lbl in real:
            self.items.append((img, lbl, 3.0))
            for _ in range(augments_per_real):
                self.items.append((augment_crop(img), lbl, 3.0))

        for img, lbl in synth:
            self.items.append((img, lbl, 1.0))

    def __len__(self):
        return len(self.items)

    def __getitem__(self, idx):
        img, lbl, weight = self.items[idx]
        tensor = torch.tensor(
            cv2.resize(img, (IMG_SIZE, IMG_SIZE)).astype(np.float32) / 127.5 - 1.0
        ).unsqueeze(0)
        label_idx = self.char_to_idx.get(lbl.upper(), 0)
        return tensor, label_idx, weight


def generate_synthetic_cnn(char, size=IMG_SIZE):
    canvas = np.random.randint(20, 60, (size, size), dtype=np.uint8)
    font = random.choice([cv2.FONT_HERSHEY_SIMPLEX, cv2.FONT_HERSHEY_DUPLEX,
                          cv2.FONT_HERSHEY_COMPLEX])
    scale = random.uniform(0.9, 1.6)
    thickness = random.randint(1, 3)
    (tw, th), _ = cv2.getTextSize(char, font, scale, thickness)
    tx = (size - tw) // 2
    ty = (size + th) // 2
    cv2.putText(canvas, char, (tx + 1, ty + 1), font, scale,
                int(random.uniform(30, 70)), thickness, cv2.LINE_AA)
    cv2.putText(canvas, char, (tx, ty), font, scale,
                int(random.uniform(160, 230)), thickness, cv2.LINE_AA)
    noise = np.random.normal(0, random.uniform(5, 15), canvas.shape).astype(np.float32)
    canvas = np.clip(canvas.astype(np.float32) + noise, 0, 255).astype(np.uint8)
    if random.random() < 0.5:
        canvas = cv2.GaussianBlur(canvas, (3, 3), 0)
    return canvas


def main():
    import argparse

    parser = argparse.ArgumentParser(description='PixTech Template Training')
    parser.add_argument('annotations', help='Path to all_annotations.json')
    parser.add_argument('output_dir', help='Output directory for model')
    parser.add_argument('--epochs', type=int, default=50)
    parser.add_argument('--batch', type=int, default=32)
    parser.add_argument('--augments', type=int, default=80)
    parser.add_argument('--pretrained', type=str, default=None)
    args = parser.parse_args()

    print(f"{'=' * 60}", flush=True)
    print(f"PixTech Template Matching Training v1.0", flush=True)
    print(f"{'=' * 60}", flush=True)
    print(f"Annotations: {args.annotations}", flush=True)
    print(f"Output:      {args.output_dir}", flush=True)
    print(f"{'=' * 60}", flush=True)

    device = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
    print(f"Device: {device}", flush=True)

    with open(args.annotations, 'r') as f:
        annotations = json.load(f)
    print(f"\nLoaded {len(annotations)} annotations", flush=True)

    crops_by_label = defaultdict(list)
    cnn_samples = []
    failed = 0
    anno_dir = str(Path(args.annotations).parent)

    for ann in annotations:
        label = ann.get('Label', '').strip().upper()
        if not label or label not in FULL_CHARS:
            continue

        img_path = ann.get('ImagePath', '')
        if not img_path:
            continue

        paths_to_try = [
            img_path,
            os.path.join(anno_dir, 'images', os.path.basename(img_path)),
            os.path.join(anno_dir, '..', os.path.basename(img_path)),
        ]

        found = False
        for p in paths_to_try:
            if not os.path.exists(p):
                continue

            crop_hires = extract_crop(
                p, ann.get('X', 0), ann.get('Y', 0),
                ann.get('Width', 0), ann.get('Height', 0),
                ann.get('Angle', 0),
                target_size=TEMPLATE_SIZE, preprocess='standard'
            )
            crop_edge = extract_crop(
                p, ann.get('X', 0), ann.get('Y', 0),
                ann.get('Width', 0), ann.get('Height', 0),
                ann.get('Angle', 0),
                target_size=TEMPLATE_SIZE, preprocess='edge'
            )
            crop_cnn = extract_crop(
                p, ann.get('X', 0), ann.get('Y', 0),
                ann.get('Width', 0), ann.get('Height', 0),
                ann.get('Angle', 0),
                target_size=IMG_SIZE, preprocess='standard'
            )

            if crop_hires is not None:
                crops_by_label[label].append(crop_hires)
                if crop_edge is not None:
                    crops_by_label[label].append(crop_edge)
                found = True

            if crop_cnn is not None:
                cnn_samples.append((crop_cnn, label, True))
                found = True

            if found:
                break

        if not found:
            failed += 1

    label_counts = {k: len(v) for k, v in crops_by_label.items()}
    annotated_chars = sorted(label_counts.keys())
    total_crops = sum(label_counts.values())

    print(f"\nExtracted {total_crops} crops from {len(annotated_chars)} chars ({failed} failed)", flush=True)
    print(f"Distribution: {dict(sorted(label_counts.items()))}", flush=True)

    if total_crops == 0:
        print("ERROR: No valid crops! Check image paths in annotations.", flush=True)
        sys.exit(1)

    print(f"\n{'=' * 60}", flush=True)
    print(f"Phase 1: Building Template Library", flush=True)
    print(f"{'=' * 60}", flush=True)

    real_templates = build_templates(crops_by_label)

    missing_chars = [c for c in FULL_CHARS if c not in real_templates]
    if missing_chars:
        print(f"\nGenerating synthetic templates for: {missing_chars}", flush=True)
        synth_templates = create_synthetic_templates(missing_chars)
    else:
        synth_templates = {}

    all_templates = {}
    for ch in FULL_CHARS:
        if ch in real_templates:
            all_templates[ch] = {'templates': real_templates[ch], 'is_real': True}
        elif ch in synth_templates:
            all_templates[ch] = {'templates': synth_templates[ch], 'is_real': False}

    n_real_tpl = sum(1 for v in all_templates.values() if v['is_real'])
    n_synth_tpl = sum(1 for v in all_templates.values() if not v['is_real'])
    total_tpl = sum(len(v['templates']) for v in all_templates.values())
    print(f"\nTemplate library: {total_tpl} templates ({n_real_tpl} real chars, {n_synth_tpl} synthetic chars)", flush=True)

    print(f"\n{'=' * 60}", flush=True)
    print(f"Phase 2: Training CNN Backup Classifier", flush=True)
    print(f"{'=' * 60}", flush=True)

    synth_per_class = max(20, len(cnn_samples) // len(FULL_CHARS))
    for ch in FULL_CHARS:
        n = synth_per_class // 3 if ch in annotated_chars else synth_per_class
        for _ in range(n):
            cnn_samples.append((generate_synthetic_cnn(ch), ch, False))

    random.shuffle(cnn_samples)

    by_label = defaultdict(list)
    for s in cnn_samples:
        by_label[s[1].upper()].append(s)

    train_samples, val_samples = [], []
    for lbl, items in by_label.items():
        random.shuffle(items)
        n_val = max(1, int(len(items) * 0.15))
        val_samples.extend(items[:n_val])
        train_samples.extend(items[n_val:])

    train_ds = CharDataset(train_samples, FULL_CHARS, augments_per_real=args.augments)
    val_ds = CharDataset(val_samples, FULL_CHARS, augments_per_real=0)
    train_loader = DataLoader(train_ds, batch_size=args.batch, shuffle=True, num_workers=0, pin_memory=True)
    val_loader = DataLoader(val_ds, batch_size=args.batch, shuffle=False, num_workers=0, pin_memory=True)

    model = TinyCNN(N_CLASSES).to(device)

    if args.pretrained and os.path.exists(args.pretrained):
        print(f"[INFO] Loading pretrained weights from {args.pretrained}", flush=True)
        try:
            checkpoint = torch.load(args.pretrained, map_location=device, weights_only=False)
            pretrained_dict = checkpoint['model_state'] if 'model_state' in checkpoint else checkpoint
            model_dict = model.state_dict()
            filtered_dict = {k: v for k, v in pretrained_dict.items()
                             if k in model_dict and v.size() == model_dict[k].size()}
            model_dict.update(filtered_dict)
            model.load_state_dict(model_dict)
            print(f"[SUCCESS] Transferred {len(filtered_dict)}/{len(model_dict)} layers.", flush=True)
        except Exception as ex:
            print(f"Warning: pretrained load failed: {ex}", flush=True)

    class_weights = torch.ones(N_CLASSES, device=device)
    for i, ch in enumerate(FULL_CHARS):
        if ch in annotated_chars:
            class_weights[i] = 3.0

    optimizer = optim.AdamW(model.parameters(), lr=1e-3, weight_decay=1e-4)
    scheduler = optim.lr_scheduler.CosineAnnealingLR(optimizer, T_max=args.epochs, eta_min=1e-5)

    best_val_acc = 0.0
    best_state = None
    patience = 12
    no_improve = 0

    for epoch in range(1, args.epochs + 1):
        model.train()
        train_loss, train_correct, train_total = 0.0, 0, 0

        for imgs, labels, weights in train_loader:
            imgs, labels, weights = imgs.to(device), labels.to(device), weights.to(device)
            optimizer.zero_grad()
            outputs = model(imgs)
            loss_per = nn.functional.cross_entropy(outputs, labels, weight=class_weights, reduction='none')
            loss = (loss_per * weights).mean()
            loss.backward()
            torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0)
            optimizer.step()
            train_loss += loss.item() * imgs.size(0)
            _, pred = outputs.max(1)
            train_correct += pred.eq(labels).sum().item()
            train_total += imgs.size(0)

        scheduler.step()

        model.eval()
        val_correct, val_total = 0, 0
        val_correct_real, val_total_real = 0, 0
        with torch.no_grad():
            for imgs, labels, weights in val_loader:
                imgs, labels = imgs.to(device), labels.to(device)
                outputs = model(imgs)
                _, pred = outputs.max(1)
                val_correct += pred.eq(labels).sum().item()
                val_total += imgs.size(0)
                real_mask = weights > 1.5
                if real_mask.any():
                    val_correct_real += pred[real_mask].eq(labels[real_mask]).sum().item()
                    val_total_real += real_mask.sum().item()

        val_acc = 100.0 * val_correct / max(val_total, 1)
        val_acc_real = 100.0 * val_correct_real / max(val_total_real, 1)

        print(f"Epoch {epoch}/{args.epochs}  "
              f"Train: {100.0 * train_correct / max(train_total, 1):.1f}%  "
              f"Val: {val_acc:.1f}%  "
              f"Val(real): {val_acc_real:.1f}%", flush=True)

        check_acc = val_acc_real if val_total_real > 0 else val_acc
        if check_acc > best_val_acc:
            best_val_acc = check_acc
            best_state = {k: v.cpu().clone() for k, v in model.state_dict().items()}
            no_improve = 0
        else:
            no_improve += 1
            if no_improve >= patience:
                print(f"Early stopping at epoch {epoch}", flush=True)
                break

    print(f"\n{'=' * 60}", flush=True)
    print(f"Saving model...", flush=True)

    os.makedirs(args.output_dir, exist_ok=True)

    tpl_data = {}
    tpl_is_real = {}
    for ch, data in all_templates.items():
        tpl_data[ch] = [t.tolist() for t in data['templates']]
        tpl_is_real[ch] = data['is_real']

    tpl_path = os.path.join(args.output_dir, 'template_library.json')
    with open(tpl_path, 'w') as f:
        json.dump({'templates': tpl_data, 'is_real': tpl_is_real, 'template_size': TEMPLATE_SIZE}, f)
    print(f"Templates saved: {tpl_path}", flush=True)

    npz_arrays = {}
    for ch, data in all_templates.items():
        for i, t in enumerate(data['templates']):
            key = f"{ch}_{i}"
            npz_arrays[key] = t
    npz_path = os.path.join(args.output_dir, 'templates.npz')
    np.savez_compressed(npz_path, **npz_arrays)
    print(f"Templates (npz): {npz_path}", flush=True)

    cnn_ckpt = {
        'model_state': best_state if best_state else model.state_dict(),
        'chars': FULL_CHARS,
        'n_classes': N_CLASSES,
        'annotated_chars': annotated_chars,
        'annotation_count': sum(len(v) for v in crops_by_label.values()),
        'best_val_acc': best_val_acc,
    }
    cnn_path = os.path.join(args.output_dir, 'char_classifier.pth')
    torch.save(cnn_ckpt, cnn_path)
    print(f"CNN saved: {cnn_path}", flush=True)

    config = {
        'model_file': 'char_classifier.pth',
        'template_file': 'templates.npz',
        'template_library_file': 'template_library.json',
        'chars': FULL_CHARS,
        'n_classes': N_CLASSES,
        'annotated_chars': annotated_chars,
        'annotation_count': sum(len(v) for v in crops_by_label.values()),
        'template_count': total_tpl,
        'template_size': TEMPLATE_SIZE,
        'img_size': IMG_SIZE,
        'best_val_acc': best_val_acc,
        'method': 'template_matching+cnn',
    }
    config_path = os.path.join(args.output_dir, 'model_config.json')
    with open(config_path, 'w') as f:
        json.dump(config, f, indent=2)
    print(f"Config saved: {config_path}", flush=True)

    debug_dir = os.path.join(args.output_dir, 'debug_templates')
    os.makedirs(debug_dir, exist_ok=True)
    for ch, data in all_templates.items():
        safe_name = ch if ch.isalnum() else f"sym_{ord(ch)}"
        for i, t in enumerate(data['templates'][:3]):
            cv2.imwrite(os.path.join(debug_dir, f"{safe_name}_{i}.png"), t)

    print(f"\n{'=' * 60}", flush=True)
    print(f"Training complete!", flush=True)
    print(f"  Templates: {total_tpl} ({n_real_tpl} real chars)", flush=True)
    print(f"  CNN val accuracy: {best_val_acc:.1f}%", flush=True)
    print(f"  Annotated chars: {annotated_chars}", flush=True)
    print(f"{'=' * 60}", flush=True)
    print(f"TRAINING_COMPLETE:{best_val_acc:.1f}", flush=True)


if __name__ == '__main__':
    main()