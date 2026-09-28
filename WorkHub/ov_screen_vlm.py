"""
WorkHub sidecar: describes screen key frames with a local vision-language model through
OpenVINO GenAI's VLMPipeline.

Embedded into WorkHub.exe and written next to the portable Python by the hub; run as
    python ov_screen_vlm.py --model <IR dir> --device AUTO --max-tokens 160

The model is loaded once and then serves frames over stdin/stdout, because loading (and on
NPU/GPU compiling) a VLM takes far longer than describing a frame:

    stdout  [vlm] <progress>                        — free-form progress lines
    stdout  WORKHUB_READY {"device": "GPU"}         — model is up, send frames
    stdin   {"raw": "<path>", "width": W, "height": H, "prompt": "..."}
    stdout  WORKHUB_FRAME {"text": "...", "seconds": 4.2}   (or {"error": "..."})
    stdin   {"cmd": "quit"}

Frames arrive as raw RGB24 (the hub converts them with ffmpeg): this venv has openvino and
numpy but no image library, and raw needs neither.

Device selection: AUTO tries NPU -> GPU -> CPU, falling through on any initialisation error,
and logs what actually ran. Exit codes: 0 ok, 2 bad input, 3 no device could load the model.
"""
import argparse
import json
import os
import sys
import time

AUTO_ORDER = ["NPU", "GPU", "CPU"]

EXIT_BAD_INPUT = 2
EXIT_NO_DEVICE = 3


def log(msg):
    print(f"[vlm] {msg}", flush=True)


def first_line(e):
    """The most telling line of an OpenVINO exception (they are long multi-line traces)."""
    lines = [l.strip() for l in str(e).splitlines() if l.strip()]
    if not lines:
        return type(e).__name__
    for l in reversed(lines):
        if not l.startswith("Exception from"):
            return l[:300]
    return lines[-1][:300]


def device_chain(requested, available):
    def present(dev):
        return any(a == dev or a.startswith(dev + ".") for a in available)

    if requested != "AUTO":
        return [requested] if present(requested) else []
    chain = [d for d in AUTO_ORDER if present(d)]
    return chain or (["CPU"] if present("CPU") else [])


def load_frame(path, width, height):
    """Raw RGB24 file -> ov.Tensor shaped (1, H, W, 3), which is what VLMPipeline expects."""
    import numpy as np
    import openvino as ov

    expected = width * height * 3
    data = np.fromfile(path, dtype=np.uint8)
    if data.size != expected:
        raise ValueError(f"кадр {os.path.basename(path)}: {data.size} байт вместо {expected}")
    return ov.Tensor(data.reshape(1, height, width, 3))


def load_pipeline(model, requested_device, cache_dir):
    """Returns (pipeline, device), trying the devices in order. None when none of them works."""
    import openvino
    import openvino_genai

    core = openvino.Core()
    available = core.available_devices
    chain = device_chain(requested_device, available)
    log(f"OpenVINO {openvino.__version__.split('-')[0]}; устройства: {', '.join(available)}; "
        f"порядок: {' -> '.join(chain) or '-'}")
    if not chain:
        log(f"устройство {requested_device} недоступно")
        return None, None

    for dev in chain:
        props = {}
        if cache_dir:
            props["CACHE_DIR"] = os.path.join(cache_dir, "vlm_" + dev.lower())
        try:
            log(f"{dev}: загрузка модели (первый раз на NPU/GPU — компиляция, может занять минуты)…")
            t0 = time.perf_counter()
            pipe = openvino_genai.VLMPipeline(model, dev, **props)
            log(f"{dev}: модель загружена за {time.perf_counter() - t0:.1f} с")
            return pipe, dev
        except Exception as e:
            log(f"{dev}: не справился ({first_line(e)}) — пробую следующее устройство")
    return None, None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", required=True, help="folder with the OpenVINO IR VLM model")
    ap.add_argument("--device", default="AUTO", choices=["AUTO", "NPU", "GPU", "CPU"])
    ap.add_argument("--max-tokens", type=int, default=160)
    ap.add_argument("--cache-dir", default="", help="OpenVINO compiled-model cache")
    args = ap.parse_args()

    if not os.path.isdir(args.model):
        log(f"папки с моделью нет: {args.model}")
        return EXIT_BAD_INPUT

    pipe, device = load_pipeline(args.model, args.device, args.cache_dir)
    if pipe is None:
        log("ни одно устройство не смогло загрузить модель")
        return EXIT_NO_DEVICE

    print("WORKHUB_READY " + json.dumps({"device": device}), flush=True)

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            request = json.loads(line)
        except Exception as e:
            print("WORKHUB_FRAME " + json.dumps({"error": f"плохой запрос: {e}"}), flush=True)
            continue
        if request.get("cmd") == "quit":
            break

        t0 = time.perf_counter()
        try:
            tensor = load_frame(request["raw"], int(request["width"]), int(request["height"]))
            result = pipe.generate(request.get("prompt") or "Describe this screenshot.",
                                   images=[tensor],
                                   max_new_tokens=args.max_tokens,
                                   do_sample=False)
            text = " ".join(str(result).split())
            answer = {"text": text, "seconds": round(time.perf_counter() - t0, 2)}
        except Exception as e:
            answer = {"error": first_line(e), "seconds": round(time.perf_counter() - t0, 2)}
        print("WORKHUB_FRAME " + json.dumps(answer, ensure_ascii=False), flush=True)

    return 0


if __name__ == "__main__":
    sys.exit(main())
