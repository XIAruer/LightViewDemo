"""Segmentation models for land cover, built on segmentation_models_pytorch.

The encoder name is validated against what SMP actually ships — an invalid name costs a
long torchvision import before failing, and the previous default (`mobilenet_v3_large`)
is not an SMP encoder at all, so the error surfaced only after the whole data pipeline
had been built.
"""
from __future__ import annotations

import torch
import torch.nn as nn

# Architecture name -> SMP class. Chosen for CPU inference and ONNX exportability.
ARCHES = ("deeplabv3plus", "unet", "fpn", "manet", "linknet", "segformer")

# Encoders worth using on CPU, roughly cheapest first. All have ImageNet weights in SMP,
# and all export cleanly to a dynamic-H/W ONNX graph.
RECOMMENDED_ENCODERS = (
    "mobileone_s1",        # ~1.5M params, fastest
    "mobilenet_v2",        # ~2.2M params, very safe default
    "mit_b0",              # SegFormer-B0, ~3.7M params, best accuracy per param here
    "efficientnet-b0",     # ~5.3M params, strong all-rounder
    "mobileone_s4",        # ~5.6M params
    "mit_b1",              # SegFormer-B1, ~13.7M params
    "resnet34",            # ~24M params, robust but slower on CPU
    "efficientnet-b3",     # ~12M params
)


def available_encoders() -> list[str]:
    try:
        import segmentation_models_pytorch as smp

        return list(smp.encoders.get_encoder_names())
    except ImportError:
        return []


def build_model(
    num_classes: int,
    encoder: str = "mobilenet_v2",
    arch: str = "deeplabv3plus",
    pretrained: bool = True,
) -> nn.Module:
    """
    Builds the segmentation network. Falls back to a small U-Net when SMP is absent.
    """
    try:
        import segmentation_models_pytorch as smp
    except ImportError:
        print("[model] segmentation_models_pytorch 未安装，使用内置 TinyUNet（精度较低）")
        return TinyUNet(num_classes)

    valid = set(smp.encoders.get_encoder_names())
    if encoder not in valid:
        near = [e for e in RECOMMENDED_ENCODERS if e in valid]
        raise ValueError(
            f"不支持的 encoder '{encoder}'。\n"
            f"推荐: {', '.join(near)}\n"
            f"（全部可用: {len(valid)} 个，见 segmentation_models_pytorch.encoders.get_encoder_names()）"
        )

    arch = arch.lower()
    if arch not in ARCHES:
        raise ValueError(f"不支持的 arch '{arch}'，可用: {', '.join(ARCHES)}")

    weights = "imagenet" if pretrained else None
    cls = {
        "deeplabv3plus": smp.DeepLabV3Plus,
        "unet": smp.Unet,
        "fpn": smp.FPN,
        "manet": smp.MAnet,
        "linknet": smp.Linknet,
        "segformer": smp.Segformer,
    }[arch]

    try:
        model = cls(
            encoder_name=encoder,
            encoder_weights=weights,
            in_channels=3,
            classes=num_classes,
            activation=None,
        )
    except Exception as ex:
        if pretrained:
            print(f"[model] 预训练权重加载失败（{ex}），改用随机初始化")
            model = cls(encoder_name=encoder, encoder_weights=None,
                        in_channels=3, classes=num_classes, activation=None)
        else:
            raise

    n_params = sum(p.numel() for p in model.parameters())
    print(f"[model] {arch} + {encoder} | classes={num_classes} | "
          f"pretrained={'imagenet' if pretrained else 'no'} | {n_params/1e6:.2f}M params")
    return model


def encoder_parameters(model: nn.Module):
    """The encoder (backbone) sub-module's parameters — what we freeze on tiny datasets."""
    return model.encoder.parameters()


class TinyUNet(nn.Module):
    """~2–4M params fallback when SMP is unavailable. Weaker but trainable anywhere."""

    def __init__(self, num_classes: int, base: int = 32):
        super().__init__()
        c = base
        self.enc1 = self._block(3, c)
        self.enc2 = self._block(c, c * 2)
        self.enc3 = self._block(c * 2, c * 4)
        self.pool = nn.MaxPool2d(2)
        self.bottleneck = self._block(c * 4, c * 8)
        self.up3 = nn.ConvTranspose2d(c * 8, c * 4, 2, stride=2)
        self.dec3 = self._block(c * 8, c * 4)
        self.up2 = nn.ConvTranspose2d(c * 4, c * 2, 2, stride=2)
        self.dec2 = self._block(c * 4, c * 2)
        self.up1 = nn.ConvTranspose2d(c * 2, c, 2, stride=2)
        self.dec1 = self._block(c * 2, c)
        self.head = nn.Conv2d(c, num_classes, 1)

    @staticmethod
    def _block(i: int, o: int) -> nn.Sequential:
        return nn.Sequential(
            nn.Conv2d(i, o, 3, padding=1, bias=False),
            nn.BatchNorm2d(o),
            nn.ReLU(inplace=True),
            nn.Conv2d(o, o, 3, padding=1, bias=False),
            nn.BatchNorm2d(o),
            nn.ReLU(inplace=True),
        )

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        e1 = self.enc1(x)
        e2 = self.enc2(self.pool(e1))
        e3 = self.enc3(self.pool(e2))
        b = self.bottleneck(self.pool(e3))
        d3 = self.up3(b)
        d3 = self.dec3(torch.cat([d3, e3], 1))
        d2 = self.up2(d3)
        d2 = self.dec2(torch.cat([d2, e2], 1))
        d1 = self.up1(d2)
        d1 = self.dec1(torch.cat([d1, e1], 1))
        return self.head(d1)


class DiceCELoss(nn.Module):
    """
    CE (with per-class weights, for the class imbalance of land cover) + soft Dice.

    `class_weights` come from the training-set class histogram; roads and water are a
    fraction of a percent of pixels, and unweighted CE lets the model ignore them.
    """

    def __init__(
        self,
        num_classes: int,
        ignore_index: int = 255,
        dice_weight: float = 0.5,
        class_weights: torch.Tensor | None = None,
    ):
        super().__init__()
        self.ce = nn.CrossEntropyLoss(ignore_index=ignore_index, weight=class_weights)
        self.num_classes = num_classes
        self.ignore_index = ignore_index
        self.dice_weight = dice_weight

    def forward(self, logits: torch.Tensor, target: torch.Tensor) -> torch.Tensor:
        ce = self.ce(logits, target)
        if self.dice_weight <= 0:
            return ce

        valid = target != self.ignore_index
        if not valid.any():
            return ce

        probs = torch.softmax(logits, dim=1)
        # one-hot on valid pixels only
        t = target.clone()
        t[~valid] = 0
        one_hot = torch.nn.functional.one_hot(t, self.num_classes).permute(0, 3, 1, 2).float()
        mask = valid.unsqueeze(1).float()
        probs = probs * mask
        one_hot = one_hot * mask
        dims = (0, 2, 3)
        inter = (probs * one_hot).sum(dims)
        denom = probs.sum(dims) + one_hot.sum(dims)
        dice = (2 * inter + 1e-6) / (denom + 1e-6)
        # skip ignore-only empty classes averaging
        dice_loss = 1.0 - dice.mean()
        return (1 - self.dice_weight) * ce + self.dice_weight * dice_loss
