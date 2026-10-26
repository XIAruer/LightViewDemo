"""Export trained checkpoint to ONNX for the C# demo (LandCoverDemo)."""
from __future__ import annotations

import sys as _sys

# Windows consoles default to GBK here, and torch's ONNX exporter prints emoji.
for _stream in (_sys.stdout, _sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

import argparse
from pathlib import Path

import torch

from model import build_model

ROOT = Path(__file__).resolve().parent


def parse_args():
    p = argparse.ArgumentParser()
    p.add_argument("--ckpt", type=Path, default=ROOT / "runs" / "best.pt")
    p.add_argument("--out", type=Path, default=ROOT.parent / "models" / "landcover_mountain_7c.onnx")
    p.add_argument("--size", type=int, default=512)
    # torch>=2.9 implements opset 18+ only; requesting 17 triggers a lossy
    # auto-downgrade with a warning.
    p.add_argument("--opset", type=int, default=18)
    return p.parse_args()


def main():
    args = parse_args()
    ckpt = torch.load(args.ckpt, map_location="cpu", weights_only=False)
    num_classes = int(ckpt["num_classes"])
    encoder = ckpt.get("encoder", "mobilenet_v2")
    arch = ckpt.get("arch", "deeplabv3plus")
    size = int(ckpt.get("size", args.size))

    model = build_model(num_classes, encoder=encoder, arch=arch, pretrained=False)
    model.load_state_dict(ckpt["model"])
    model.eval()

    dummy = torch.zeros(1, 3, size, size, dtype=torch.float32)
    args.out.parent.mkdir(parents=True, exist_ok=True)

    torch.onnx.export(
        model,
        dummy,
        str(args.out),
        input_names=["input"],
        output_names=["output"],
        opset_version=args.opset,
        dynamic_axes={
            "input": {0: "batch", 2: "h", 3: "w"},
            "output": {0: "batch", 2: "h", 3: "w"},
        },
    )
    print(f"exported: {args.out}")
    print(f"classes={num_classes}, arch={arch}, encoder={encoder}, size={size}")
    print("C#: copy to models/ and run with --model <path> --tile", size)


if __name__ == "__main__":
    main()
