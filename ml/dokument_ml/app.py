"""The HTTP contract the worker talks to (docs/design.md §11). Stateless: nothing is kept but the model cache."""

import asyncio
import contextlib
import json
import logging
import os
import secrets
import tempfile
import threading
from collections.abc import Iterator

from fastapi import Depends, FastAPI, File, Form, HTTPException, Request, UploadFile
from fastapi.responses import StreamingResponse

from .config import Settings
from .transcribe import AUTO, MODELS, Engine, resolve_model

log = logging.getLogger("dokument_ml")

LANGUAGES = ("sv", "en")


def create_app(settings: Settings, engine: Engine | None = None) -> FastAPI:
    @contextlib.asynccontextmanager
    async def lifespan(app: FastAPI):
        if app.state.engine is None:
            from .transcribe import WhisperEngine

            app.state.engine = WhisperEngine(settings.device, settings.cache_dir, settings.model_ttl_seconds)
        unloader = asyncio.create_task(unload_idle(app.state.engine))
        yield
        unloader.cancel()

    async def unload_idle(engine: Engine) -> None:
        if not hasattr(engine, "unload_idle"):
            return
        while True:
            await asyncio.sleep(30)
            engine.unload_idle()

    app = FastAPI(title="Dokumentsystem ML", version=settings.version, lifespan=lifespan)
    app.state.engine = engine
    slots = threading.BoundedSemaphore(settings.transcribe_concurrency)

    def authorize(request: Request) -> None:
        if settings.api_key is None:
            return
        header = request.headers.get("authorization", "")
        scheme, _, key = header.partition(" ")
        if scheme.lower() != "bearer" or not secrets.compare_digest(key.encode(), settings.api_key.encode()):
            raise HTTPException(401, "Missing or wrong API key")

    @app.get("/health", dependencies=[Depends(authorize)])
    def health(request: Request):
        engine: Engine = request.app.state.engine
        return {"status": "ok", "version": settings.version, "device": "cuda" if engine.gpu else "cpu"}

    @app.get("/models", dependencies=[Depends(authorize)])
    def models(request: Request):
        engine: Engine = request.app.state.engine
        loaded = set(engine.loaded())
        return {
            "transcribe": {
                "default": resolve_model(AUTO, engine.gpu),
                "languages": list(LANGUAGES),
                "models": [{"name": name, "loaded": name in loaded} for name in MODELS],
            }
        }

    @app.post("/transcribe", dependencies=[Depends(authorize)])
    async def transcribe(
        request: Request,
        audio: UploadFile = File(...),
        model: str = Form(AUTO),
        language: str | None = Form(None),
        languages: str = Form(",".join(LANGUAGES)),
        start_ms: int = Form(0),
    ):
        """Streams NDJSON: one "detected" line, a "segment" line per stretch of speech, then "done".

        A failure after the stream has started arrives as an "error" line, since the status is already sent.
        """
        engine: Engine = request.app.state.engine
        try:
            name = resolve_model(model, engine.gpu)
        except KeyError:
            raise HTTPException(400, f"Unknown model {model!r}") from None
        allowed = [code for code in languages.split(",") if code in LANGUAGES]
        if not allowed:
            raise HTTPException(400, f"Languages must be among {', '.join(LANGUAGES)}")
        if language is not None and language not in allowed:
            raise HTTPException(400, f"Unknown language {language!r}")
        if start_ms < 0:
            raise HTTPException(400, "start_ms must not be negative")

        path = await save_upload(audio, settings.max_upload_bytes)
        return StreamingResponse(
            stream(engine, path, name, language, allowed, start_ms), media_type="application/x-ndjson"
        )

    def stream(
        engine: Engine, path: str, model: str, language: str | None, allowed: list[str], start_ms: int
    ) -> Iterator[bytes]:
        # Starlette runs a plain iterator in its thread pool, so the blocking model calls don't stall the loop.
        slots.acquire()
        try:
            detected, segments = engine.transcribe(path, model, language, allowed, start_ms)
            yield line(
                type="detected",
                language=detected.language,
                language_probability=round(detected.language_probability, 3),
                duration_ms=detected.duration_ms,
                speech_ms=detected.speech_ms,
                model=model,
                engine=engine.engine_version,
            )
            count = 0
            for s in segments:
                count += 1
                yield line(
                    type="segment",
                    start_ms=s.start_ms,
                    end_ms=s.end_ms,
                    text=s.text,
                    words=[[w.start_ms, w.end_ms, w.text] for w in s.words],
                )
            yield line(type="done", segments=count)
        except Exception as e:
            log.exception("Transcription failed")
            yield line(type="error", message=str(e) or type(e).__name__)
        finally:
            slots.release()
            with contextlib.suppress(OSError):
                os.remove(path)

    return app


def line(**fields) -> bytes:
    return (json.dumps(fields, ensure_ascii=False) + "\n").encode()


async def save_upload(upload: UploadFile, limit: int) -> str:
    handle, path = tempfile.mkstemp(prefix="transcribe-")
    size = 0
    try:
        with os.fdopen(handle, "wb") as out:
            while chunk := await upload.read(1024 * 1024):
                size += len(chunk)
                if size > limit:
                    raise HTTPException(413, f"Audio over {limit // (1024 * 1024)} MB")
                out.write(chunk)
    except BaseException:
        os.remove(path)
        raise
    return path
