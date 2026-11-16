"""Small persistent Kokoro TTS worker for Sim Coach.

Reads one JSON request per line from stdin and writes one JSON response per
line to stdout. Audio is returned as base64-encoded WAV data so the WPF host
does not need to manage temporary files.
"""

from __future__ import annotations

import base64
import contextlib
import io
import json
import sys
from typing import Any

import soundfile as sf
from kokoro import KPipeline


DEFAULT_VOICE = "af_heart"


def emit(payload: dict[str, Any]) -> None:
    sys.stdout.write(json.dumps(payload, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def render(pipeline: KPipeline, text: str, voice: str, speed: float) -> str:
    audio_chunks = []
    for _, _, audio in pipeline(text, voice=voice, speed=speed, split_pattern=r"\n+"):
        audio_chunks.append(audio)

    if not audio_chunks:
        raise RuntimeError("Kokoro returned no audio.")

    audio_buffer = io.BytesIO()
    sf.write(audio_buffer, audio_chunks[0] if len(audio_chunks) == 1 else _join(audio_chunks), 24000, format="WAV", subtype="PCM_16")
    return base64.b64encode(audio_buffer.getvalue()).decode("ascii")


def _join(chunks: list[Any]) -> Any:
    import numpy as np

    return np.concatenate(chunks)


def main() -> None:
    # Some model/download libraries write progress messages to stdout. Keep
    # stdout reserved for the line-delimited JSON protocol used by WPF.
    pipelines: dict[str, KPipeline] = {}
    emit({"ready": True})

    for line in sys.stdin:
        if not line.strip():
            continue

        try:
            request = json.loads(line)
            text = str(request.get("text", "")).strip()
            voice = str(request.get("voice", DEFAULT_VOICE)).strip() or DEFAULT_VOICE
            speed = float(request.get("speed", 1.0))
            if not text:
                emit({"error": "Text was empty."})
                continue

            language = "b" if voice.startswith(("bf_", "bm_")) else "a"
            if language not in pipelines:
                with contextlib.redirect_stdout(sys.stderr):
                    pipelines[language] = KPipeline(lang_code=language, repo_id="hexgrad/Kokoro-82M")
            audio = render(pipelines[language], text, voice, max(0.5, min(2.0, speed)))
            emit({"audioBase64": audio})
        except Exception as exception:  # keep the worker alive for the next request
            emit({"error": str(exception)})


if __name__ == "__main__":
    main()
