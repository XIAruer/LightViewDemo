using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using Size = OpenCvSharp.Size;

namespace LandCover.Core;

/// <summary>
/// Sliding-window ONNX segmentation with optional multi-threaded tile inference.
/// Memory: full-size classMap only (no extra RGB copies); tiles are 512² working set.
/// </summary>
public sealed class OnnxLandCoverSegmenter : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _tileSize;
    private readonly int _overlap;

    public int NumClasses { get; }
    public int TileSize => _tileSize;
    public int Overlap => _overlap;

    /// <summary>Optional sidecar describing preprocessing and class mapping; null when absent.</summary>
    public ModelMetadata? Metadata { get; private set; }

    /// <summary>True when output class ids are remapped onto this project's palette.</summary>
    public bool HasClassMap => Metadata?.ClassMap is { Length: > 0 };

    private readonly float[]? _mean;
    private readonly float[]? _std;

    public OnnxLandCoverSegmenter(string modelPath, int tileSize = 512, int overlap = 64, int intraOpThreads = 1)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"ONNX 模型不存在: {modelPath}", modelPath);

        if (overlap < 0 || overlap >= tileSize)
            throw new ArgumentException("overlap 必须满足 0 <= overlap < tileSize");

        // When tiles run in parallel, keep each session call single-threaded
        // so total CPU use ≈ tileThreads * intraOpThreads.
        var so = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, intraOpThreads),
        };

        _session = new InferenceSession(modelPath, so);
        Metadata = ModelMetadata.TryLoad(modelPath);
        if (Metadata is { UsesMeanStd: true })
        {
            _mean = [.. Metadata.Mean!.Select(v => (float)v)];
            _std = [.. Metadata.Std!.Select(v => (float)v)];
        }

        _inputName = _session.InputMetadata.Keys.First();
        _tileSize = tileSize;
        _overlap = overlap;

        var outputMeta = _session.OutputMetadata.Values.First();
        var dims = outputMeta.Dimensions;
        NumClasses = dims.Length switch
        {
            4 => dims[1] > 0 ? dims[1] : LandCoverPalette.ClassCount,
            3 => dims[0] > 0 ? dims[0] : LandCoverPalette.ClassCount,
            _ => LandCoverPalette.ClassCount,
        };

        Console.WriteLine($"[模型] input={_inputName}, tile={tileSize}, overlap={overlap}, classes={NumClasses}, intraOp={intraOpThreads}");
        if (Metadata is not null)
            Console.WriteLine($"[模型] {Metadata.Describe(NumClasses)}");
        if (HasClassMap && NumClasses != Metadata!.ClassMap!.Length)
            Console.Error.WriteLine($"[模型] 警告: class_map 长度 {Metadata.ClassMap.Length} 与模型输出通道数 {NumClasses} 不一致，映射会被忽略");
        else if (!HasClassMap && NumClasses != LandCoverPalette.ClassCount)
            Console.Error.WriteLine($"[模型] 警告: 模型输出 {NumClasses} 类，但调色板是 {LandCoverPalette.ClassCount} 类，"
                + "且没有 class_map —— 输出的类别 ID 会错位。请为模型补一个 <模型>.onnx.json。");
    }

    public Mat Segment(
        Mat bgrImage,
        int maxDegreeOfParallelism = 0,
        bool centerWeightedBlend = true,
        IProgress<(int done, int total)>? progress = null,
        CancellationToken ct = default)
    {
        if (bgrImage.Empty())
            throw new ArgumentException("输入影像为空");

        return SegmentCore(
            bgrImage.Cols, bgrImage.Rows,
            new MatTileSource(bgrImage),
            maxDegreeOfParallelism, centerWeightedBlend, progress, ct);
    }

    /// <summary>
    /// Sliding-window inference reading tiles straight off disk through GDAL.
    /// Only one tile per worker is ever materialised, so a scene far larger than RAM
    /// can be classified; peak managed memory is the class map alone (width x height bytes).
    /// The caller keeps ownership of <paramref name="source"/>.
    /// </summary>
    public Mat SegmentRaster(
        RasterSource source,
        int maxDegreeOfParallelism = 0,
        bool centerWeightedBlend = true,
        IProgress<(int done, int total)>? progress = null,
        CancellationToken ct = default)
    {
        return SegmentCore(
            source.Width, source.Height,
            new RasterTileSource(source),
            maxDegreeOfParallelism, centerWeightedBlend, progress, ct);
    }

    private Mat SegmentCore(
        int w,
        int h,
        ITileSource tileSource,
        int maxDegreeOfParallelism,
        bool centerWeightedBlend,
        IProgress<(int done, int total)>? progress,
        CancellationToken ct)
    {
        int stride = Math.Max(1, _tileSize - _overlap);
        int dop = maxDegreeOfParallelism <= 0
            ? Math.Max(1, Environment.ProcessorCount / 2)
            : maxDegreeOfParallelism;

        var classMap = new Mat(h, w, MatType.CV_8UC1, Scalar.All(0));
        // higher = trust this tile more at this pixel (center of tile wins over seams)
        Mat? weightMap = centerWeightedBlend ? new Mat(h, w, MatType.CV_8UC1, Scalar.All(0)) : null;

        var xs = GenerateStarts(w, stride, _tileSize);
        var ys = GenerateStarts(h, stride, _tileSize);

        var jobs = new List<(int X, int Y)>(xs.Count * ys.Count);
        foreach (int y in ys)
            foreach (int x in xs)
                jobs.Add((x, y));

        int total = jobs.Count;
        int done = 0;
        var gate = new object();

        Console.WriteLine($"[推理] tiles={total}, 并行度={dop}, 影像={w}x{h}, 中心加权={centerWeightedBlend}");

        // Each worker gets its own raster handle: a GDAL dataset is not safe to share.
        Parallel.ForEach(
            jobs,
            new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = ct },
            tileSource.Fork,
            (job, _, local) =>
            {
                int x2 = Math.Min(job.X + _tileSize, w);
                int y2 = Math.Min(job.Y + _tileSize, h);
                int tw = x2 - job.X;
                int th = y2 - job.Y;

                using var tile = local.ReadTile(job.X, job.Y, tw, th);
                using var mask = RunTile(tile);

                lock (gate)
                {
                    if (weightMap is null)
                    {
                        using var sub = classMap[new Rect(job.X, job.Y, tw, th)];
                        mask.CopyTo(sub);
                    }
                    else
                    {
                        MergeCenterWeighted(classMap, weightMap, mask, job.X, job.Y, tw, th);
                    }

                    done++;
                    if (done == total || done % Math.Max(1, total / 20) == 0)
                        progress?.Report((done, total));
                }

                return local;
            },
            local => local.Dispose());

        weightMap?.Dispose();
        return classMap;
    }

    /// <summary>Supplies raw BGR tiles for the sliding window, whatever the backing store.</summary>
    private interface ITileSource : IDisposable
    {
        /// <summary>A handle usable from one other thread. Disposed by that worker.</summary>
        ITileSource Fork();

        Mat ReadTile(int x, int y, int w, int h);
    }

    private sealed class MatTileSource(Mat image) : ITileSource
    {
        // A Mat is only read here, so every worker can share the one instance.
        public ITileSource Fork() => this;

        public Mat ReadTile(int x, int y, int w, int h) => new(image, new Rect(x, y, w, h));

        public void Dispose()
        {
            // owned by the caller of Segment
        }
    }

    private sealed class RasterTileSource(RasterSource source) : ITileSource
    {
        public ITileSource Fork() => new RasterTileSource(source.Fork());

        public Mat ReadTile(int x, int y, int w, int h) => source.ReadBgrNative(x, y, w, h);

        public void Dispose() => source.Dispose();
    }

    /// <summary>
    /// Overwrite classMap pixels only where this tile's center-distance weight is higher.
    /// Weight is 1 at tile border, 255 at tile center — seams go to the tile that owns the pixel.
    /// </summary>
    private static void MergeCenterWeighted(Mat classMap, Mat weightMap, Mat tileMask, int ox, int oy, int tw, int th)
    {
        unsafe
        {
            byte* clsBase = (byte*)classMap.Data;
            byte* wBase = (byte*)weightMap.Data;
            byte* tBase = (byte*)tileMask.Data;
            int stepC = (int)classMap.Step();
            int stepW = (int)weightMap.Step();
            int stepT = (int)tileMask.Step();

            for (int ly = 0; ly < th; ly++)
            {
                int gy = oy + ly;
                byte* rc = clsBase + gy * stepC + ox;
                byte* rw = wBase + gy * stepW + ox;
                byte* rt = tBase + ly * stepT;

                // distance to nearest tile edge (local), scaled to 1..255
                double dyEdge = Math.Min(ly + 0.5, th - ly - 0.5) / (th * 0.5);

                for (int lx = 0; lx < tw; lx++)
                {
                    double dxEdge = Math.Min(lx + 0.5, tw - lx - 0.5) / (tw * 0.5);
                    double edge = Math.Min(dxEdge, dyEdge); // 0..1, 1 at center
                    byte wgt = (byte)Math.Clamp((int)(edge * 254) + 1, 1, 255);

                    if (wgt >= rw[lx])
                    {
                        rw[lx] = wgt;
                        rc[lx] = rt[lx];
                    }
                }
            }
        }
    }

    private Mat RunTile(Mat tileBgr)
    {
        int tileH = tileBgr.Rows;
        int tileW = tileBgr.Cols;

        using var padded = new Mat(_tileSize, _tileSize, MatType.CV_8UC3, Scalar.All(0));
        tileBgr.CopyTo(new Mat(padded, new Rect(0, 0, tileW, tileH)));

        using var rgb = new Mat();
        Cv2.CvtColor(padded, rgb, ColorConversionCodes.BGR2RGB);

        var tensor = ToTensorRgb01(rgb, _mean, _std);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputName, tensor),
        };

        using var results = _session.Run(inputs);
        var output = results.First().AsTensor<float>();
        using var fullMask = ArgMaxToMask(output);

        // Some models (e.g. SparseMask) emit logits at a fraction of the input resolution.
        using var sized = fullMask.Rows == _tileSize && fullMask.Cols == _tileSize
            ? fullMask.Clone()
            : ResizeNearest(fullMask, _tileSize, _tileSize);

        using var cropped = new Mat(sized, new Rect(0, 0, tileW, tileH));
        Mat result = cropped.Clone();
        ApplyClassMap(result);
        return result;
    }

    /// <summary>
    /// BGR Mat -> NCHW float tensor in RGB order, scaled to 0..1 and optionally normalised.
    /// Most land-cover checkpoints expect plain 0..1; a few (the OpenEarthMap ones) were
    /// trained on mean/std-normalised input and produce nonsense without it.
    /// </summary>
    private static DenseTensor<float> ToTensorRgb01(Mat rgb, float[]? mean, float[]? std)
    {
        int h = rgb.Rows;
        int w = rgb.Cols;
        var tensor = new DenseTensor<float>(new[] { 1, 3, h, w });
        Span<float> buffer = tensor.Buffer.Span;
        int plane = h * w;
        bool normalize = mean is { Length: 3 } && std is { Length: 3 };

        if (rgb.IsContinuous())
        {
            var src = new byte[h * w * 3];
            Marshal.Copy(rgb.Data, src, 0, src.Length);
            for (int i = 0; i < plane; i++)
            {
                int s = i * 3;
                for (int c = 0; c < 3; c++)
                {
                    float v = src[s + c] / 255f;
                    buffer[c * plane + i] = normalize ? (v - mean![c]) / std![c] : v;
                }
            }
        }
        else
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var p = rgb.At<Vec3b>(y, x);
                    buffer[y * w + x] = normalize ? (p.Item0 / 255f - mean![0]) / std![0] : p.Item0 / 255f;
                    buffer[plane + y * w + x] = normalize ? (p.Item1 / 255f - mean![1]) / std![1] : p.Item1 / 255f;
                    buffer[2 * plane + y * w + x] = normalize ? (p.Item2 / 255f - mean![2]) / std![2] : p.Item2 / 255f;
                }
            }
        }

        return tensor;
    }

    private static Mat ResizeNearest(Mat src, int w, int h)
    {
        var dst = new Mat();
        Cv2.Resize(src, dst, new Size(w, h), 0, 0, InterpolationFlags.Nearest);
        return dst;
    }

    /// <summary>Rewrites model output ids onto palette ids in place, using the sidecar's class_map.</summary>
    private void ApplyClassMap(Mat mask)
    {
        if (!HasClassMap || mask.Empty())
            return;

        int[] map = Metadata!.ClassMap!;
        if (map.Length != NumClasses)
            return;

        var lut = new byte[256];
        for (int i = 0; i < 256; i++)
            lut[i] = i < map.Length ? (byte)map[i] : (byte)0;

        unsafe
        {
            byte* basePtr = (byte*)mask.Data;
            int step = (int)mask.Step();
            for (int y = 0; y < mask.Rows; y++)
            {
                byte* row = basePtr + (long)y * step;
                for (int x = 0; x < mask.Cols; x++)
                    row[x] = lut[row[x]];
            }
        }
    }

    private static Mat ArgMaxToMask(Tensor<float> logits)
    {
        var dims = logits.Dimensions.ToArray();
        int classes, outH, outW;

        if (dims.Length == 4)
        {
            classes = dims[1];
            outH = dims[2];
            outW = dims[3];
        }
        else if (dims.Length == 3)
        {
            classes = dims[0];
            outH = dims[1];
            outW = dims[2];
        }
        else
        {
            throw new NotSupportedException($"不支持的输出维度: {string.Join(",", dims)}");
        }

        var mask = new Mat(outH, outW, MatType.CV_8UC1);
        var src = logits.ToArray();
        // layout [1,C,H,W] or [C,H,W] flattened row-major
        int hw = outH * outW;

        unsafe
        {
            byte* dst = (byte*)mask.Data;
            int step = (int)mask.Step();
            for (int y = 0; y < outH; y++)
            {
                byte* row = dst + y * step;
                int rowOff = y * outW;
                for (int x = 0; x < outW; x++)
                {
                    int best = 0;
                    float bestVal = float.NegativeInfinity;
                    int pix = rowOff + x;
                    for (int c = 0; c < classes; c++)
                    {
                        float v = src[c * hw + pix];
                        if (v > bestVal)
                        {
                            bestVal = v;
                            best = c;
                        }
                    }

                    row[x] = (byte)best;
                }
            }
        }

        return mask;
    }

    private static List<int> GenerateStarts(int length, int stride, int tileSize)
    {
        var list = new List<int>();
        if (length <= tileSize)
        {
            list.Add(0);
            return list;
        }

        for (int pos = 0; pos + tileSize < length; pos += stride)
            list.Add(pos);

        int last = length - tileSize;
        if (list.Count == 0 || list[^1] != last)
            list.Add(last);

        return list;
    }

    public void Dispose() => _session.Dispose();
}
