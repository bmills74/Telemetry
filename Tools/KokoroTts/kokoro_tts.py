"""Bundled Kokoro worker. JSON stdin/stdout; no network access at runtime."""
import base64
import contextlib
import io
import json
from pathlib import Path
import sys


def emit(payload):
    print(json.dumps(payload, separators=(",", ":")), flush=True)


def main():
    try:
        with contextlib.redirect_stdout(sys.stderr):
            import soundfile as sf
            from kokoro_onnx import Kokoro
            home = Path(sys.executable).parent if getattr(sys, "frozen", False) else Path(__file__).parent / "runtime" / "KokoroWorker"
            kokoro = Kokoro(str(home / "kokoro-v1.0.onnx"), str(home / "voices-v1.0.bin"))
        emit({"ready": True})
    except Exception as error:
        emit({"error": str(error)})
        return
    for line in sys.stdin:
        if not line.strip():
            continue
        try:
            request = json.loads(line)
            text = str(request.get("text", "")).strip()
            if not text:
                raise ValueError("Text was empty.")
            voice = request.get("voice", "af_heart")
            language = "en-gb" if voice.startswith(("bf_", "bm_")) else "en-us"
            with contextlib.redirect_stdout(sys.stderr):
                audio, rate = kokoro.create(text, voice=voice,
                    speed=max(0.5, min(2.0, float(request.get("speed", 1)))), lang=language)
                output = io.BytesIO()
                sf.write(output, audio, rate, format="WAV", subtype="PCM_16")
            emit({"audioBase64": base64.b64encode(output.getvalue()).decode("ascii")})
        except Exception as error:
            emit({"error": str(error)})


if __name__ == "__main__":
    main()
