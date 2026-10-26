"""
Slice large orthophotos into train tiles and/or convert color masks to class-id masks.

Big GeoTIFFs (tiled, internal-overview COGs, multi-GB mosaics) are read through rasterio,
which bundles GDAL. PIL is kept as a fallback so the simple PNG workflow still works with
no extra dependencies.

Examples:
  # cut a small image into 512 tiles (PIL, whole image in memory)
  python prepare_tiles.py slice --image D:\\ortho\\block.png --out data\\images --size 512 --overlap 64

  # stream tiles out of a huge GeoTIFF without loading it (rasterio/GDAL)
  python prepare_tiles.py slice-raster --image D:\\ortho\\big.tif --out data\\images --size 512
  python prepare_tiles.py slice-raster --image big.tif --out data\\images --skip-nodata --max-tiles 200

  # convert color mask (same grid as image tiles or full mask) to id mask
  python prepare_tiles.py color2id --mask paint\\tile_0001.png --out data\\masks\\tile_0001.png

  # slice image + color mask together
  python prepare_tiles.py slice-pair --image img.png --mask color_mask.png --out data --size 512
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parent


def _try_rasterio():
    try:
        import rasterio  # noqa: F401

        return rasterio
    except ImportError:
        return None


def load_palette(cfg_path: Path) -> list[tuple[int, tuple[int, int, int]]]:
    """Return list of (class_id, (B,G,R)) from classes.json; match nearest color."""
    with open(cfg_path, "r", encoding="utf-8") as f:
        cfg = json.load(f)
    out = []
    for c in cfg["classes"]:
        bgr = tuple(c["color_bgr"])
        out.append((int(c["id"]), bgr))
    return out


def color_mask_to_id(color_img: Image.Image, palette: list[tuple[int, tuple[int, int, int]]]) -> Image.Image:
    """Nearest-color decode BGR/RGB paint mask -> class id L mask.

    Palette in classes.json is BGR (OpenCV). PIL loads RGB — we convert.
    """
    rgb = np.asarray(color_img.convert("RGB"), dtype=np.int16)
    bgr = rgb[:, :, ::-1]
    h, w, _ = bgr.shape
    out = np.zeros((h, w), dtype=np.uint8)
    best_dist = np.full((h, w), np.iinfo(np.int32).max, dtype=np.int32)

    for cls_id, (pb, pg, pr) in palette:
        dist = (
            (bgr[:, :, 0].astype(np.int32) - pb) ** 2
            + (bgr[:, :, 1].astype(np.int32) - pg) ** 2
            + (bgr[:, :, 2].astype(np.int32) - pr) ** 2
        )
        closer = dist < best_dist
        out[closer] = cls_id
        best_dist[closer] = dist[closer]
    return Image.fromarray(out, mode="L")


def slice_image(img: Image.Image, size: int, overlap: int):
    w, h = img.size
    stride = max(1, size - overlap)
    tiles = []
    ys = list(range(0, max(1, h - size + 1), stride))
    xs = list(range(0, max(1, w - size + 1), stride))
    if h > size and (not ys or ys[-1] != h - size):
        ys.append(h - size)
    if w > size and (not xs or xs[-1] != w - size):
        xs.append(w - size)
    if h <= size and w <= size:
        return [(0, 0, img)]

    for y in ys:
        for x in xs:
            box = (x, y, min(x + size, w), min(y + size, h))
            tile = img.crop(box)
            # pad if needed
            if tile.size != (size, size):
                canvas = Image.new(img.mode, (size, size), 0)
                canvas.paste(tile, (0, 0))
                tile = canvas
            tiles.append((x, y, tile))
    return tiles


def tile_starts(length: int, size: int, overlap: int) -> list[int]:
    stride = max(1, size - overlap)
    if length <= size:
        return [0]
    starts = list(range(0, length - size, stride))
    last = length - size
    if not starts or starts[-1] != last:
        starts.append(last)
    return starts


def cmd_slice(args):
    img = Image.open(args.image).convert("RGB")
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    stem = Path(args.image).stem
    tiles = slice_image(img, args.size, args.overlap)
    for x, y, tile in tiles:
        name = f"{stem}_x{x}_y{y}.png"
        tile.save(out / name)
        print("wrote", out / name)


def cmd_slice_raster(args):
    """Stream tiles out of any GDAL-readable raster, one window at a time."""
    rasterio = _try_rasterio()
    if rasterio is None:
        raise SystemExit(
            "需要 rasterio 才能流式切分大栅格:  python -m pip install rasterio\n"
            "（小图可直接用 slice / slice-pair 子命令，走 PIL）"
        )

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    stem = Path(args.image).stem

    with rasterio.open(args.image) as src:
        print(f"driver={src.driver} size={src.width}x{src.height} bands={src.count} crs={src.crs}")

        ys = tile_starts(src.height, args.size, args.overlap)
        xs = tile_starts(src.width, args.size, args.overlap)
        print(f"tiles: {len(xs)} x {len(ys)} = {len(xs) * len(ys)}")

        written = 0
        for y in ys:
            for x in xs:
                w = min(args.size, src.width - x)
                h = min(args.size, src.height - y)

                window = rasterio.windows.Window(x, y, w, h)
                data = src.read(indexes=args.bands, window=window, boundless=True, fill_value=0)

                if args.skip_nodata and not _has_content(data, src.nodata):
                    continue

                # Blank margins are common in mosaics and carry no signal for training.
                if args.skip_blank and _is_blank(data):
                    continue

                # (bands, h, w) -> (h, w, bands)
                arr = np.transpose(data, (1, 2, 0))
                if arr.shape[2] == 1:
                    arr = np.repeat(arr, 3, axis=2)
                elif arr.shape[2] >= 3:
                    arr = arr[:, :, :3]

                arr = _to_uint8(arr, src.dtypes[0])

                if (h, w) != (args.size, args.size):
                    canvas = np.zeros((args.size, args.size, 3), dtype=np.uint8)
                    canvas[:h, :w] = arr
                    arr = canvas

                name = f"{stem}_x{x}_y{y}.png"
                Image.fromarray(arr, mode="RGB").save(out / name)
                written += 1

                if args.max_tiles and written >= args.max_tiles:
                    print(f"达到 --max-tiles {args.max_tiles}，停止")
                    print(f"共写出 {written} 个瓦片 -> {out}")
                    return

    print(f"共写出 {written} 个瓦片 -> {out}")


def _is_blank(data: np.ndarray) -> bool:
    """True for a window with essentially no detail (uniform padding or all-white margin)."""
    return bool(np.ptp(data) < 4)


def _has_content(data: np.ndarray, nodata) -> bool:
    if nodata is not None:
        return bool(np.any(data != nodata))
    return bool(np.any(data))


def _to_uint8(arr: np.ndarray, dtype: str) -> np.ndarray:
    """GDALRasterIO-equivalent: scale non-byte imagery into 0-255 before writing PNG."""
    if dtype == "uint8":
        return arr.astype(np.uint8)

    finite = arr[np.isfinite(arr)]
    if finite.size == 0:
        return np.zeros_like(arr, dtype=np.uint8)

    lo, hi = float(finite.min()), float(finite.max())
    if hi <= lo:
        return np.zeros_like(arr, dtype=np.uint8)
    return ((arr.astype(np.float32) - lo) * (255.0 / (hi - lo))).clip(0, 255).astype(np.uint8)


def cmd_slice_pair(args):
    img = Image.open(args.image).convert("RGB")
    mask = Image.open(args.mask)
    if mask.mode != "L":
        # treat as color
        palette = load_palette(args.classes)
        mask = color_mask_to_id(mask, palette)

    out = Path(args.out)
    (out / "images").mkdir(parents=True, exist_ok=True)
    (out / "masks").mkdir(parents=True, exist_ok=True)
    stem = Path(args.image).stem

    # ensure same size
    if mask.size != img.size:
        mask = mask.resize(img.size, Image.NEAREST)

    img_tiles = slice_image(img, args.size, args.overlap)
    mask_tiles = slice_image(mask, args.size, args.overlap)
    for (x, y, it), (_, _, mt) in zip(img_tiles, mask_tiles):
        name = f"{stem}_x{x}_y{y}.png"
        it.save(out / "images" / name)
        mt.save(out / "masks" / name)
        print("wrote", name)


def cmd_color2id(args):
    palette = load_palette(args.classes)
    mask = Image.open(args.mask)
    idm = color_mask_to_id(mask, palette)
    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    idm.save(out)
    arr = np.asarray(idm)
    uniq, cnt = np.unique(arr, return_counts=True)
    print("wrote", out)
    print("class histogram:", dict(zip(uniq.tolist(), cnt.tolist())))


def main():
    p = argparse.ArgumentParser(description="Prepare training tiles")
    p.add_argument("--classes", type=Path, default=ROOT / "classes.json")
    sub = p.add_subparsers(dest="cmd", required=True)

    s1 = sub.add_parser("slice", help="slice one orthophoto into image tiles (PIL, whole image in RAM)")
    s1.add_argument("--image", required=True)
    s1.add_argument("--out", default=str(ROOT / "data" / "images"))
    s1.add_argument("--size", type=int, default=512)
    s1.add_argument("--overlap", type=int, default=0)
    s1.set_defaults(func=cmd_slice)

    s2 = sub.add_parser("slice-pair", help="slice image + mask pair")
    s2.add_argument("--image", required=True)
    s2.add_argument("--mask", required=True, help="id-mask or color-mask")
    s2.add_argument("--out", default=str(ROOT / "data"))
    s2.add_argument("--size", type=int, default=512)
    s2.add_argument("--overlap", type=int, default=0)
    s2.set_defaults(func=cmd_slice_pair)

    s3 = sub.add_parser("color2id", help="color paint mask -> class-id mask")
    s3.add_argument("--mask", required=True)
    s3.add_argument("--out", required=True)
    s3.set_defaults(func=cmd_color2id)

    s4 = sub.add_parser("slice-raster", help="stream tiles from a big GeoTIFF via rasterio/GDAL")
    s4.add_argument("--image", required=True)
    s4.add_argument("--out", default=str(ROOT / "data" / "images"))
    s4.add_argument("--size", type=int, default=512)
    s4.add_argument("--overlap", type=int, default=0)
    s4.add_argument("--bands", type=int, nargs="+", default=[1, 2, 3],
                    help="1-based band indexes to read (default 1 2 3)")
    s4.add_argument("--skip-nodata", action="store_true", help="skip windows that are entirely NoData")
    s4.add_argument("--skip-blank", action="store_true",
                    help="skip windows with no detail (uniform white/black margins)")
    s4.add_argument("--max-tiles", type=int, default=0, help="stop after N tiles (0 = all)")
    s4.set_defaults(func=cmd_slice_raster)

    args = p.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
