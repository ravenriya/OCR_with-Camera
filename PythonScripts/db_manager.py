import sqlite3
import os

class DatabaseManager:
    def __init__(self, db_path="ocr_training.db"):
        self.db_path = db_path
        self._initialize_db()

    def _initialize_db(self):
        """Creates the database and table if they don't exist."""
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute('''
                CREATE TABLE IF NOT EXISTS corrections (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    image_filename TEXT NOT NULL,
                    system_guess TEXT NOT NULL,
                    operator_label TEXT NOT NULL,
                    timestamp DATETIME DEFAULT CURRENT_TIMESTAMP,
                    is_trained BOOLEAN DEFAULT 0
                )
            ''')
            conn.commit()

    def log_correction(self, filename, system_guess, operator_label):
        """Inserts a new operator correction into the database."""
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute('''
                INSERT INTO corrections (image_filename, system_guess, operator_label)
                VALUES (?, ?, ?)
            ''', (filename, system_guess, operator_label))
            conn.commit()

    def get_untrained_data(self):
        """Fetches all data that hasn't been added to the model yet."""
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            cursor.execute('SELECT id, image_filename, operator_label FROM corrections WHERE is_trained = 0')
            return cursor.fetchall()

    def mark_as_trained(self, record_ids):
        """Updates records after a successful retraining loop."""
        with sqlite3.connect(self.db_path) as conn:
            cursor = conn.cursor()
            cursor.executemany('UPDATE corrections SET is_trained = 1 WHERE id = ?', [(r_id,) for r_id in record_ids])
            conn.commit()