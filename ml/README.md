# ML service

Dokumentsystem's optional machine learning service ([design §11](../docs/design.md#11-ml-service-optional-remote-capable)). For now it does speech to text (§11.1) with [faster-whisper](https://github.com/SYSTRAN/faster-whisper). It's stateless: the worker sends 16 kHz audio and stores what comes back.

Run it on the NAS with `docker compose --profile ml up -d`, or on a stronger machine with `docker run -p 3003:3003 -v ml-cache:/cache ghcr.io/elmaxe/dokumentsystem-ml:edge`. For an NVIDIA GPU, use the `edge-cuda` tag and add `--gpus all`. Then add the URL under Admin › Settings › Machine learning.

| Variable | Default | |
| --- | --- | --- |
| `DOKUMENT_ML_API_KEY` | none | When set, requests need `Authorization: Bearer <key>` |
| `ML_DEVICE` | `auto` | `cpu`, `cuda`, or `auto` to use a GPU when one is visible |
| `ML_MODEL_TTL_SECONDS` | `300` | Unload a model after this long unused |
| `ML_TRANSCRIBE_CONCURRENCY` | `1` | Transcriptions run at once |
| `ML_MAX_UPLOAD_MB` | `1024` | Largest audio accepted |

Models are downloaded into `/cache` on first use. `auto` picks `large-v3-turbo` on a GPU and `small` on a CPU. The `kb-whisper-*` models are KBLab's Swedish fine-tunes.

## Endpoints

- `GET /health`: `{"status", "version", "device"}`.
- `GET /models`: the transcription models, the default, and which are loaded.
- `POST /transcribe` (multipart): `audio`, and optionally `model` (`auto`), `language` (`sv` or `en`; detected when left out), `languages` (`sv,en`) and `start_ms` (resume from there). The answer is NDJSON: a `detected` line (language, duration, how much was speech, model), a `segment` line per stretch of speech (`start_ms`, `end_ms`, `text`, `words` as `[start_ms, end_ms, "word"]`), then `done`, or `error` if it failed after starting. With under 3 s of speech, no model is loaded and no segments come.

## Develop

```sh
cd ml
python -m venv .venv && . .venv/bin/activate
pip install -e ".[dev]"
pytest && ruff check . && ruff format --check .
uvicorn dokument_ml.main:app --port 3003
```
