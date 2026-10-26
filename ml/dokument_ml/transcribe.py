"""Speech to text with faster-whisper (docs/design.md §11.1).

The worker sends 16 kHz mono audio, and gets timed segments back as they're recognized, so a long recording shows
progress and an interrupted job can resume from the last time it stored.
"""

import threading
import time
from collections.abc import Iterator
from dataclasses import dataclass, field
from typing import Protocol

SAMPLE_RATE = 16_000

# Less speech than this after voice detection counts as none: music, silence, a pocket recording.
MIN_SPEECH_MS = 3_000

# The models the service will load. A fixed list, so a request can't make it download any repository.
MODELS: dict[str, str] = {
    "tiny": "tiny",
    "base": "base",
    "small": "small",
    "medium": "medium",
    "large-v3": "large-v3",
    "large-v3-turbo": "large-v3-turbo",
    # The Swedish National Library's fine-tunes, much better on Swedish speech.
    "kb-whisper-small": "KBLab/kb-whisper-small",
    "kb-whisper-medium": "KBLab/kb-whisper-medium",
    "kb-whisper-large": "KBLab/kb-whisper-large",
}

AUTO = "auto"


def resolve_model(name: str, gpu: bool) -> str:
    """Picks the model for "auto" (large-v3-turbo on a GPU, small on a CPU); raises KeyError for unknown names."""
    if name == AUTO:
        return "large-v3-turbo" if gpu else "small"
    if name not in MODELS:
        raise KeyError(name)
    return name


def choose_language(probabilities: list[tuple[str, float]], allowed: list[str]) -> tuple[str, float]:
    """The likeliest of the allowed languages, with its probability among them only.

    Whisper may hear Norwegian or Danish in Swedish speech; limiting the choice to the languages the server
    indexes (Swedish and English) avoids a transcript in the wrong language.
    """
    scores = {code: p for code, p in probabilities if code in allowed}
    if not scores:
        return allowed[0], 0.0
    best = max(scores, key=lambda code: scores[code])
    total = sum(scores.values())
    return best, scores[best] / total if total > 0 else 0.0


@dataclass(frozen=True)
class Word:
    start_ms: int
    end_ms: int
    text: str


@dataclass(frozen=True)
class Segment:
    start_ms: int
    end_ms: int
    text: str
    words: list[Word] = field(default_factory=list)


@dataclass(frozen=True)
class Detected:
    language: str
    language_probability: float
    duration_ms: int
    speech_ms: int


class Engine(Protocol):
    gpu: bool
    engine_version: str

    def loaded(self) -> list[str]: ...

    def transcribe(
        self, path: str, model: str, language: str | None, languages: list[str], start_ms: int
    ) -> tuple[Detected, Iterator[Segment]]: ...


class WhisperEngine:
    """faster-whisper models, loaded on first use and unloaded after an idle time."""

    def __init__(self, device: str, cache_dir: str, ttl_seconds: int):
        import ctranslate2
        import faster_whisper

        self.gpu = device == "cuda" or (device == "auto" and ctranslate2.get_cuda_device_count() > 0)
        self.engine_version = f"faster-whisper {faster_whisper.__version__}"
        self._device = "cuda" if self.gpu else "cpu"
        self._compute_type = "float16" if self.gpu else "int8"
        self._cache_dir = cache_dir
        self._ttl = ttl_seconds
        self._lock = threading.Lock()
        self._models: dict[str, tuple[object, float]] = {}

    def loaded(self) -> list[str]:
        with self._lock:
            return sorted(self._models)

    def unload_idle(self) -> None:
        now = time.monotonic()
        with self._lock:
            for name in [n for n, (_, used) in self._models.items() if now - used > self._ttl]:
                del self._models[name]

    def _model(self, name: str):
        from faster_whisper import WhisperModel

        with self._lock:
            if name in self._models:
                model = self._models[name][0]
            else:
                model = WhisperModel(
                    MODELS[name],
                    device=self._device,
                    compute_type=self._compute_type,
                    download_root=self._cache_dir,
                )
            self._models[name] = (model, time.monotonic())
            return model

    def transcribe(
        self, path: str, model: str, language: str | None, languages: list[str], start_ms: int
    ) -> tuple[Detected, Iterator[Segment]]:
        from faster_whisper import decode_audio
        from faster_whisper.vad import VadOptions, get_speech_timestamps

        audio = decode_audio(path, sampling_rate=SAMPLE_RATE)
        duration_ms = len(audio) * 1000 // SAMPLE_RATE
        audio = audio[start_ms * SAMPLE_RATE // 1000 :]
        speech_ms = sum(
            (t["end"] - t["start"]) * 1000 // SAMPLE_RATE for t in get_speech_timestamps(audio, VadOptions())
        )
        if speech_ms < MIN_SPEECH_MS:
            return Detected(language or languages[0], 0.0, duration_ms, speech_ms), iter(())

        whisper = self._model(model)
        probability = 1.0
        if language is None:
            _, _, probabilities = whisper.detect_language(audio, vad_filter=True)
            language, probability = choose_language(probabilities, languages)

        segments, _ = whisper.transcribe(
            audio,
            language=language,
            vad_filter=True,
            word_timestamps=True,
            condition_on_previous_text=False,
        )

        def ms(seconds: float) -> int:
            return start_ms + round(seconds * 1000)

        def timed() -> Iterator[Segment]:
            for s in segments:
                text = s.text.strip()
                if not text:
                    continue
                words = [Word(ms(w.start), ms(w.end), w.word.strip()) for w in s.words or [] if w.word.strip()]
                yield Segment(ms(s.start), ms(s.end), text, words)
                # Keep the model warm while a long file is read.
                with self._lock:
                    if model in self._models:
                        self._models[model] = (self._models[model][0], time.monotonic())

        return Detected(language, probability, duration_ms, speech_ms), timed()
