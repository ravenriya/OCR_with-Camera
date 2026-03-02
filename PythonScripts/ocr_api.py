import cv2
import numpy as np
import uuid
from ocr_engine import IndustrialOCRSystem

class OCRWrapper:
    def __init__(self):
        self.engine = IndustrialOCRSystem()
        self.active_transactions = {} # Temporarily holds data until operator verifies

    def process_image_bytes(self, image_byte_array):
        """Called by C#. Takes a byte array, decodes it, and runs OCR."""
        try:
            # 1. Convert C# byte array to OpenCV Image
            nparr = np.frombuffer(memoryview(image_byte_array), np.uint8)
            img = cv2.imdecode(nparr, cv2.IMREAD_COLOR)

            if img is None:
                return "ERROR: Invalid Image", ""

            # 2. Run the OCR Engine
            predicted_text, char_data = self.engine.read_part(img)

            # 3. Store the data in memory with a unique ID
            transaction_id = uuid.uuid4().hex
            self.active_transactions[transaction_id] = {
                "prediction": predicted_text,
                "data": char_data
            }

            return predicted_text, transaction_id

        except Exception as e:
            return f"ERROR: {str(e)}", ""

    def submit_correction(self, transaction_id, operator_text):
        """Called by C# if the operator fixes the string."""
        if transaction_id in self.active_transactions:
            session = self.active_transactions.pop(transaction_id) # Get and remove
            
            system_prediction = session["prediction"]
            char_data = session["data"]
            
            # Trigger the active learning loop in the engine
            self.engine.process_operator_correction(system_prediction, operator_text, char_data)
            return True
        return False

    def clear_transaction(self, transaction_id):
        """Called by C# if the operator accepts the string (cleanup)."""
        if transaction_id in self.active_transactions:
            self.active_transactions.pop(transaction_id)