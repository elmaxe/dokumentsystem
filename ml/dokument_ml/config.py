"""Settings, read from the environment once at start-up."""

import os
from dataclasses import dataclass


@dataclass(frozen=True)
class Settings:
    version: str
    # When set, every request must send it as "Authorization: Bearer <key>" (the admin UI stores it per URL).
    api_key: str | None
    # "auto" uses a GPU when CTranslate2 sees one.
    device: str
    cache_dir: str
    # Models are unloaded after this many seconds without use.
    model_ttl_seconds: int
    # Transcriptions that may run at once; one keeps a NAS responsive and a GPU's memory in check.
    transcribe_concurrency: int
    max_upload_bytes: int

    @staticmethod
    def from_env() -> "Settings":
        return Settings(
            version=os.environ.get("DOKUMENT_VERSION", "0.0.0-dev"),
            api_key=os.environ.get("DOKUMENT_ML_API_KEY") or None,
            device=os.environ.get("ML_DEVICE", "auto"),
            cache_dir=os.environ.get("ML_CACHE_DIR", "/cache"),
            model_ttl_seconds=int(os.environ.get("ML_MODEL_TTL_SECONDS", "300")),
            transcribe_concurrency=int(os.environ.get("ML_TRANSCRIBE_CONCURRENCY", "1")),
            max_upload_bytes=int(os.environ.get("ML_MAX_UPLOAD_MB", "1024")) * 1024 * 1024,
        )
