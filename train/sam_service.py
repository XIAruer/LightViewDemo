"""Resident SAM segmentation service for the labeling GUI.

The GUI is a WinExe that cannot host PyTorch, and SAM's checkpoint is a PyTorch state_dict that
C# has no way to load. So the model lives here, in a process that stays up for the whole session
and answers one JSON message per line on stdin. That is what makes the interactive tool usable:
`import torch` alone costs seconds, so the model must be loaded once and then only the cheap
prompt query runs per click.

Protocol (one JSON object per line, request on stdin -> response on stdout):

    {"cmd":"load","model":"sam2.1_hiera_tiny","device":"auto"}   ->  ok/device/model/family
    {"cmd":"encode","key":"<id>","w":W,"h":H,"rgb":"<b64 W*H*3 uint8 RGB>"}  ->  ok/ms
    {"cmd":"prompt","key":"<id>","points":[[x,y,true|false],...],"box":[x0,y0,x1,y1]|null}
                                                                 ->  ok/w/h/mask(b64)/score/ms
    {"cmd":"reset"} / {"cmd":"ping"} / {"cmd":"quit"}

Only the response goes to stdout — every log line goes to stderr, because a stray print would
corrupt the stream and stall the GUI's reader.

Splitting `encode` from `prompt` is the whole performance story (the same split Geo-SAM makes):
the image encoder is expensive and runs once per chip, the prompt decoder is cheap and runs per
click. The GUI sends the same chip bytes for every refinement click and we answer from the
feature cache.

    python -u train/sam_service.py
"""
from __future__ import annotations

import sys as _sys

# Windows consoles are GBK; keep every diagnostic decodable. stdout is our protocol and is
# written as UTF-8 JSON regardless.
for _stream in (_sys.stdout, _sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

import base64
import json
import os
import time
import traceback
from collections import OrderedDict
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parent.parent
WEIGHTS_DIR = ROOT / "models" / "sam"

# How many encoded chips to keep. Each one is ~20 MB of image features, so this is a few tens of
# MB of RAM in exchange for "every refinement click after the first is instant".
MAX_CHIPS = 4

MODEL_ALIASES = {
    "sam2.1_t": "sam2.1_hiera_tiny",
    "sam2.1_s": "sam2.1_hiera_small",
    "sam2.1_b": "sam2.1_hiera_base_plus",
    "sam2.1_l": "sam2.1_hiera_large",
    "sam2_t": "sam2_hiera_tiny",
    "sam2_s": "sam2_hiera_small",
    "sam2_b": "sam2_hiera_base_plus",
    "sam2_l": "sam2_hiera_large",
    "sam_b": "sam_vit_b",
    "sam_l": "sam_vit_l",
}

# Checkpoint stem -> the yaml that ships inside the `sam2` package. The base-plus yaml is
# spelled "b+", which is easy to get wrong.
SAM2_CONFIGS = {
    "sam2.1_hiera_tiny": "configs/sam2.1/sam2.1_hiera_t.yaml",
    "sam2.1_hiera_small": "configs/sam2.1/sam2.1_hiera_s.yaml",
    "sam2.1_hiera_base_plus": "configs/sam2.1/sam2.1_hiera_b+.yaml",
    "sam2.1_hiera_large": "configs/sam2.1/sam2.1_hiera_l.yaml",
    "sam2_hiera_tiny": "configs/sam2/sam2_hiera_t.yaml",
    "sam2_hiera_small": "configs/sam2/sam2_hiera_s.yaml",
    "sam2_hiera_base_plus": "configs/sam2/sam2_hiera_b+.yaml",
    "sam2_hiera_large": "configs/sam2/sam2_hiera_l.yaml",
}


def log(msg: str) -> None:
    print(msg, file=_sys.stderr, flush=True)


def reply(obj: dict) -> None:
    _sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
    _sys.stdout.flush()


def pick_device(requested: str = "auto") -> str:
    if requested and requested != "auto":
        return requested
    env = os.environ.get("LC_SAM_DEVICE", "").strip()
    if env:
        return env

    import torch

    if torch.cuda.is_available():
        return "cuda"
    if getattr(torch.backends, "mps", None) is not None and torch.backends.mps.is_available():
        return "mps"
    return "cpu"


def pick_model(device: str) -> str:
    """Bigger weights where there is a GPU to hide the cost, smallest on CPU."""
    env = os.environ.get("LC_SAM_MODEL", "").strip()
    if env:
        return MODEL_ALIASES.get(env, env)
    return "sam2.1_hiera_base_plus" if device == "cuda" else "sam2.1_hiera_tiny"


def find_checkpoint(model: str) -> Path:
    for name in (f"{model}.pt", f"{model}.pth"):
        p = WEIGHTS_DIR / name
        if p.exists():
            return p
    raise FileNotFoundError(
        f"找不到 {model} 的权重，期望在 {WEIGHTS_DIR / (model + '.pt')}。"
        f"从 https://huggingface.co/facebook/{model} 下载 .pt 放到该目录即可。")


class Chip:
    """One encoded window plus the predictor that owns its features."""

    def __init__(self, predictor, w: int, h: int):
        self.predictor = predictor
        self.w = w
        self.h = h


class Service:
    def __init__(self):
        self.device = "cpu"
        self.model_name = ""
        self.family = ""
        self.model = None
        self.predictor_cls = None
        self.chips: OrderedDict[str, Chip] = OrderedDict()

    # ---------------------------------------------------------------- load

    def load(self, model: str, device: str) -> dict:
        t0 = time.time()
        self.device = pick_device(device)
        self.model_name = MODEL_ALIASES.get(model, model) if model else pick_model(self.device)
        ckpt = find_checkpoint(self.model_name)

        import torch

        torch.set_grad_enabled(False)
        is_sam2 = self.model_name.startswith("sam2")
        log(f"[sam] torch {torch.__version__} (cuda build={'cuda' in torch.__version__ or torch.version.cuda is not None}), "
            f"requested device={self.device}")

        if is_sam2:
            from sam2.build_sam import build_sam2
            from sam2.sam2_image_predictor import SAM2ImagePredictor

            cfg = SAM2_CONFIGS[self.model_name]
            # build_sam2 defaults to device="cuda" and will throw on a CPU-only torch build
            # before we ever get to move the model ourselves — pass it explicitly.
            self.model = build_sam2(cfg, ckpt_path=str(ckpt), device=self.device)
            self.predictor_cls = SAM2ImagePredictor
            self.family = "sam2"
        else:
            from segment_anything import SamPredictor, sam_model_registry

            arch = "vit_l" if "vit_l" in self.model_name else "vit_b"
            self.model = sam_model_registry[arch](checkpoint=str(ckpt))
            self.predictor_cls = SamPredictor
            self.family = "sam1"

        self.model.to(self.device)
        self.model.eval()
        self.chips.clear()
        log(f"[sam] loaded {self.model_name} ({self.family}) on {self.device} "
            f"in {time.time() - t0:.1f}s")
        return {"ok": True, "device": self.device, "model": self.model_name, "family": self.family}

    # ------------------------------------------------------------- encode

    def encode(self, key: str, w: int, h: int, rgb_b64: str) -> dict:
        if self.model is None:
            return {"ok": False, "error": "模型尚未加载"}

        t0 = time.time()
        raw = base64.b64decode(rgb_b64)
        if len(raw) != w * h * 3:
            return {"ok": False, "error": f"影像字节数不符: {len(raw)} != {w * h * 3}"}

        img = np.frombuffer(raw, np.uint8).reshape(h, w, 3)

        if key in self.chips:
            self.chips.move_to_end(key)
            return {"ok": True, "key": key, "cached": True, "ms": 0}

        # One predictor per chip: SAMPredictor keeps the encoded features on itself, so caching
        # features means caching predictors (they share the one heavy model).
        predictor = self.predictor_cls(self.model)
        predictor.set_image(img)
        self.chips[key] = Chip(predictor, w, h)
        while len(self.chips) > MAX_CHIPS:
            self.chips.popitem(last=False)

        ms = int((time.time() - t0) * 1000)
        log(f"[sam] encode {key} {w}x{h} -> {ms}ms (cache {len(self.chips)}/{MAX_CHIPS})")
        return {"ok": True, "key": key, "cached": False, "ms": ms}

    # ------------------------------------------------------------- prompt

    def prompt(self, key: str, points, box) -> dict:
        chip = self.chips.get(key)
        if chip is None:
            return {"ok": False, "error": f"没有缓存的窗口 {key}，请先 encode"}

        t0 = time.time()
        coords, labels = [], []
        for x, y, positive in (points or []):
            coords.append([float(x), float(y)])
            labels.append(1 if positive else 0)

        box_arr = None
        if box is not None and len(box) == 4:
            box_arr = np.array(box, dtype=np.float32)

        if not coords and box_arr is None:
            return {"ok": False, "error": "没有提示点"}

        kwargs = {}
        if coords:
            kwargs["point_coords"] = np.array(coords, dtype=np.float32)
            kwargs["point_labels"] = np.array(labels, dtype=np.int32)
        if box_arr is not None:
            kwargs["box"] = box_arr

        # Three candidate masks + their self-reported quality. multimask_output=True is what
        # makes SAM usable with a single click: one of the three is usually the "whole object".
        # Only the model's opinion goes back — the app enforces its own conventions (hard
        # negative disks, keep-the-clicked-blob) in one place instead of two.
        masks, scores, _ = chip.predictor.predict(multimask_output=True, **kwargs)
        mask, score = pick_mask(masks, scores, coords, labels)

        out = (mask.astype(np.uint8) * 255).tobytes()
        ms = int((time.time() - t0) * 1000)
        return {
            "ok": True,
            "key": key,
            "w": chip.w,
            "h": chip.h,
            "mask": base64.b64encode(out).decode("ascii"),
            "score": round(float(score), 4),
            "ms": ms,
        }

    def reset(self) -> dict:
        self.chips.clear()
        return {"ok": True}


def pick_mask(masks: np.ndarray, scores: np.ndarray, coords, labels):
    """
    Chooses among SAM's three candidates, and reports that candidate's own score.

    A click is a hard constraint first and a quality hint second: every positive point has to
    land inside the result and every negative point outside it. Among the candidates that
    satisfy that, the model's own predicted IoU decides. Ranking purely on "did it obey the
    clicks" would let a tiny fragment beat the whole object just because it happens to avoid
    the negative point — which is how one stray click used to shrink a 190k-pixel object to 5k.
    """
    cands = [(m.astype(bool), float(s)) for m, s in zip(masks, scores)]
    pos = [(x, y) for (x, y), lab in zip(coords, labels) if lab]
    neg = [(x, y) for (x, y), lab in zip(coords, labels) if not lab]

    def obeys(mb: np.ndarray) -> bool:
        return (all(sample(mb, x, y) for x, y in pos)
                and all(not sample(mb, x, y) for x, y in neg))

    feasible = [(mb, s) for mb, s in cands if obeys(mb)]
    if feasible:
        return max(feasible, key=lambda t: t[1])

    # Nothing satisfies every click (happens when the user rejects the only object there is):
    # fall back to whichever breaks the fewest, then to the model's score.
    def fit(mb: np.ndarray) -> float:
        total = len(pos) + len(neg)
        if not total:
            return 0.0
        hit = sum(1 for x, y in pos if sample(mb, x, y))
        miss = sum(1 for x, y in neg if not sample(mb, x, y))
        return (hit + miss) / total

    return max(cands, key=lambda t: (fit(t[0]), t[1]))


def sample(mask: np.ndarray, x: float, y: float) -> bool:
    h, w = mask.shape
    xi = min(w - 1, max(0, int(round(x))))
    yi = min(h - 1, max(0, int(round(y))))
    return bool(mask[yi, xi])



def main() -> int:
    log(f"[sam] service up (weights dir {WEIGHTS_DIR}, python {_sys.executable})")
    svc = Service()
    for line in _sys.stdin:
        # .NET's Encoding.UTF8 opens a stream with a byte-order mark; JSON chokes on it.
        line = line.strip().lstrip("﻿")
        if not line:
            continue
        try:
            req = json.loads(line)
            cmd = req.get("cmd")
            if cmd == "load":
                out = svc.load(req.get("model") or "", req.get("device") or "auto")
            elif cmd == "encode":
                out = svc.encode(req["key"], int(req["w"]), int(req["h"]), req["rgb"])
            elif cmd == "prompt":
                out = svc.prompt(req["key"], req.get("points"), req.get("box"))
            elif cmd == "reset":
                out = svc.reset()
            elif cmd == "ping":
                out = {"ok": True, "device": svc.device, "model": svc.model_name}
            elif cmd == "quit":
                reply({"ok": True})
                return 0
            else:
                out = {"ok": False, "error": f"未知命令: {cmd}"}
        except Exception as ex:  # keep the loop alive; a bad request must not kill the service
            log(traceback.format_exc())
            out = {"ok": False, "error": f"{type(ex).__name__}: {ex}"}
        reply(out)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
