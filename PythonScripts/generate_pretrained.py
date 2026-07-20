# -*- coding: utf-8 -*-
"""
generate_pretrained.py  -  PixTech Synthetic Pretrainer
========================================================
Run ONCE to create pretrained_base.pth — a CNN that knows
A-Z, 0-9, and common special chars from synthetic data.

Usage:
    python generate_pretrained.py --output Models\\pretrained_base.pth
"""
import argparse, random, sys, io
import cv2, numpy as np
import torch, torch.nn as nn, torch.optim as optim
from torch.utils.data import Dataset, DataLoader

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8', errors='replace')
random.seed(42); np.random.seed(42); torch.manual_seed(42)

IMG_SIZE = 48
FULL_CHARS = list("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ/-.,()#:&")
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
    draw.text(((size-tw)//2+random.randint(-3,3), (size-th)//2+random.randint(-3,3)), char, fill=fg, font=font)
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


def render_char(char):
    return _pil_render(char) if (HAS_PIL and random.random() < 0.7) else _cv2_render(char)


def augment(img):
    h, w = img.shape
    M = cv2.getRotationMatrix2D((w/2, h/2), random.uniform(-20, 20), random.uniform(0.85, 1.15))
    img = cv2.warpAffine(img, M, (w, h), borderMode=cv2.BORDER_REPLICATE)
    if random.random() < 0.5:
        mg = max(1, int(min(h, w) * 0.12))
        src = np.float32([[0,0],[w,0],[w,h],[0,h]])
        dst = np.float32([[random.randint(0,mg), random.randint(0,mg)],
                          [w-random.randint(0,mg), random.randint(0,mg)],
                          [w-random.randint(0,mg), h-random.randint(0,mg)],
                          [random.randint(0,mg), h-random.randint(0,mg)]])
        img = cv2.warpPerspective(img, cv2.getPerspectiveTransform(src, dst), (w, h), borderMode=cv2.BORDER_REPLICATE)
    img = np.clip(img.astype(np.float32) * random.uniform(0.5, 1.6) + random.randint(-40, 40), 0, 255).astype(np.uint8)
    if random.random() < 0.4: img = cv2.GaussianBlur(img, (random.choice([3,5,7]),)*2, 0)
    if random.random() < 0.4: img = np.clip(img.astype(np.float32) + np.random.normal(0, random.uniform(5,20), img.shape), 0, 255).astype(np.uint8)
    if random.random() < 0.3:
        for _ in range(random.randint(2, 8)):
            yl = random.randint(0, h-1); t = random.randint(1, 3)
            img[max(0,yl-t):min(h,yl+t), :] = np.clip(img[max(0,yl-t):min(h,yl+t), :].astype(np.int32) + random.randint(-20,20), 0, 255).astype(np.uint8)
    if random.random() < 0.25:
        cx, cy = random.randint(0, w), random.randint(0, h)
        Y, X = np.ogrid[:h, :w]; r = random.randint(8, 20)
        mask = ((X-cx)**2 + (Y-cy)**2) < r**2
        img[mask] = np.clip(img[mask].astype(np.int32) + random.randint(30, 70), 0, 255).astype(np.uint8)
    if random.random() < 0.3:
        k = np.ones((2,2), np.uint8)
        img = cv2.erode(img, k, 1) if random.random() < 0.5 else cv2.dilate(img, k, 1)
    return img


def to_tensor(img):
    return torch.tensor(cv2.resize(img, (IMG_SIZE, IMG_SIZE)).astype(np.float32) / 127.5 - 1.0).unsqueeze(0)

class DS(Dataset):
    def __init__(self, s): self.s = s
    def __len__(self): return len(self.s)
    def __getitem__(self, i): return to_tensor(self.s[i][0]), self.s[i][1]


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--output', default='pretrained_base.pth')
    p.add_argument('--epochs', type=int, default=25)
    p.add_argument('--samples', type=int, default=600)
    p.add_argument('--batch', type=int, default=64)
    p.add_argument('--lr', type=float, default=0.001)
    args = p.parse_args()

    device = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
    print(f"[INFO] Device: {device} | {N_CLASSES} chars x {args.samples} = {N_CLASSES*args.samples} images", flush=True)
    print(f"[INFO] Charset: {FULL_CHARS}", flush=True)

    data = []
    for ci, ch in enumerate(FULL_CHARS):
        for _ in range(args.samples):
            data.append((augment(render_char(ch)), ci))
        if (ci+1) % 10 == 0: print(f"  Generated {ci+1}/{N_CLASSES}...", flush=True)
    random.shuffle(data)

    split = int(len(data) * 0.9)
    tr = DataLoader(DS(data[:split]), args.batch, shuffle=True, drop_last=True, num_workers=0)
    va = DataLoader(DS(data[split:]), args.batch, shuffle=False, num_workers=0)

    model = TinyCNN(N_CLASSES).to(device)
    crit = nn.CrossEntropyLoss(label_smoothing=0.05)
    opt = optim.AdamW(model.parameters(), lr=args.lr, weight_decay=1e-4)
    sch = optim.lr_scheduler.CosineAnnealingLR(opt, T_max=args.epochs)
    best_val, best_sd = 0.0, None

    for ep in range(1, args.epochs+1):
        model.train(); tc = tn = 0
        for imgs, lbls in tr:
            imgs, lbls = imgs.to(device), lbls.to(device)
            opt.zero_grad(); loss = crit(model(imgs), lbls); loss.backward(); opt.step()
            with torch.no_grad(): tc += (model(imgs).argmax(1)==lbls).sum().item(); tn += len(lbls)
        model.eval(); vc = vn = 0
        with torch.no_grad():
            for imgs, lbls in va:
                imgs, lbls = imgs.to(device), lbls.to(device)
                vc += (model(imgs).argmax(1)==lbls).sum().item(); vn += len(lbls)
        sch.step()
        print(f"  Epoch {ep:3d}/{args.epochs} | Train: {tc/max(tn,1)*100:.1f}% | Val: {vc/max(vn,1)*100:.1f}%", flush=True)
        if vc/max(vn,1) >= best_val:
            best_val = vc/max(vn,1); best_sd = {k: v.clone() for k, v in model.state_dict().items()}

    if best_sd: model.load_state_dict(best_sd)
    torch.save({'model_state': model.state_dict(), 'chars': FULL_CHARS, 'n_classes': N_CLASSES, 'img_size': IMG_SIZE}, args.output)
    print(f"\n[DONE] Saved {args.output} — Val: {best_val*100:.1f}%", flush=True)
    print(f"PRETRAIN_COMPLETE:{best_val*100:.1f}", flush=True)

if __name__ == '__main__':
    main()