import numpy as np
from ocr_engine import IndustrialOCRSystem

def main():
    print("Initializing Industrial OCR Engine...")
    system = IndustrialOCRSystem()
    
    # Simulating a camera trigger pulling an image
    dummy_image = np.zeros((100, 300, 3), dtype=np.uint8) 
    
    print("\n--- NEW PART TRIGGERED ---")
    predicted_text, char_data = system.read_part(dummy_image)
    print(f"Vision System Output: {predicted_text}") 
    
    # Simulating the operator looking at the screen and correcting it
    operator_input = input("Operator, verify or correct the string (Press Enter if correct): ")
    
    if operator_input and operator_input != predicted_text:
        print("Correction received. Updating database...")
        system.process_operator_correction(predicted_text, operator_input, char_data)
    else:
        print("Part verified. Passing to next station.")

if __name__ == "__main__":
    main()