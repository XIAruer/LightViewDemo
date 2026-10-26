# LightViewDemo — 轻量化正射地物分类（C# 本地推理 + 标注/训练闭环）

本地 CPU 即可运行。**仓库不携带影像数据和模型权重**，两样都需要你自己准备（模型可以一键下载，见第 9 节）。

| 目录 | 作用 |
|------|------|
| `src/LandCover.Core/` | 公共库：GDAL 读写、ONNX 滑窗推理、后处理、矢量化、交互式分割 |
| `src/LandCoverDemo/` | 命令行推理：栅格 → 掩码 → GeoJSON / KML / SHP / GPKG |
| `src/LandCoverLabeler/` | **标注 + 训练界面**：在 TIF 上直接涂抹地物，一键导出瓦片并训练 |
| `src/LandCoverInfer/` | **识别界面**：选影像、选模型、调参数、出结果，不用敲命令 |
| `src/LandCoverShell/` | **主菜单**：一个窗口里切换上面两个界面（见第 3 节） |
| `train/` | Python 训练侧（PyTorch + segmentation_models_pytorch） |

## 1. GDAL 已内置（tif / shp 不再是麻烦）

C# 侧通过 NuGet 打包了原生 GDAL，**不需要装 OSGeo4W、不用配 PATH**：

```xml
<PackageReference Include="MaxRev.Gdal.Core" Version="3.13.3.557" />
<PackageReference Include="MaxRev.Gdal.WindowsRuntime.Minimal" Version="3.13.3.557" />
```

带来的实际变化：

- **能打开以前打不开的 tif**。分块 / 多页 / 带金字塔的 GeoTIFF、COG、多 GB 镶嵌图都能读；
  以前 OpenCV 读不了多页 GeoTIFF，只能先手工转 PNG，现在直接就能跑。
- **不再需要「同名 PNG」兜底**，`LoadImageRobust` 只在 GDAL 不可用时才走。
- **分块流式读取**。推理时按 tile 从磁盘窗口读，不再把整幅影像读进内存：
  几百 MP 的影像以前要整幅常驻（而且根本读不出来），现在峰值只有类别掩码本身（每像素 1 字节）。
- **CRS 交给 PROJ**。任意投影（UTM、高斯克吕格、Web Mercator、经纬度）都能正反算。
  例如一份 EPSG:3857 的在线地图影像，旧的 TM 手算代码认不出来，KML 只能写空文档；
  现在会正确反算成 WGS84。手写的 `GeoTiffGeoref.TryRead` 仍保留，作为 GDAL 不可用时的兜底。
- **Shapefile / GeoPackage 直接出**，不用再绕 QGIS。中文属性用 UTF-8 写入 `.cpg`。

Python 侧用 `rasterio`（自带 GDAL，`pip` 即可装），见 `train/prepare_tiles.py` 的 `slice-raster`。

> 注：读取统计信息时 GDAL 默认会在影像旁写 `.aux.xml`，本项目已设 `GDAL_PAM_ENABLED=NO` 关掉，
> 不会往你的数据目录里塞东西。建立金字塔会显式询问，写的是标准 `.ovr`，不改原文件。

## 2. 标注 + 训练界面（LandCoverLabeler）

```powershell
dotnet run --project src\LandCoverLabeler -c Release
# 或直接带影像打开
dotnet run --project src\LandCoverLabeler -c Release -- "D:\ortho\big.tif"
```

工作流：

1. **打开影像**（任意 GDAL 能读的栅格，几百 MP 也没问题）→ 自动建工程 `annotations/<影像名>/`
2. **在 TIF 上涂抹**：0-9 选类别，`B` 画笔 / `R` 矩形 / `P` 多边形 / `F` 填充 / `I` 吸管，`E` 橡皮
3. **交互标注**（`G`）：点一下或拖个框就把目标选出来，写成当前类别，见下方「半自动地物识别」
4. 滚轮缩放、中键（或空格+左键）平移；大图会主动问你要不要先建金字塔（强烈建议建）
5. **放大镜**：画面角落常驻一个 200×200 的放大窗（自动躲开鼠标那一侧），**画布本身不会自动缩放**，
   所以你可以保持舒服的视角，靠放大镜看清细节再落笔。工具栏「放大镜」按钮可开关。
6. **用模型预标注**：拿现有 ONNX 跑一遍当前视野，结果只填进**你没涂过**的像素，再手工修
7. **导出瓦片**：从原图裁出 512×512 的 image/mask 对到 `train/data/`
8. **开始训练**：调 `train/train.py`，输出实时显示在下方日志区，同时完整写入 `train/runs/train.log`
   （菜单「训练 → 打开训练日志」直接用系统默认程序打开）
9. **导出 ONNX** → `models/landcover_custom.onnx`，回到第 6 步迭代
10. **和 GIS 互导**：工具栏「导出 shp」一键出图；也可以去 QGIS/ArcGIS 精修后导回来接着训练（见下方「GIS 往返」）

### 半自动地物识别：交互标注 → shp

**打开影像 → 点几个目标 → 直接拿到 shp**，不需要训练，也不需要涂满整幅图。
适合「只想把一片建筑/水塘/田块勾出来给别人用」这类任务。

**完整流程（9 步）**

1. **打开影像**（文件 → 打开影像）。会自动在 `annotations/<影像名>/` 建工程，标注落在里面——
   你不用管它，但它保证了撤销、续做和导出。
2. **选类别**：点左侧调色板，或按 `0`-`6`（`E` 是橡皮 = 忽略）。**交互标注的结果会写成当前类别**，
   所以先选好类别再点目标。要标另一类就先换类别。
3. **进入交互标注**：按 `G`，或点工具栏「交互标注」。工具栏上**当前工具会高亮**，
   状态栏会写明这一档的操作。
4. **给提示**，三种可以混用：
   - **左键 = 正点**：「这里就是该类别」
   - **右键 = 负点**：「这里不是」
   - **左键拖拽 = 框住整个目标**（黄框）——比多点几下更管用，见下
5. **看预览**。对了按 **`回车`** 写入标注；不对按 **`Z`** 撤销上一个提示（点或框）继续调，
   或 `Esc` 全清重来。
6. **重复 4-5**，把这一类的目标都点完。交互标注模式**会一直保持**，方便连着标注多个目标。
7. **退出交互标注**，三个等价的办法（这是最容易卡住的一步，记住任一个就行）：
   - 再点一次工具栏 **「交互标注」**（它是个开关）
   - 按 `B` / `R` / `P` / `F` / `I` 切到任何别的工具（会回到你进入前的那个工具）
   - 按 `Esc`——**当没有待处理的提示时**；有提示时 `Esc` 是清空提示，再按一次才退出
8. **保存**：`Ctrl+S` 或工具栏「保存标注」。写入 `annotations/<影像名>/labels/` 下的瓦片 PNG。
   *只为了出 shp 的话这步可以跳过*——导出会自动帮你保存一次。
9. **导出 shp**：点工具栏 **「导出 shp」**（或 文件 → 导出标注矢量，默认就是 `.shp`）。
   得到一个真实地理坐标、带 `class_id` / `class_name` / `area_px` / `area_m2` 的多边形 shp。

> **一句话版**：选类别 → `G` → 左键点 / 拖框 → `回车` → 重复 → 再点「交互标注」退出 → 工具栏「导出 shp」。

**为什么建议拖一个框**：框（黄框）告诉模型「我要的是这个目标」，比多点几下更能钉住边界；
程序会在框外再扩一圈（橙框）作为工作区，免得目标贴着边导致分不完整。**框要框住整个目标**，
框外扩的部分只是给它留点背景做参照。不拖框时，工作区就是工具栏「交互范围」的方框。

**后端是 SAM，默认就用它**：软件启动时会在后台加载一个 SAM 服务（首次点击一个区域约 2 秒
做图像编码，之后同一区域的每次调整约 80 毫秒）。分不出来时加**负点**最有效。
装不上 SAM 时（没有 `.venv`、没装 `sam2`、没有权重）会自动退回经典算法，功能不变。
想强制关掉：环境变量 `LC_SAM=off`。

**结果会自动做一遍清洗**，针对模型掩码常见的三种毛病：内部零星空洞（全部补上）、
零星噪点/毛刺（3×3 多数滤波）、边缘弥散（用影像做引导滤波把边界吸到真实边缘上，只在
影像上真有边的地方动，平地上的细长目标不会被吃掉）。你点的正点/负点是硬约束，
清洗动不了它们。

<details>
<summary>退回的经典算法 + 两个参数（交互范围 / 相似度）</summary>

经典算法是「颜色相似区域生长 + GrabCut 修边界」，不需要模型、不需要 GPU：生长负责铺开同类地物，
GrabCut 用正/负点当硬约束把边界收紧到真实边缘上（浅色房顶、成片农田、水面效果最好；
林地、树影这类纹理破碎的地物更适合用画笔）。一次点击约 1–2 秒，跑在后台线程，界面不会卡。

| 参数 | 作用 |
|------|------|
| 交互范围 | 工作区边长（栅格像素）。越大地物越完整，但边界越粗 |
| 相似度 | 颜色容差。分不出来就调大，漫出来了就调小（**只对经典算法有效**，用 SAM 时不起作用） |

</details>

### GIS 往返（导出 / 导入标注矢量）

标注做了一部分之后，往往想在 QGIS / ArcGIS 里用现成的编辑工具精修（合并、切分、对齐地界），
或者把成果直接给别的软件用。工具栏的 **「导出 shp」** 是最快的一条（等价于下面第一条、默认格式就是 shp），「文件」菜单里有两个完整入口：

| 菜单项 | 作用 |
|--------|------|
| 导出标注矢量（shp/gpkg/geojson）… | 把**全部已标注区域**写成多边形，带 `class_id`/`class_name`/`area_px`/`area_m2` 属性 |
| 导入标注矢量… | 把（改过的）矢量读回来，变成标注继续训练 |

几个要点：

- 导出的是真实地理坐标（跟影像同一套 CRS，`.prj` 一起写出），不是像素坐标；
  导入时如果矢量换了坐标系（比如你在 QGIS 里重投影过），会自动换算回影像坐标系。
- 类别跟着 `class_id` 字段走。**多边形属性写 255 表示「忽略」**——想从训练集里挖掉一块，
  在 GIS 里给它填 255 就行。没有类别字段时会按当前选中的类别整体导入。
- 导入时会问一句：**先清空全部标注再导入**（适合"我在 GIS 里改的就是完整成果"），
  还是**只覆盖多边形范围**（适合增量补充）。清空那一步不可撤销，导入本身是一步 `Ctrl+Z`。
- 导出的窗口是「已标注瓦片」的连通区域的外接矩形（各连通片分别取景），
  所以即便你只在几百 MP 影像的两个角落标了几笔，也不会为了导出去开一整幅图的缓冲。
- 反过来，不带矢量的路径是「导出全幅标注 GeoTIFF」——那个更适合拿去当栅格成果用。

**关键设计：没涂过的像素是「忽略(255)」，不参与训练。**所以你不需要涂满整幅影像，只标确定的地方即可；一个瓦片里标了 2% 以上才会被导出。
这也避免了「未标注区域被当成背景」这个常见陷阱。

标注只按需分配 512×512 瓦片内存，并以 `labels/x{tx}_y{ty}.png` 落盘，
在几百 MP 影像角落标一小块的代价是几百 KB，而不是一整张稠密标注面。

无界面自检（不需要点鼠标，需要给一张影像）：

```powershell
dotnet run --project src\LandCoverLabeler -c Release -- --selftest "D:\ortho\image.tif"
```

## 3. 主菜单（LandCoverShell）—— 一个窗口切换识别与标注

```powershell
dotnet run --project src\LandCoverShell -c Release
# 直接打开影像，并指定先进哪个界面（infer / labeler）
dotnet run --project src\LandCoverShell -c Release -- --view labeler "D:\ortho\big.tif"
```

「识别」和「标注与训练」本来是同一个工具的两半，来回跑两个程序很别扭。主菜单把它们放进
同一个窗口：菜单栏的 **视图 → 识别界面 / 标注与训练界面**（或 `Ctrl+1` / `Ctrl+2`）随时切换。

两个界面都只创建一次并常驻，**切换不会丢状态**——已经打开的影像、标注进度、日志都原样保留。
切换时当前影像会跟着带过去，所以「先标注几笔 → 切过去识别看看」是一条顺手的路。

两个界面各自的菜单（文件/标注/训练/帮助）依然完整可用：主菜单只是给它们加了个框，
`src\LandCoverInfer` 和 `src\LandCoverLabeler` **仍然可以单独运行**，行为不变。
单独运行时点「视图」里的切换项，程序会去找 `LandCoverShell.exe` 帮你把另一个界面拉起来
（没构建过就提示你用 `dotnet run` 先构建）。

## 4. 识别界面（LandCoverInfer）—— 不想敲命令就用它

```powershell
dotnet run --project src\LandCoverInfer -c Release
# 或直接带影像打开
dotnet run --project src\LandCoverInfer -c Release -- "D:\ortho\big.tif"
```

界面分三块：

- **顶部**：输入影像 / 模型 / 输出前缀，各带“浏览…”按钮；**影像可以直接拖进窗口**
- **左侧**：切片边长、重叠、线程、后处理开关与参数、矢量参数、导出格式、是否先建金字塔
- **右侧**：`日志` 标签实时滚动识别过程，`结果` 标签显示分类占比表和彩色预览

选好模型后日志里会打印这个模型的类别表、`class_map` 映射和预处理方式，一眼能看出它跟本项目
7 类调色板对不对得上。跑完可以点“打开输出目录”，或用 `结果` 标签里的文件列表定位产物。

它和命令行用的是**同一个** `InferencePipeline`，所以两边结果必然一致——命令行适合批处理和脚本，
界面适合临时看一张图。

## 5. 命令行推理

```powershell
# 先下载一个模型（首次使用必做）
dotnet run --project src\LandCoverDemo -c Release -- --download-model oem

# 跑一张影像
dotnet run --project src\LandCoverDemo -c Release -- "D:\ortho\image.tif" --tile 512 --overlap 64 --threads 8

# 额外导出 Shapefile 和 GeoPackage
dotnet run --project src\LandCoverDemo -c Release -- "ortho.tif" --shp --gpkg

# 大图先建金字塔，之后缩放/预览都快
dotnet run --project src\LandCoverDemo -c Release -- "big.tif" --build-overviews
```

参数：

| 参数 | 说明 |
|------|------|
| 第一个位置参数 | 输入影像（tif/png/jpg，任意 GDAL 格式） |
| `--model` | ONNX 路径，默认 `models/oem_sparsemask_8c.onnx`（首次用 `--download-model oem` 下载） |
| `--download-model` | 下载模型：`oem` / `sam-tiny` / `sam-base-plus` / `all`，不带影像时只下载 |
| `--tile` / `--overlap` | 滑窗边长 / 重叠，默认 512 / 64（`0 <= overlap < tile`） |
| `--threads` | 并行 tile 数，默认 `CPU核数/2` |
| `--preview` / `--no-preview` | 是否出全图彩色/叠加（>12MP 默认关） |
| `--no-stream` | 关掉分块流式读取，改为整图读进内存 |
| `--build-overviews` | 给没有金字塔的影像建 `.ovr` |
| `--shp` / `--gpkg` | 额外导出 Shapefile / GeoPackage |
| `--out` | 输出前缀，默认 `output/<输入文件名>` |
| `--georef` | 显式指定做坐标参考的 GeoTIFF |
| `--min-area` / `--simplify` | 矢量最小面积（px）/ 简化容差（px），默认 64 / 1.5 |
| `--no-post` / `--morph` / `--post-min-area` | 后处理开关 / 形态学核 / 碎斑阈值 |
| `--no-center-blend` | 关闭中心加权融合，回到后写覆盖 |

## 6. 类别（7 类山区方案）

单一定义源：`train/classes.json`。三处必须逐 ID 对齐——`src/LandCover.Core/LandCoverPalette.cs`、
`train/classes.json`、标注界面（直接读 `classes.json`，不再手抄一份）。

| ID | 键名 | 名称 | 颜色 BGR |
|----|------|------|-----------|
| 0 | background | 背景 | (0,0,0) |
| 1 | building | 建筑 | (0,0,222) |
| 2 | road | 道路 | (255,255,255) |
| 3 | farmland | 农田 | (73,181,75) |
| 4 | woodland | 林地 | (38,97,34) |
| 5 | water | 水体 | (255,69,0) |
| 6 | bareland | 裸地 | (128,128,0) |
| 255 | — | 忽略（不参与训练） | — |

> 仓库不携带 ONNX 权重。推荐的 `models/oem_sparsemask_8c.onnx` 用
> `--download-model oem`（或界面里的 文件 → 下载模型…）获取，它带 sidecar，8 类会自动映射到本表。
>
> 如果你拿到的是类别数不等于 7、又没有 sidecar 的模型，程序会明确警告，而不是默默输出错位的 ID。

## 7. 输出

| 文件 | 说明 |
|------|------|
| `*_mask.png` | 类别 ID 掩码（始终生成） |
| `*_color.png` / `*_overlay.png` | 彩色掩码 / 叠加预览（小图或 `--preview`） |
| `*_stats.txt` | 各类像素占比 |
| `*.geojson` | 矢量面（原生投影坐标） |
| `*.kml` | KML（WGS84 lon/lat，经 PROJ 反算） |
| `*.shp` / `*.gpkg` | `--shp` / `--gpkg` 时生成 |

矢量属性：`class_id` / `class_name` / `class_key` / `area_px` / `area_m2`。

## 8. 依赖

- .NET 9 SDK
- `Microsoft.ML.OnnxRuntime`、`OpenCvSharp4`、`MaxRev.Gdal.Core` + `MaxRev.Gdal.WindowsRuntime.Minimal`
- 训练侧：见 `train/requirements.txt`（torch、segmentation-models-pytorch、rasterio、onnxscript）

## 9. 模型：仓库不带权重，先下载

### 获取模型

模型权重不进仓库（体积大、和代码版本无关）。命令行和界面走的同一份目录
（`ModelCatalog`），下载完成后校验字节数与 SHA-256 再落到 `models/`：

```powershell
# 命令行
dotnet run --project src\LandCoverDemo -c Release -- --download-model oem
dotnet run --project src\LandCoverDemo -c Release -- --download-model all   # 或 sam-tiny / sam-base-plus

# 界面：文件 → 下载模型…（识别界面和标注界面都有这一项）
```

| id | 落地文件 | 大小 | 许可 | 说明 |
|----|----------|------|------|------|
| `oem` | `models/oem_sparsemask_8c.onnx` | 11.9 MB | MIT | [geobase/sparsemask](https://huggingface.co/geobase/sparsemask)，OpenEarthMap 8 类，免训练 |
| `sam-tiny` | `models/sam/sam2.1_hiera_tiny.pt` | 156 MB | Apache-2.0 | 交互标注后端，CPU 默认 |
| `sam-base-plus` | `models/sam/sam2.1_hiera_base_plus.pt` | 324 MB | Apache-2.0 | 交互标注后端，有 GPU 时默认 |

`models/*.onnx.json` sidecar 是几十行的文本，随仓库提交，所以下载下来的模型行为和自己训练导出的一致。

### 为什么推荐 OEM

通用的 LandCover.ai 4 类模型（背景/建筑/林地/水/道路）**没有「农田」「裸地」类**，在梯田、
林地为主的山区影像上会把大片农田判成「背景」——它本质上是个「道路 + 树林」检测器。如果你手上
有这样的模型，可以直接跑（程序从 ONNX 元数据读类别数，不会崩），但要预期类别语义错位。

`oem_sparsemask_8c.onnx` 是 8 类 OpenEarthMap 方案，SparseMask NAS，约 2.9 M 参数，CPU 可跑。
在一幅山区正射影像上的分类占比对比：

| 类别 | LandCover.ai 4 类 | OEM + class_map |
|------|-------------------|-----------------|
| 背景 | 37.4% | 0% |
| 建筑 | 25.9% | 32.0% |
| 道路 | 29.3% | 1.5% |
| 农田 | 5.2% | 4.1% |
| 林地 | 2.1% | **41.7%** |
| 水体 | 0% | 6.1% |
| 裸地 | 0% | 14.6% |

「29% 道路、2% 林地」对这类地貌明显不合理；OEM 的分布才接近实际。但它也不是万能的：
道路会断续、梯田内部有椒盐噪声。**要贴合你自己的地类，仍然建议用第 2 节的标注界面微调一版。**

### 模型 sidecar（类别映射与预处理）

不同模型有自己的类别顺序、自己的归一化方式、甚至自己的输出分辨率。硬编码在 C# 里既脆弱又
不可维护，所以这些信息放在模型旁边的 `<模型>.onnx.json` 里：

```jsonc
{
  "name": "geobase/sparsemask (OpenEarthMap 8-class, MIT)",
  "input_size": 1024,
  "normalize": "mean_std",                  // 或 "raw"（默认，即 像素/255）
  "mean": [0.4325, 0.4483, 0.3879],
  "std":  [0.0195, 0.0169, 0.0179],
  "class_names": ["bareland", "rangeland", "developed space", "road",
                  "tree", "water", "agriculture land", "buildings"],
  "class_map": [6, 4, 1, 2, 4, 5, 3, 1]     // 模型输出下标 -> 本项目调色板 ID
}
```

有 sidecar 时，类别 ID 会自动映射到 `LandCoverPalette`；没有 sidecar 且类别数不等于 7，
程序会**明确警告**而不是默默输出错位的 ID。输出分辨率不等于输入分辨率（SparseMask 输出是
输入的 1/2）时会自动上采样。

### 关于归一化（一个容易踩的坑）

不要给原本按「像素/255」训练的模型加 ImageNet 归一化。实测在真实影像上，加 `mean/std` 会让它
**塌成 92–96% 背景**，而原始的 `像素/255` 才给出合理的类别分布。不同模型的归一化方式不同，
必须按 sidecar 走，别想当然。
