"""Fine-tune a land-cover segmentation model on annotated tiles.

Typical use from the labeler GUI, or by hand:

    python train.py --images data/images --masks data/masks ^
        --arch deeplabv3plus --encoder mit_b0 --epochs 60 --batch 4

Sensible defaults for a modest annotation set (a few dozen 512x512 tiles):
freeze the ImageNet encoder for the first epochs so the randomly-initialised decoder
does not wash out the pretrained features, then unfreeze and fine-tune everything.
"""
from __future__ import annotations

import sys as _sys

# Windows consoles default to GBK here, and torch's ONNX exporter prints emoji.
for _stream in (_sys.stdout, _sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

import argparse
import json
import os
import time
from pathlib import Path

import torch
from torch.utils.data import DataLoader, Subset

from dataset import LandCoverDataset, load_classes
from model import ARCHES, RECOMMENDED_ENCODERS, DiceCELoss, build_model, encoder_parameters

ROOT = Path(__file__).resolve().parent


def parse_args():
    p = argparse.ArgumentParser(description="Fine-tune land cover model")
    p.add_argument("--images", type=Path, default=ROOT / "data" / "images")
    p.add_argument("--masks", type=Path, default=ROOT / "data" / "masks")
    p.add_argument("--val-images", type=Path, default=None)
    p.add_argument("--val-masks", type=Path, default=None)
    p.add_argument("--classes", type=Path, default=ROOT / "classes.json")
    p.add_argument("--out", type=Path, default=ROOT / "runs" / "best.pt")
    p.add_argument("--arch", type=str, default="deeplabv3plus", choices=ARCHES)
    p.add_argument("--encoder", type=str, default="mit_b0",
                   help=f"推荐: {', '.join(RECOMMENDED_ENCODERS)}")
    p.add_argument("--no-pretrained", action="store_true")
    p.add_argument("--epochs", type=int, default=60)
    p.add_argument("--batch", type=int, default=4)
    p.add_argument("--lr", type=float, default=6e-4)
    p.add_argument("--min-lr", type=float, default=1e-6)
    p.add_argument("--size", type=int, default=512)
    p.add_argument("--workers", type=int, default=0 if os.name == "nt" else 2,
                   help="DataLoader 工作进程数。Windows 上用 spawn，每个 epoch 都要重新 "
                        "import torch（实测约 10 秒），所以默认 0——开了反而慢约 7 倍。")
    p.add_argument("--val-ratio", type=float, default=0.15)
    p.add_argument("--dice-weight", type=float, default=0.5)
    p.add_argument("--freeze-encoder-epochs", type=int, default=5,
                   help="前 N 轮只训练解码器；标注很少时能显著稳定收敛")
    p.add_argument("--no-class-weights", action="store_true",
                   help="关闭按类别频率加权的 CE（默认开启，缓解道路/水体样本稀少问题）")
    p.add_argument("--seed", type=int, default=42)
    p.add_argument("--device", type=str, default="cuda" if torch.cuda.is_available() else "cpu")
    return p.parse_args()


def class_histogram(ds, num_classes: int, ignore_index: int, sample_limit: int = 300) -> torch.Tensor:
    """Counts valid pixels per class over a subset, for inverse-frequency CE weights."""
    n = min(len(ds), sample_limit)
    counts = torch.zeros(num_classes, dtype=torch.float64)
    step = max(1, len(ds) // n)
    for i in range(0, len(ds), step):
        _, y = ds[i]
        valid = y != ignore_index
        counts += torch.bincount(y[valid].flatten(), minlength=num_classes).double()
    return counts


def make_class_weights(counts: torch.Tensor, device) -> torch.Tensor:
    """Inverse-frequency weights, normalised to mean 1 and clipped so rare classes don't dominate."""
    total = counts.sum()
    present = counts > 0
    if total == 0 or not bool(present.any()):
        return torch.ones(len(counts), device=device)

    weights = torch.ones_like(counts)
    freq = counts[present] / total
    weights[present] = 1.0 / freq
    weights[present] /= weights[present].mean()
    weights = weights.clamp(max=20.0)
    return weights.float().to(device)


def main():
    args = parse_args()
    torch.manual_seed(args.seed)

    cfg = load_classes(args.classes)
    num_classes = int(cfg["num_classes"])
    ignore_index = int(cfg.get("ignore_index", 255))

    device = torch.device(args.device)
    print(f"device={device}, classes={num_classes}, arch={args.arch}, encoder={args.encoder}")

    train_full = LandCoverDataset(
        args.images, args.masks, num_classes, ignore_index, augment=True, size=args.size
    )
    eval_full = LandCoverDataset(
        args.images, args.masks, num_classes, ignore_index, augment=False, size=args.size
    )

    external_val = (
        args.val_images is not None
        and args.val_masks is not None
        and Path(args.val_images).exists()
        and Path(args.val_masks).exists()
        and any(Path(args.val_images).iterdir())
    )

    if external_val:
        train_ds = train_full
        val_ds = LandCoverDataset(
            args.val_images, args.val_masks, num_classes, ignore_index, augment=False, size=args.size
        )
        print(f"train={len(train_ds)} (所有瓦片), val={len(val_ds)} (val_images)")
    else:
        # Split by index into an augmenting copy and a clean copy, so validation never
        # sees flipped/rotated tiles (random_split on one dataset would leak augmentation).
        n_val = max(1, int(len(train_full) * args.val_ratio)) if len(train_full) > 1 else 0
        if n_val <= 0 or n_val >= len(train_full):
            train_ds, val_ds = train_full, eval_full
            print(f"瓦片太少({len(train_full)})，训练集与验证集相同，指标仅供参考")
        else:
            g = torch.Generator().manual_seed(args.seed)
            perm = torch.randperm(len(train_full), generator=g).tolist()
            train_ds = Subset(train_full, perm[n_val:])
            val_ds = Subset(eval_full, perm[:n_val])
            print(f"train={len(train_ds)}, val={len(val_ds)} (按瓦片划分, seed={args.seed})")

    train_loader = DataLoader(
        train_ds, batch_size=args.batch, shuffle=True, num_workers=args.workers,
        drop_last=len(train_ds) > args.batch,
        # Without this the workers are torn down and respawned every epoch, which on Windows
        # means paying the torch import again each time.
        persistent_workers=args.workers > 0,
    )
    val_loader = DataLoader(val_ds, batch_size=args.batch, shuffle=False, num_workers=args.workers,
                            persistent_workers=args.workers > 0)

    model = build_model(
        num_classes, encoder=args.encoder, arch=args.arch, pretrained=not args.no_pretrained
    ).to(device)

    class_weights = None
    if not args.no_class_weights:
        counts = class_histogram(train_full, num_classes, ignore_index)
        class_weights = make_class_weights(counts, device)
        total = counts.sum().item()
        pct = [f"{c}:{(n / total * 100 if total else 0):.2f}%" for c, n in enumerate(counts.tolist())]
        print(f"[类别分布] {'  '.join(pct)}")
        print(f"[类别权重] {[round(w, 2) for w in class_weights.tolist()]}")

    criterion = DiceCELoss(num_classes, ignore_index, dice_weight=args.dice_weight,
                           class_weights=class_weights)
    optim = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=1e-4)
    sched = torch.optim.lr_scheduler.CosineAnnealingLR(
        optim, T_max=max(1, args.epochs), eta_min=args.min_lr
    )

    args.out.parent.mkdir(parents=True, exist_ok=True)
    best_miou = -1.0
    history = []
    encoder_frozen = False

    for epoch in range(1, args.epochs + 1):
        # Warm up the decoder against frozen ImageNet features, then fine-tune everything.
        want_frozen = epoch <= args.freeze_encoder_epochs
        if want_frozen != encoder_frozen:
            encoder_frozen = want_frozen
            for p in encoder_parameters(model):
                p.requires_grad = not encoder_frozen
            print(f"  [encoder] {'冻结' if encoder_frozen else '解冻'} (epoch {epoch})")

        model.train()
        t0 = time.time()
        running = 0.0
        for x, y in train_loader:
            x, y = x.to(device), y.to(device)
            optim.zero_grad(set_to_none=True)
            logits = model(x)
            loss = criterion(logits, y)
            loss.backward()
            torch.nn.utils.clip_grad_norm_(model.parameters(), 5.0)
            optim.step()
            running += loss.item() * x.size(0)
        sched.step()
        train_loss = running / max(1, len(train_ds))

        miou, val_loss, per_class = evaluate(model, val_loader, criterion, num_classes,
                                             ignore_index, device, cfg)
        history.append({"epoch": epoch, "train_loss": train_loss, "val_loss": val_loss,
                        "miou": miou, "per_class_iou": per_class})
        print(
            f"epoch {epoch:03d}/{args.epochs}  "
            f"train_loss={train_loss:.4f}  val_loss={val_loss:.4f}  mIoU={miou:.4f}  "
            f"lr={sched.get_last_lr()[0]:.2e}  ({time.time() - t0:.1f}s)"
        )

        if epoch % 10 == 0 or epoch == args.epochs:
            shown = "  ".join(
                f"{cfg['classes'][c]['name']}={per_class.get(c, 0):.3f}"
                for c in sorted(per_class)
            )
            print(f"    per-class IoU: {shown}")

        if miou >= best_miou:
            best_miou = miou
            torch.save(
                {
                    "model": model.state_dict(),
                    "num_classes": num_classes,
                    "arch": args.arch,
                    "encoder": args.encoder,
                    "size": args.size,
                    "miou": miou,
                    "classes": cfg,
                },
                args.out,
            )
            print(f"  saved best -> {args.out}  (mIoU={miou:.4f})")

    with open(args.out.parent / "history.json", "w", encoding="utf-8") as f:
        json.dump(history, f, indent=2, ensure_ascii=False)
    print(f"done. best mIoU={best_miou:.4f}")
    return 0


@torch.no_grad()
def evaluate(model, loader, criterion, num_classes, ignore_index, device, cfg=None):
    model.eval()
    inter = torch.zeros(num_classes, device=device)
    union = torch.zeros(num_classes, device=device)
    loss_sum = 0.0
    n = 0

    for x, y in loader:
        x, y = x.to(device), y.to(device)
        logits = model(x)
        loss_sum += criterion(logits, y).item() * x.size(0)
        n += x.size(0)

        pred = logits.argmax(1)
        valid = y != ignore_index
        for c in range(num_classes):
            p = (pred == c) & valid
            t = (y == c) & valid
            inter[c] += (p & t).sum()
            union[c] += (p | t).sum()

    ious = inter / union.clamp(min=1)
    present = union > 0
    miou = ious[present].mean().item() if bool(present.any()) else 0.0
    per_class = {c: round(ious[c].item(), 4) for c in range(num_classes) if bool(present[c])}
    return miou, loss_sum / max(1, n), per_class


if __name__ == "__main__":
    raise SystemExit(main())
