"""
STAGE 3: Inference with Your Custom Model
Uses your fine-tuned model for fast, accurate OCR
"""

import torch
import torch.nn as nn
import torchvision.transforms as transforms
import cv2
import numpy as np
import json
import sys
from pathlib import Path
import warnings

warnings.filterwarnings('ignore')


class CustomBackbone(nn.Module):
    """Same architecture"""
    def __init__(self, num_classes):
        super(CustomBackbone, self).__init__()
        
        self.features = nn.Sequential(
            nn.Conv2d(1, 64, kernel_size=3, padding=1),
            nn.BatchNorm2d(64),
            nn.ReLU(inplace=True),
            nn.Conv2d(64, 64, kernel_size=3, padding=1),
            nn.BatchNorm2d(64),
            nn.ReLU(inplace=True),
            nn.MaxPool2d(2, 2),
            
            nn.Conv2d(64, 128, kernel_size=3, padding=1),
            nn.BatchNorm2d(128),
            nn.ReLU(inplace=True),
            nn.Conv2d(128, 128, kernel_size=3, padding=1),
            nn.BatchNorm2d(128),
            nn.ReLU(inplace=True),
            nn.MaxPool2d(2, 2),
            
            nn.Conv2d(128, 256, kernel_size=3, padding=1),
            nn.BatchNorm2d(256),
            nn.ReLU(inplace=True),
            nn.Conv2d(256, 256, kernel_size=3, padding=1),
            nn.BatchNorm2d(256),
            nn.ReLU(inplace=True),
            nn.MaxPool2d(2, 2),
        )
        
        self.classifier = nn.Sequential(
            nn.Dropout(0.5),
            nn.Linear(256 * 3 * 3, 512),
            nn.ReLU(inplace=True),
            nn.Dropout(0.5),
            nn.Linear(512, num_classes)
        )
        
    def forward(self, x):
        x = self.features(x)
        x = x.view(x.size(0), -1)
        x = self.classifier(x)
        return x


class CustomOCR:
    """OCR engine using your custom model"""
    
    def __init__(self, model_path):
        self.device = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
        
        # Load checkpoint
        checkpoint = torch.load(model_path, map_location=self.device, weights_only=False)
        
        self.char_to_idx = checkpoint['char_to_idx']
        self.idx_to_char = checkpoint['idx_to_char']
        num_classes = checkpoint['num_classes']
        
        # Load model
        self.model = CustomBackbone(num_classes=num_classes).to(self.device)
        self.model.load_state_dict(checkpoint['model_state_dict'])
        self.model.eval()
        
        # Transform
        self.transform = transforms.Compose([
            transforms.ToPILImage(),
            transforms.Resize((28, 28)),
            transforms.ToTensor(),
            transforms.Normalize((0.1307,), (0.3081,))
        ])
        
        print(f"[INFO] Loaded custom model", file=sys.stderr)
        print(f"[INFO] Characters: {', '.join(sorted(self.char_to_idx.keys()))}", file=sys.stderr)
    
    def detect_characters(self, image):
        """Detect character boxes"""
        gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
        H, W = gray.shape
        
        # Preprocessing
        clahe = cv2.createCLAHE(clipLimit=3.0, tileGridSize=(8, 8))
        enhanced = clahe.apply(gray)
        enhanced = cv2.bilateralFilter(enhanced, 9, 75, 75)
        
        # Thresholding
        binary = cv2.adaptiveThreshold(
            enhanced, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C,
            cv2.THRESH_BINARY_INV, 25, 12
        )
        
        # Morphology
        kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (2, 2))
        morphed = cv2.morphologyEx(binary, cv2.MORPH_CLOSE, kernel, iterations=1)
        
        # Find contours
        contours, _ = cv2.findContours(morphed, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        
        # Filter contours
        boxes = []
        min_area = (H * W) * 0.0005
        max_area = (H * W) * 0.12
        
        for cnt in contours:
            x, y, w, h = cv2.boundingRect(cnt)
            area = w * h
            
            if area < min_area or area > max_area:
                continue
            
            aspect = h / max(w, 1)
            if aspect < 0.8 or aspect > 4.0:
                continue
            
            if w < 15 or h < 25:
                continue
            
            contour_area = cv2.contourArea(cnt)
            solidity = contour_area / area if area > 0 else 0
            if solidity < 0.25 or solidity > 0.90:
                continue
            
            boxes.append({"x": int(x), "y": int(y), "width": int(w), "height": int(h)})
        
        boxes.sort(key=lambda b: b['x'])
        return boxes
    
    def recognize_char(self, char_img):
        """Recognize single character"""
        # Convert to grayscale if needed
        if len(char_img.shape) == 3:
            char_img = cv2.cvtColor(char_img, cv2.COLOR_BGR2GRAY)
        
        # Transform
        img_tensor = self.transform(char_img).unsqueeze(0).to(self.device)
        
        # Predict
        with torch.no_grad():
            outputs = self.model(img_tensor)
            probs = torch.softmax(outputs, dim=1)
            confidence, predicted = torch.max(probs, 1)
            
            char_idx = predicted.item()
            conf_value = confidence.item() * 100
            
            char = self.idx_to_char[str(char_idx)]
            
            return char, conf_value
    
    def process_image(self, image_path):
        """Process full image"""
        image = cv2.imread(image_path)
        if image is None:
            return {"error": f"Cannot load: {image_path}", "text": "", "confidence": 0.0, "char_count": 0}
        
        # Detect
        boxes = self.detect_characters(image)
        
        if not boxes:
            return {"error": "No characters detected", "text": "", "confidence": 0.0, "char_count": 0}
        
        # Recognize
        gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
        text = ""
        total_conf = 0.0
        
        for box in boxes:
            x, y, w, h = box['x'], box['y'], box['width'], box['height']
            roi = gray[y:y+h, x:x+w]
            
            if roi.size > 0:
                char, conf = self.recognize_char(roi)
                box['label'] = char
                box['confidence'] = round(conf, 1)
                text += char
                total_conf += conf
        
        avg_conf = total_conf / len(boxes) if boxes else 0.0
        
        return {
            "text": text,
            "confidence": round(avg_conf, 1),
            "char_count": len(boxes),
            "boxes": boxes
        }
    
    def process_and_save(self, image_path, output_dir):
        """Process and save results"""
        result = self.process_image(image_path)
        
        output_dir = Path(output_dir)
        output_dir.mkdir(parents=True, exist_ok=True)
        
        # Save JSON
        json_path = output_dir / "result.json"
        with open(json_path, 'w') as f:
            result_copy = {k: float(v) if isinstance(v, (np.float32, np.float64)) else v 
                          for k, v in result.items()}
            json.dump(result_copy, f, indent=2)
        
        result['json_path'] = str(json_path)
        return result


def main():
    if len(sys.argv) < 3:
        print("Usage: python inference_custom.py <model.pth> <image.jpg> [output_dir]")
        sys.exit(1)
    
    model_path = sys.argv[1]
    image_path = sys.argv[2]
    output_dir = sys.argv[3] if len(sys.argv) > 3 else "output"
    
    # Initialize
    ocr = CustomOCR(model_path)
    
    # Process
    result = ocr.process_and_save(image_path, output_dir)
    
    # Print
    if 'error' in result and result['text'] == "":
        print(f"\n{'='*60}")
        print(f"ERROR: {result['error']}")
        print(f"{'='*60}\n")
        sys.exit(1)
    
    print(f"\n{'='*60}")
    print(f"CUSTOM MODEL OCR RESULT")
    print(f"{'='*60}")
    print(f"Text:       {result.get('text', 'N/A')}")
    print(f"Confidence: {result.get('confidence', 0):.1f}%")
    print(f"Characters: {result.get('char_count', 0)}")
    print(f"{'='*60}\n")


if __name__ == '__main__':
    main()