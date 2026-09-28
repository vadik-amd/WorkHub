"""
WorkHub sidecar: transcribes one 16-bit PCM WAV with OpenVINO GenAI WhisperPipeline.

Embedded into WorkHub.exe and written next to the portable Python by the hub; run as
    python ov_whisper.py --model <IR dir> --wav <file> --lang ru --device AUTO --out <txt>

Device selection: AUTO tries NPU -> GPU -> CPU (only those OpenVINO reports), falling
through on any init/inference error; an explicit NPU/GPU/CPU runs on that device only.
A device that failed on this model is remembered in <cache-dir>/device_health.json (per
model + OpenVINO version + NPU driver) and skipped by AUTO afterwards — an NPU compile of
a large model takes minutes, no point repeating it for every file. Picking the device
explicitly retries it (and clears the mark on success).

Progress goes to stdout as "[ov] ..." lines; the last line is
    WORKHUB_RESULT {"device": ..., "load_s": ..., "infer_s": ..., "audio_s": ...}
Exit codes: 0 ok, 2 bad input, 3 the model can't run on the device(s), 4 a transient
device failure (hang / reset / out of memory) — the hub retries or hands the job over.
"""
import argparse
import json
import os
import sys
import time
import wave

AUTO_ORDER = ["NPU", "GPU", "CPU"]
SAMPLE_RATE = 16000

EXIT_BAD_INPUT = 2
EXIT_INCOMPATIBLE = 3  # the model can't run on the device(s) — retrying won't help
EXIT_TRANSIENT = 4     # the device glitched (hung / reset / out of memory) — worth a retry

# Level Zero / plugin errors that mean "the device had a bad moment", not "can't run this".
TRANSIENT_MARKERS = ("DEVICE_LOST", "device hung", "OUT_OF_DEVICE_MEMORY", "OUT_OF_HOST_MEMORY",
                     "out of memory", "bad_alloc")


def is_transient(message):
    return any(m.lower() in message.lower() for m in TRANSIENT_MARKERS)


def log(msg):
    print(f"[ov] {msg}", flush=True)


def load_wav(path):
    """Returns float32 mono samples at 16 kHz in [-1, 1] (stereo is averaged)."""
    import numpy as np

    with wave.open(path, "rb") as w:
        channels, width, rate, frames = w.getnchannels(), w.getsampwidth(), w.getframerate(), w.getnframes()
        raw = w.readframes(frames)
    if width != 2:
        raise ValueError(f"нужен 16-bit PCM WAV, а в файле {width * 8}-bit")

    audio = np.frombuffer(raw, dtype="<i2").astype(np.float32) / 32768.0
    if channels > 1:
        audio = audio.reshape(-1, channels).mean(axis=1)
    if rate != SAMPLE_RATE:  # our recordings are 16 kHz already; this is just a safety net
        n = int(round(len(audio) * SAMPLE_RATE / rate))
        audio = np.interp(np.linspace(0, len(audio) - 1, n), np.arange(len(audio)), audio).astype(np.float32)
    return audio


class DeviceHealth:
    """Persistent "this device can't run this model" marks, keyed so upgrades retry."""

    def __init__(self, cache_dir, model, core, ov_version):
        self.path = os.path.join(cache_dir, "device_health.json") if cache_dir else None
        self.model = os.path.normcase(os.path.abspath(model))
        self.core = core
        self.ov_version = ov_version
        self.data = {}
        if self.path and os.path.exists(self.path):
            try:
                with open(self.path, encoding="utf-8") as f:
                    self.data = json.load(f)
            except Exception:
                self.data = {}

    def _key(self, dev):
        driver = ""
        if dev == "NPU":
            try:
                driver = str(self.core.get_property("NPU", "NPU_DRIVER_VERSION"))
            except Exception:
                pass
        return f"{self.model}|{self.ov_version}|{dev}|{driver}"

    def failure(self, dev):
        return self.data.get(self._key(dev))

    def mark(self, dev, error):
        key = self._key(dev)
        if error is None:
            if self.data.pop(key, None) is None:
                return
        else:
            self.data[key] = {"error": error, "at": time.strftime("%Y-%m-%d %H:%M:%S")}
        if self.path:
            try:
                os.makedirs(os.path.dirname(self.path), exist_ok=True)
                with open(self.path, "w", encoding="utf-8") as f:
                    json.dump(self.data, f, ensure_ascii=False, indent=1)
            except Exception:
                pass


def device_chain(requested, available, health):
    """Devices to try, in order. `available` is openvino.Core().available_devices."""
    def present(dev):
        return any(a == dev or a.startswith(dev + ".") for a in available)

    if requested != "AUTO":
        return [requested] if present(requested) else []

    chain = []
    for dev in AUTO_ORDER:
        if not present(dev):
            continue
        bad = health.failure(dev)
        if bad:
            log(f"{dev} пропущен: ранее не справился с этой моделью ({bad['at']}: {bad['error']}). "
                f"Выберите устройство {dev} явно, чтобы попробовать снова.")
            continue
        chain.append(dev)
    return chain or (["CPU"] if present("CPU") else [])


def remove_cache(path):
    """Deletes a device cache folder. OpenVINO writes its .blob files read-only, which a
    plain rmtree can't delete on Windows — clear the flag and retry."""
    import gc
    import shutil
    import stat

    def force(func, p, _exc):
        try:
            os.chmod(p, stat.S_IWRITE)
            func(p)
        except OSError:
            pass

    gc.collect()  # release the failed compiled model so its blobs aren't held open
    shutil.rmtree(path, onexc=force)


def first_line(e):
    """The most telling line of an OpenVINO exception (they are long multi-line traces)."""
    lines = [l.strip() for l in str(e).splitlines() if l.strip()]
    if not lines:
        return type(e).__name__
    for l in reversed(lines):  # the driver/runtime reason sits at the bottom
        if not l.startswith("Exception from"):
            return l[:300]
    return lines[-1][:300]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", required=True, help="folder with the OpenVINO IR Whisper model")
    ap.add_argument("--wav", required=True)
    ap.add_argument("--lang", required=True, help="language code, e.g. ru / en")
    ap.add_argument("--out", required=True, help="output .txt path")
    ap.add_argument("--device", default="AUTO", choices=["AUTO", "NPU", "GPU", "CPU"])
    ap.add_argument("--cache-dir", default="", help="OpenVINO compiled-model cache")
    args = ap.parse_args()

    import openvino
    import openvino_genai

    try:
        audio = load_wav(args.wav)
    except Exception as e:
        log(f"не удалось прочитать WAV: {e}")
        return 2
    audio_s = len(audio) / SAMPLE_RATE

    core = openvino.Core()
    available = core.available_devices
    health = DeviceHealth(args.cache_dir, args.model, core, openvino.__version__)
    chain = device_chain(args.device, available, health)
    log(f"OpenVINO {openvino.__version__.split('-')[0]}; устройства: {', '.join(available)}; "
        f"порядок: {' -> '.join(chain) or '-'}")
    if not chain:
        log(f"устройство {args.device} недоступно")
        return 3

    result = used = None
    load_s = infer_s = 0.0
    transient = False
    for dev in chain:
        # One cache folder per device, so a device that can't run the model doesn't leave
        # gigabytes of useless compiled blobs behind (they're deleted on failure below).
        dev_cache = os.path.join(args.cache_dir, dev.lower()) if args.cache_dir else ""
        props = {"CACHE_DIR": dev_cache} if dev_cache else {}
        try:
            log(f"{dev}: загрузка модели (первый раз на NPU/GPU — компиляция, может занять минуты)…")
            t0 = time.perf_counter()
            pipe = openvino_genai.WhisperPipeline(args.model, dev, **props)
            load_s = time.perf_counter() - t0
            log(f"{dev}: модель загружена за {load_s:.1f} с, распознавание {audio_s:.0f} с аудио…")

            t0 = time.perf_counter()
            result = pipe.generate(audio, language=f"<|{args.lang}|>", task="transcribe",
                                   return_timestamps=True)
            infer_s = time.perf_counter() - t0
            used = dev
            health.mark(dev, None)
            break
        except Exception as e:  # NPU is picky with Whisper; fall through to the next device
            reason = first_line(e)
            result = pipe = None
            if is_transient(str(e)):
                # A hang / reset / memory squeeze (e.g. while the GPU and CPU are busy too):
                # the model itself is fine — keep its compiled cache, don't mark the device.
                transient = True
                log(f"{dev}: временный сбой ({reason})")
            else:
                log(f"{dev}: ошибка ({reason}) — модель на этом устройстве не работает")
                health.mark(dev, reason)
                if dev_cache:
                    remove_cache(dev_cache)

    if used is None:
        log("ни одно устройство не справилось")
        return EXIT_TRANSIENT if transient else EXIT_INCOMPATIBLE

    if result.chunks:
        lines = [c.text.strip() for c in result.chunks if c.text.strip()]
    else:
        lines = [t.strip() for t in result.texts if t.strip()]

    tmp = args.out + ".part"
    with open(tmp, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + ("\n" if lines else ""))
    os.replace(tmp, args.out)

    rtf = infer_s / audio_s if audio_s else 0.0
    log(f"готово на {used}: загрузка {load_s:.1f} с, распознавание {infer_s:.1f} с "
        f"на {audio_s:.0f} с аудио (RTF {rtf:.2f})")
    print("WORKHUB_RESULT " + json.dumps({"device": used, "load_s": round(load_s, 2),
                                          "infer_s": round(infer_s, 2), "audio_s": round(audio_s, 2)}),
          flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
