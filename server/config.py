import os
from dotenv import load_dotenv

load_dotenv()

# Server
HOST  = os.getenv("HOST", "0.0.0.0")
PORT  = int(os.getenv("PORT", 5000))

# Paths
OUTPUT_DIR   = os.getenv("OUTPUT_DIR",   "output")   # Excel files land here
STICKER_DIR  = os.getenv("STICKER_DIR",  "stickers") # ZPL files land here

# Printer — name must match Windows printer queue exactly
PRINTER_NAME = os.getenv("PRINTER_NAME", "ZDesigner ZD421")
