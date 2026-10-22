"""Paints one picture for Tonwerk's covers (Images/MfluxEngine.cs), with mflux or flux3-mlx.

Run with the runner's own Python environment (deploy/install-images.sh), not YuE Studio's or the speech lab's: mflux
pins mlx, transformers and torch versions the others do not share, and flux3-mlx brings mlx-vlm for its upsampler.

In: one JSON object on stdin
    {"runner": "mflux" | "flux3-mlx", "model": "flux2-klein-4b", "quantize": 8 | null, "steps": 4,
     "upsampler": "4bit" | null, "contentFilter": false, "prompt": "...", "seed": 42,
     "width": 1024, "height": 1024, "output": "/abs/picture.jpg",
     "loras": [{"path": "/abs/style.safetensors", "strength": 1.0}]}
Out: the picture as JPEG at "output", and lines starting with "YUEUI " on stdout, each a JSON event:
    {"event": "stage", "stage": "loading" | "painting", "fraction": 0.5}
    {"event": "done", "loadSeconds": 12.3, "paintSeconds": 30.1, "peakMemoryGb": 8.9, "paintedPrompt": "..." | null}
Anything else on stdout or stderr is the runner's own output, which the server keeps for the error message.
The model is loaded for this one picture and freed with the process: MLX's Metal cache only goes with the process, and
nothing may stay beside YuE2.
"""

import json
import sys
import time

# Krea's licence asks for a content filter, FLUX 3's and Klein 9B's for a filter or a review; this is one of the classifiers Krea's
# names, the one the ComfyUI node and Black Forest Labs' FLUX 3 reference code use too.
NSFW_REPO = "Falconsai/nsfw_image_detection"
NSFW_THRESHOLD = 0.85

GATED = (
    "Error: Hugging Face refused the download. Accept the model's licence on huggingface.co and put an"
    " access token of that account into ~/.config/tonwerk/huggingface.token."
)


def emit(event: dict) -> None:
    print("YUEUI " + json.dumps(event), flush=True)


def peak_memory_gb() -> float | None:
    try:
        import mlx.core as mx

        return round(mx.get_peak_memory() / 1e9, 2)
    except Exception:  # an older mlx only has it under mx.metal
        return None


def refused(error: Exception) -> bool:
    """A gated repository answers 401/403 until its licence is accepted with the token's account."""
    return type(error).__name__ in ("GatedRepoError", "RepositoryNotFoundError") or "401" in str(error) or "403" in str(error)


def missing(name: str) -> int:
    print(f"Error: the Python package '{name}' is missing; run deploy/install-images.sh again.", flush=True)
    return 1


class Progress:
    """mflux calls every registered object that has call_in_loop once per denoising step."""

    def __init__(self, steps: int):
        self.steps = max(steps, 1)

    def call_in_loop(self, t, seed, prompt, latents, config, time_steps, **_) -> None:
        emit({"event": "stage", "stage": "painting", "fraction": round(min((t + 1) / self.steps, 1), 3)})


def load_mflux(request: dict):
    """The model's class by family; all take the same arguments and paint through generate_image."""
    from mflux.models.common.config import ModelConfig

    name = request["model"]
    if name.startswith("z-image"):
        from mflux.models.z_image import ZImage as Model
    elif name.startswith("krea-2"):
        from mflux.models.krea2 import Krea2 as Model
    else:
        from mflux.models.flux2.variants import Flux2Klein as Model
    # LoRAs are applied when the weights load (mflux bakes them into the quantized layers), one strength each.
    loras = request.get("loras") or []
    return Model(
        model_config=ModelConfig.from_name(name),
        quantize=request.get("quantize"),
        lora_paths=[lora["path"] for lora in loras] or None,
        lora_scales=[float(lora["strength"]) for lora in loras] or None,
    )


def paint_mflux(model, request: dict, steps: int):
    model.callbacks.register(Progress(steps))
    arguments = dict(
        seed=int(request["seed"]),
        prompt=request["prompt"],
        num_inference_steps=steps,
        width=int(request.get("width") or 1024),
        height=int(request.get("height") or 1024),
    )
    # Distilled Klein and Krea 2 Turbo sample at guidance 1; Z-Image Turbo takes none and picks its own.
    if not request["model"].startswith("z-image"):
        arguments["guidance"] = 1.0
    return model.generate_image(**arguments).image, None


def load_flux3(request: dict):
    from flux3_mlx import Flux3Klein

    # low_memory: the upsampler (a 9B model at 4 bits) leaves before the transformer loads, so the picture holds about
    # 12 GB rather than both at once.
    return Flux3Klein.from_pretrained(request["model"], upsampler=request.get("upsampler"), low_memory=True)


def paint_flux3(pipe, request: dict, steps: int):
    def step(done: int, total: int) -> None:
        emit({"event": "stage", "stage": "painting", "fraction": round(min(done / max(total, 1), 1), 3)})

    image, record = pipe(
        request["prompt"],
        width=int(request.get("width") or 1024),
        height=int(request.get("height") or 1024),
        steps=steps,
        seed=int(request["seed"]),
        upsample=bool(request.get("upsampler")),
        callback=step,
    )
    # What the upsampler wrote is what the model painted; the dialog shows it under the picture.
    return image, record.get("prompt") if record.get("upsampled") else None


def flagged(image) -> bool:
    """Falconsai's ViT on the CPU through transformers (mflux brings it, flux3-mlx's mlx-vlm too, torch is installed
    beside); a few hundred MB, cached in HF_HOME."""
    from transformers import pipeline

    classify = pipeline("image-classification", model=NSFW_REPO, device="cpu")
    scores = {r["label"].lower(): r["score"] for r in classify(image.convert("RGB"))}
    return scores.get("nsfw", 0.0) >= NSFW_THRESHOLD


def main() -> int:
    request = json.load(sys.stdin)
    flux3 = request.get("runner") == "flux3-mlx"
    if flux3 and request.get("loras"):
        print("Error: FLUX 3 Klein takes no LoRAs; flux3-mlx has no loader for them.", flush=True)
        return 1
    steps = int(request.get("steps") or 4)
    emit({"event": "stage", "stage": "loading", "fraction": 0})
    started = time.perf_counter()
    try:
        # Imported here so that a missing package shows up as the picture's error rather than before the first event.
        model = load_flux3(request) if flux3 else load_mflux(request)
    except ModuleNotFoundError as gone:
        return missing(gone.name)
    except Exception as error:
        if refused(error):
            print(GATED, flush=True)
            return 1
        raise
    loaded = time.perf_counter()

    emit({"event": "stage", "stage": "painting", "fraction": 0})
    try:
        image, painted = paint_flux3(model, request, steps) if flux3 else paint_mflux(model, request, steps)
    except Exception as error:
        # flux3-mlx downloads the upsampler only when it first expands a prompt.
        if refused(error):
            print(GATED, flush=True)
            return 1
        raise
    finished = time.perf_counter()
    del model

    if request.get("contentFilter"):
        try:
            if flagged(image):
                print("Error: the content filter held the picture back; describe it differently.", flush=True)
                return 1
        except ModuleNotFoundError as gone:
            return missing(gone.name)

    # PIL directly rather than the runner's save, which in mflux swallows errors and writes PNGs of several megabytes:
    # a cover goes into every export and streaming copy.
    image.convert("RGB").save(request["output"], "JPEG", quality=92)
    emit(
        {
            "event": "done",
            "loadSeconds": round(loaded - started, 2),
            "paintSeconds": round(finished - loaded, 2),
            "peakMemoryGb": peak_memory_gb(),
            "paintedPrompt": painted,
        }
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
