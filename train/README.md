# 训练与微调

用你自己的正射 + 标注，训一版含**农田**的轻量分割模型，导出 ONNX 后接 C#。

最快路径是从标注界面里点按钮（见根目录 `README.md` 第 2 节）。这一份是命令行 / 背景说明。

## 类别（classes.json，与 C# 对齐）

| ID | 键名 | 中文 | 颜色 BGR |
|----|------|------|-----------|
| 0 | background | 背景 | (0,0,0) |
| 1 | building | 建筑 | (0,0,222) |
| 2 | road | 道路 | (255,255,255) |
| 3 | farmland | 农田 | (73,181,75) |
| 4 | woodland | 林地 | (38,97,34) |
| 5 | water | 水体 | (255,69,0) |
| 6 | bareland | 裸地 | (128,128,0) |
| 255 | — | 忽略（`ignore_index`，不参与 loss） | — |

`classes.json` 是唯一真源：C# 调色板、标注界面、本目录的脚本都读它。
掩码是**单通道 PNG，像素值 = 类别 ID**，不是彩色图；彩色描边用 `color2id` 转。

## 环境

```powershell
cd D:\EVproject\LightViewDemo
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r train\requirements.txt

# 想要小得多的下载体积（本项目 CPU 推理用不着 CUDA）：
pip install torch torchvision --index-url https://download.pytorch.org/whl/cpu
```

`torch >= 2.9` 的 `torch.onnx.export` 走 dynamo 导出器，**需要 `onnxscript`**，否则导出报
`ModuleNotFoundError: No module named 'onnxscript'`。`requirements.txt` 已包含。

### 交互标注的 SAM 后端（可选）

`train/sam_service.py` 是标注界面「交互标注」工具的后端：一个常驻进程持着 SAM，界面发提示、
收回掩码。它不是训练，但用同一个 `.venv`：

```powershell
pip install sam2 segment-anything
# 权重放到 models\sam\，文件名要和 HF 仓库里的一致
pip install huggingface_hub
python -c "from huggingface_hub import hf_hub_download; import shutil; shutil.copy(hf_hub_download('facebook/sam2.1-hiera-tiny','sam2.1_hiera_tiny.pt'), 'models/sam/sam2.1_hiera_tiny.pt')"
```

- CPU 上默认用 `sam2.1_hiera_tiny`（156 MB），有 CUDA 会自动改用 `sam2.1_hiera_base_plus`（324 MB）；
  都可以放，用 `LC_SAM_MODEL` 指定。
- 装不上 `sam2` 也没关系：界面会退回内置的经典算法（区域生长 + GrabCut），
  或者用 `LC_SAM=off` 显式关掉。
- 起服务、看延迟、验证缓存：`dotnet run --project src\LandCoverLabeler -c Release -- --samcheck`

## 模型选择

`--arch` × `--encoder` 决定网络。默认 `deeplabv3plus + mit_b0`（SegFormer-B0 编码器，
约 4.1 M 参数）：ImageNet 预训练迁移好，边界（道路/建筑）比 MobileNet 类编码器干净，
CPU 与 ONNX 都能跑。

> ⚠️ **编码器名必须是 `segmentation_models_pytorch` 真正有的那些。**
> 本仓库旧版默认的 `mobilenet_v3_large` **不存在**于 SMP，训练会在建图阶段直接报
> `KeyError: Wrong encoder name`。`model.py` 现在会提前校验并列出推荐项。

CPU 上值得用的编码器（大致由小到大）：

| encoder | 参数量 | 说明 |
|---------|--------|------|
| `mobileone_s1` | ~1.5 M | 最快，精度一般 |
| `mobilenet_v2` | ~2.2 M | 稳妥的轻量选择 |
| **`mit_b0`** | ~3.7 M | **默认**，同等体积下精度最好 |
| `efficientnet-b0` | ~5.3 M | 均衡 |
| `mobileone_s4` | ~5.6 M | 快且比 s1 准 |
| `mit_b1` | ~13.7 M | 更准，CPU 慢一倍左右 |
| `resnet34` | ~24 M | 稳但慢 |
| `efficientnet-b3` | ~12 M | 均衡偏重 |

`--arch` 可选：`deeplabv3plus`（默认）、`unet`、`fpn`、`manet`、`linknet`、`segformer`。

想直接换更大的模型、不训练也行——任何「输入 `[1,3,H,W]` RGB/255 → 输出 `[1,C,H,W]` logits」
的 ONNX 都能喂给 `LandCoverDemo`，加 `--model` 和匹配的 `--tile` 即可。
但**类别 ID 必须和 `LandCoverPalette.cs` 对齐**，否则语义会错位。

## 数据准备

### 从标注界面导出（推荐）

标注界面里点「导出瓦片」，会按 `train/data/` 结构写出：

```
data/images/<影像名>_x{像素X}_y{像素Y}.png
data/masks/<影像名>_x{像素X}_y{像素Y}.png
data/val_images/...   # 自动按瓦片切出验证集
```

### 命令行切分

```powershell
# 小图（PIL，整图进内存）
python prepare_tiles.py slice-pair --image a.png --mask a_mask.png --out data --size 512

# 大 GeoTIFF（rasterio/GDAL 流式，不整图加载）
python prepare_tiles.py slice-raster --image big.tif --out data\images --size 512 `
    --skip-blank --max-tiles 200

# 彩色描边 → ID 掩码
python prepare_tiles.py color2id --mask paint\tile.png --out data\masks\tile.png
```

### 用别的软件标注

EISeg / LabelMe / CVAT / QGIS 都可以，导出灰度 ID 掩码，或彩色图再 `color2id`。

### 数量建议

| 阶段 | 瓦片数（512²） | 说明 |
|------|----------------|------|
| 能跑通 | 20–40 | 覆盖主要地貌 |
| 可用 | 80–200 | 含不同季节/光照更好 |
| 验证 | 另留 10–20% | 或放 `data/val_images` |

**每类都要出现**，尤其是农田、道路、建筑。

## 训练

```powershell
python train.py --arch deeplabv3plus --encoder mit_b0 --epochs 60 --batch 4
```

常用参数：

| 参数 | 默认 | 说明 |
|------|------|------|
| `--arch` / `--encoder` | `deeplabv3plus` / `mit_b0` | 见上表 |
| `--epochs` / `--batch` / `--lr` | 60 / 4 / 6e-4 | CPU 内存紧张就 `--batch 2` |
| `--freeze-encoder-epochs` | 5 | 前 N 轮只训解码器。**标注少时显著更稳** |
| `--no-class-weights` | 关 | 默认按类别频率反比加权 CE，缓解道路/水体稀少 |
| `--val-ratio` | 0.15 | 无 `--val-images` 时按瓦片随机划分验证集 |
| `--no-pretrained` | 关 | 不用 ImageNet 权重（离线环境） |
| `--dice-weight` | 0.5 | Dice 与 CE 的配比 |

输出：`train/runs/best.pt`（按验证 mIoU 保存）+ `train/runs/history.json`（每轮指标 + 每类 IoU）。

两点值得知道的设计：

- **验证集不会看到增强图**。旧的 `random_split` 会把同一张瓦片的翻转版本分到验证集，
  指标虚高；现在按索引把「有增强的训练副本」和「无增强的验证副本」分开取。
- **类别加权默认开**。山区影像里道路/水体常不足 1%，不加权时 CE 会让模型直接放弃它们。

## 导出 ONNX

```powershell
python export_onnx.py --ckpt runs\best.pt --out ..\models\landcover_mountain_7c.onnx --size 512
```

## C# 使用

```powershell
dotnet run --project src\LandCoverDemo -c Release -- "你的正射.tif" `
  --model models\landcover_mountain_7c.onnx --tile 512 --overlap 64
```

`--tile` 要和训练 `--size` 一致。类别数会从 ONNX 输出维度自动读取。

## 标注质量比模型更重要

1. **农田必须标**，否则会被判成水体/背景
2. 道路：铺装 + 主要土路标清，过细碎路可忽略
3. 建筑：屋顶为主，院子可不标
4. 树冠阴影：按实际地物标（树下还是林地）
5. **拿不准就别标**——留成忽略(255)比标错强

## 常见问题

| 问题 | 处理 |
|------|------|
| `KeyError: Wrong encoder name` | 编码器名不存在于 SMP，改用上表里的名字 |
| `No module named 'onnxscript'` | `pip install onnxscript`（导出 ONNX 需要） |
| `UnicodeEncodeError: gbk codec` | 直接跑脚本时设 `set PYTHONIOENCODING=utf-8`；界面里已自动设置 |
| mIoU 很低 | 检查掩码是否真是 0–6、是否和图对齐；确认瓦片里确实有标注 |
| 农田仍漏 | 增加农田样本，或调低 `--dice-weight` 之外的类别权重上限（`model.py` 的 `clamp`） |
| 过拟合 | 增加数据/增强，换更小 encoder，或加大 `--freeze-encoder-epochs` |
| 导出后 C# 类别乱 | 确认 `--tile` 与训练 `--size` 一致，且类别顺序与 `classes.json` 相同 |

## 目录

```
train/
  classes.json       # 唯一类别真源
  dataset.py         # 瓦片数据集 + 增强
  model.py           # 网络构建（arch × encoder）+ Dice/CE 损失
  train.py           # 训练主循环
  export_onnx.py     # 导出动态 H/W 的 ONNX
  prepare_tiles.py   # 切片 / 彩色掩码转 ID / 大栅格流式切片
  requirements.txt
  data/{images,masks,val_images,val_masks}/
  runs/best.pt
```
