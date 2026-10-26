"""The ASGI entry point: `uvicorn dokument_ml.main:app`."""

import logging

from .app import create_app
from .config import Settings

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
app = create_app(Settings.from_env())
