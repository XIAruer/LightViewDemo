"""Dataset: image / mask pairs, same stem, 512 tiles recommended."""
from __future__ import annotations

import json
import random
from pathlib import Path

import numpy as np
import torch
from PIL import Image
from torch.utils.data import Dataset

IMG_EXTS = {".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp"}


def load_classes(cfg_path: Path) -> dict:
    with open(cfg_path, "r", encoding="utf-8") as f:
        return json.load(f)


class LandCoverDataset(Dataset):
    def __init__(
        self,
        image_dir: Path,
        mask_dir: Path,
        num_classes: int,
        ignore_index: int = 255,
        augment: bool = False,
        size: int = 512,
    ):
        self.image_dir = Path(image_dir)
        self.mask_dir = Path(mask_dir)
        self.num_classes = num_classes
        self.ignore_index = ignore_index
        self.augment = augment
        self.size = size

        self.items: list[tuple[Path, Path]] = []
        for img in sorted(self.image_dir.iterdir()):
            if img.suffix.lower() not in IMG_EXTS:
                continue
            mask = self.mask_dir / f"{img.stem}.png"
            if not mask.exists():
                mask = self.mask_dir / f"{img.stem}.tif"
            if mask.exists():
                self.items.append((img, mask))

        if not self.items:
            raise FileNotFoundError(
                f"No image/mask pairs in {image_dir} + {mask_dir}. "
                "Name must match: foo.png <-> foo.png (mask)."
            )

    def __len__(self) -> int:
        return len(self.items)

    def __getitem__(self, idx: int):
        img_path, mask_path = self.items[idx]
        img = Image.open(img_path).convert("RGB")
        mask = Image.open(mask_path)
        if mask.mode != "L":
            mask = mask.convert("L")

        if img.size != mask.size:
            mask = mask.resize(img.size, Image.NEAREST)

        if self.augment:
            img, mask = self._augment(img, mask)

        # resize/pad to training size
        img, mask = self._resize_square(img, mask, self.size)

        img_np = np.asarray(img, dtype=np.float32) / 255.0  # HWC RGB
        mask_np = np.asarray(mask, dtype=np.int64)

        # clamp unknown ids to ignore
        mask_np = np.where(
            (mask_np >= self.num_classes) & (mask_np != self.ignore_index),
            self.ignore_index,
            mask_np,
        )

        x = torch.from_numpy(img_np).permute(2, 0, 1).contiguous()  # CHW
        y = torch.from_numpy(mask_np)
        return x, y

    @staticmethod
    def _resize_square(img: Image.Image, mask: Image.Image, size: int):
        w, h = img.size
        if w == size and h == size:
            return img, mask
        # crop center square then resize — keeps aspect for tiles
        side = min(w, h)
        left = (w - side) // 2
        top = (h - side) // 2
        img = img.crop((left, top, left + side, top + side)).resize((size, size), Image.BILINEAR)
        mask = mask.crop((left, top, left + side, top + side)).resize((size, size), Image.NEAREST)
        return img, mask

    def _augment(self, img: Image.Image, mask: Image.Image):
        if random.random() < 0.5:
            img = img.transpose(Image.FLIP_LEFT_RIGHT)
            mask = mask.transpose(Image.FLIP_LEFT_RIGHT)
        if random.random() < 0.5:
            img = img.transpose(Image.FLIP_TOP_BOTTOM)
            mask = mask.transpose(Image.FLIP_TOP_BOTTOM)
        if random.random() < 0.3:
            # 90-degree rotations
            k = random.choice([1, 2, 3])
            img = img.rotate(90 * k, expand=False)
            mask = mask.rotate(90 * k, expand=False, resample=Image.NEAREST)
        return img, mask
