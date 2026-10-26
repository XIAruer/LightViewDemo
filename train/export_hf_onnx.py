"""Export a HuggingFace segmentation checkpoint to ONNX for the C# pipeline.

This is the "use somebody else's weights" counterpart to `export_onnx.py` (which exports
our own `runs/best.pt`). Neither one trains anything: this script downloads a checkpoint
and rewrites it as `[1,3,H,W]` RGB/255 -> `[1,C,H,W]` logits, which is the contract
`OnnxLandCoverSegmenter` expects.

    python export_hf_onnx.py --repo IgorNer/segformer-b5-loveda --out ..\models\segformer_b5_loveda.onnx

Two things the ONNX graph alone cannot express and that therefore go into the sidecar
(`<out>.onnx.json`, written automatically):

* how the input has to be normalised (SegFormer wants ImageNet mean/std, not raw 0..1), and
* what the output channel ids mean (this checkpoint's classes are not in palette order).

Weights stay in one file: onnxruntime reads external-data sidecars fine, but a single
self-contained model is one less thing to lose when copying `models/` around.
"""
from __future__ import annotations

import sys as _sys

for _stream in (_sys.stdout, _sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

import argparse
import json
from pathlib import Path

import torch


def parse_args():
    p = argparse.ArgumentParser(description="HF segmentation checkpoint -> ONNX")
    p.add_argument("--repo", required=True, help="HuggingFace repo id")
    p.add_argument("--out", type=Path, required=True, help="where to write the .onnx")
    p.add_argument("--size", type=int, default=512, help="tile size used for tracing")
    p.add_argument("--opset", type=int, default=18)
    p.add_argument("--class-map", type=str, default=None,
                   help="逗号分隔的 模型输出下标->调色板ID 映射；缺省则不写 class_map")
    p.add_argument("--no-sidecar", action="store_true",
                   help="只导出 onnx，不写 <out>.onnx.json")
    return p.parse_args()


def main():
    args = parse_args()

    # Imported here so --help works without transformers installed.
    from transformers import AutoImageProcessor, AutoModelForSemanticSegmentation

    print(f"[导出] 加载 {args.repo} ...")
    model = AutoModelForSemanticSegmentation.from_pretrained(args.repo)
    model.eval()

    num_labels = int(model.config.num_labels)
    id2label = {int(k): v for k, v in (model.config.id2label or {}).items()}
    print(f"[导出] {type(model).__name__} | {num_labels} 类 | "
          f"{sum(p.numel() for p in model.parameters()) / 1e6:.1f}M 参数")
    for i in sorted(id2label):
        print(f"        {i}: {id2label[i]}")

    dummy = torch.zeros(1, 3, args.size, args.size, dtype=torch.float32)
    args.out.parent.mkdir(parents=True, exist_ok=True)

    print(f"[导出] 追踪 {args.size}x{args.size} -> ONNX (opset {args.opset}) ...")
    with torch.no_grad():
        torch.onnx.export(
            model,
            (dummy,),
            str(args.out),
            input_names=["input"],
            output_names=["logits"],
            opset_version=args.opset,
            # Dynamic H/W: the C# side runs whatever --tile says, and this model is fully
            # convolutional apart from the positional-embedding interpolation it does anyway.
            dynamic_axes={
                "input": {0: "batch", 2: "height", 3: "width"},
                "logits": {0: "batch", 2: "out_height", 3: "out_width"},
            },
        )

    fuse_external_data(args.out)
    verify(str(args.out), model, dummy, args.size)

    if not args.no_sidecar:
        write_sidecar(args, model, num_labels, id2label)

    size_mb = sum(f.stat().st_size for f in model_files(args.out)) / 1e6
    print(f"[导出] 完成: {args.out} ({size_mb:.0f} MB)")
    return 0


def model_files(out: Path) -> list[Path]:
    """The .onnx plus any external-data sidecars the exporter may have emitted."""
    return [out] + sorted(out.parent.glob(out.name + ".data*"))


def fuse_external_data(out: Path):
    """
    Re-save a model that the exporter split into a .data sidecar as a single file.

    340 MB is well under protobuf's 2 GB ceiling, so the split is pure friction here — it
    means `models/` holds two files that must travel together.
    """
    sidecars = [f for f in model_files(out) if f != out]
    if not sidecars:
        return

    import onnx

    print(f"[导出] 合并 {len(sidecars)} 个外置数据文件到单文件 ...")
    model = onnx.load(str(out))  # resolves the sidecars relative to the model path
    onnx.save_model(model, str(out), save_as_external_data=False)
    for f in sidecars:
        f.unlink()


def verify(path: str, model, dummy, size: int):
    """ONNXRuntime vs PyTorch on the same input: shapes must match and argmax must agree."""
    import numpy as np
    import onnxruntime as ort

    print("[校验] ONNXRuntime 与 PyTorch 对比 ...")
    sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
    inp = sess.get_inputs()[0]
    print(f"        输入 {inp.name} {inp.shape} {inp.type}")

    rng = np.random.default_rng(0)
    x = (rng.random((1, 3, size, size), dtype=np.float32) * 0.8 + 0.1)
    onnx_logits = sess.run(None, {inp.name: x})[0]

    with torch.no_grad():
        torch_logits = model(torch.from_numpy(x)).logits.numpy()

    if onnx_logits.shape != torch_logits.shape:
        raise SystemExit(f"[校验] 失败: 形状不一致 {onnx_logits.shape} vs {torch_logits.shape}")

    max_diff = float(np.abs(onnx_logits - torch_logits).max())
    same = float((onnx_logits.argmax(1) == torch_logits.argmax(1)).mean())
    print(f"        输出 {onnx_logits.shape}，最大误差 {max_diff:.2e}，argmax 一致率 {same * 100:.2f}%")
    if same < 0.999:
        raise SystemExit("[校验] 失败: ONNX 与 PyTorch 的 argmax 差异过大，导出不可信")


def write_sidecar(args, model, num_labels: int, id2label: dict):
    import json as _json

    proc = read_processor_config(args.repo)
    mean = list(proc.get("image_mean", [0.485, 0.456, 0.406]))
    std = list(proc.get("image_std", [0.229, 0.224, 0.225]))

    meta = {
        "name": f"{args.repo} ({num_labels} 类)",
        "input_size": args.size,
        "normalize": "mean_std",
        "mean": [round(float(v), 4) for v in mean],
        "std": [round(float(v), 4) for v in std],
        "class_names": [id2label.get(i, f"class_{i}") for i in range(num_labels)],
        "notes": _notes(args, id2label, num_labels),
    }
    if args.class_map:
        meta["class_map"] = [int(v) for v in args.class_map.split(",")]
        if len(meta["class_map"]) != num_labels:
            raise SystemExit(
                f"[sidecar] class_map 有 {len(meta['class_map'])} 项，模型是 {num_labels} 类，必须一一对应")

    path = Path(str(args.out) + ".json")
    with open(path, "w", encoding="utf-8") as f:
        _json.dump(meta, f, ensure_ascii=False, indent=2)
    print(f"[sidecar] {path}")


def read_processor_config(repo: str) -> dict:
    """The processor's normalisation constants; falls back to ImageNet if unavailable."""
    try:
        from transformers import AutoImageProcessor

        return AutoImageProcessor.from_pretrained(repo).to_dict()
    except Exception as ex:
        print(f"[sidecar] 读不到 preprocessor_config（{ex}），用 ImageNet 默认值")
        return {}


def _notes(args, id2label: dict, num_labels: int) -> str:
    pairs = "  ".join(f"{i}={id2label.get(i, '?')}" for i in range(num_labels))
    base = (f"HF 权重 {args.repo} 直接导出，未经训练。输入 RGB/255 + mean/std 归一化，"
            f"输出 logits 分辨率为输入的 1/4（C# 侧自动放大）。模型类别: {pairs}")
    if args.class_map:
        return base + "。class_map 已把模型输出下标映射到本项目调色板。"
    return base + "。注意：未提供 class_map，输出 ID 按调色板解释，语义可能错位。"


if __name__ == "__main__":
    raise SystemExit(main())
