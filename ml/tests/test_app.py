import json
import wave
from collections.abc import Iterator

import pytest
from fastapi.testclient import TestClient

from dokument_ml.app import create_app
from dokument_ml.config import Settings
from dokument_ml.transcribe import Detected, Segment, WhisperEngine, Word, choose_language, resolve_model


def settings(**overrides) -> Settings:
    values = dict(
        version="1.2.3",
        api_key=None,
        device="cpu",
        cache_dir="/tmp",
        model_ttl_seconds=300,
        transcribe_concurrency=1,
        max_upload_bytes=1024 * 1024,
    )
    values.update(overrides)
    return Settings(**values)


class FakeEngine:
    gpu = False
    engine_version = "fake 1"

    def __init__(self, fail_after: int | None = None):
        self.calls: list[dict] = []
        self.fail_after = fail_after

    def loaded(self) -> list[str]:
        return ["small"]

    def transcribe(self, path, model, language, languages, start_ms) -> tuple[Detected, Iterator[Segment]]:
        self.calls.append(dict(model=model, language=language, languages=languages, start_ms=start_ms))

        def segments():
            yield Segment(start_ms, start_ms + 2500, "Hej och välkommen.", [Word(start_ms, start_ms + 400, "Hej")])
            if self.fail_after == 1:
                raise RuntimeError("out of memory")
            yield Segment(start_ms + 2600, start_ms + 4000, "Tack.", [])

        return Detected(language or "sv", 0.93, 60_000, 50_000), segments()


def post(client: TestClient, **form):
    return client.post("/transcribe", files={"audio": ("a.ogg", b"OggS fake", "audio/ogg")}, data=form)


def lines(response) -> list[dict]:
    return [json.loads(x) for x in response.text.splitlines()]


def test_health_reports_version_and_device():
    with TestClient(create_app(settings(), FakeEngine())) as client:
        assert client.get("/health").json() == {"status": "ok", "version": "1.2.3", "device": "cpu"}


def test_models_lists_the_default_and_what_is_loaded():
    with TestClient(create_app(settings(), FakeEngine())) as client:
        body = client.get("/models").json()["transcribe"]
        assert body["default"] == "small"
        assert {"name": "small", "loaded": True} in body["models"]
        assert {"name": "kb-whisper-small", "loaded": False} in body["models"]


def test_api_key_is_required_when_set():
    with TestClient(create_app(settings(api_key="s3cret"), FakeEngine())) as client:
        assert client.get("/health").status_code == 401
        assert client.get("/health", headers={"Authorization": "Bearer nope"}).status_code == 401
        assert client.get("/health", headers={"Authorization": "Bearer s3cret"}).status_code == 200


def test_transcribe_streams_detected_segments_and_done():
    engine = FakeEngine()
    with TestClient(create_app(settings(), engine)) as client:
        response = post(client)
        assert response.headers["content-type"] == "application/x-ndjson"
        assert lines(response) == [
            {
                "type": "detected",
                "language": "sv",
                "language_probability": 0.93,
                "duration_ms": 60000,
                "speech_ms": 50000,
                "model": "small",
                "engine": "fake 1",
            },
            {
                "type": "segment",
                "start_ms": 0,
                "end_ms": 2500,
                "text": "Hej och välkommen.",
                "words": [[0, 400, "Hej"]],
            },
            {"type": "segment", "start_ms": 2600, "end_ms": 4000, "text": "Tack.", "words": []},
            {"type": "done", "segments": 2},
        ]
        assert engine.calls == [dict(model="small", language=None, languages=["sv", "en"], start_ms=0)]


def test_transcribe_resumes_from_a_time_in_a_known_language():
    engine = FakeEngine()
    with TestClient(create_app(settings(), engine)) as client:
        body = lines(post(client, start_ms="90000", language="en", model="kb-whisper-small"))
        assert body[1]["start_ms"] == 90000
        assert engine.calls == [dict(model="kb-whisper-small", language="en", languages=["sv", "en"], start_ms=90000)]


def test_a_failure_mid_stream_ends_with_an_error_line():
    with TestClient(create_app(settings(), FakeEngine(fail_after=1))) as client:
        body = lines(post(client))
        assert [x["type"] for x in body] == ["detected", "segment", "error"]
        assert body[-1]["message"] == "out of memory"


@pytest.mark.parametrize(
    "form",
    [{"model": "openai/whisper-evil"}, {"languages": "de"}, {"language": "de"}, {"start_ms": "-1"}],
)
def test_bad_requests_are_refused(form):
    with TestClient(create_app(settings(), FakeEngine())) as client:
        assert post(client, **form).status_code == 400


def test_too_large_audio_is_refused():
    with TestClient(create_app(settings(max_upload_bytes=4), FakeEngine())) as client:
        assert post(client).status_code == 413


def test_auto_model_depends_on_the_gpu():
    assert resolve_model("auto", gpu=True) == "large-v3-turbo"
    assert resolve_model("auto", gpu=False) == "small"
    with pytest.raises(KeyError):
        resolve_model("huge", gpu=False)


def test_language_is_limited_to_the_allowed_ones():
    # Whisper often hears Norwegian in Swedish speech.
    assert choose_language([("no", 0.5), ("sv", 0.3), ("en", 0.1)], ["sv", "en"]) == ("sv", pytest.approx(0.75))
    assert choose_language([("de", 0.9)], ["sv", "en"]) == ("sv", 0.0)


def test_silence_is_no_speech_without_loading_a_model(tmp_path):
    path = tmp_path / "silence.wav"
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(16_000)
        w.writeframes(b"\0\0" * 16_000 * 10)

    engine = WhisperEngine("cpu", str(tmp_path), 300)
    detected, segments = engine.transcribe(str(path), "small", None, ["sv", "en"], 0)
    assert detected.duration_ms == 10_000
    assert detected.speech_ms == 0
    assert list(segments) == []
    assert engine.loaded() == []
