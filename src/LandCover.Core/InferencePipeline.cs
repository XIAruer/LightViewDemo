using OpenCvSharp;

namespace LandCover.Core;

/// <summary>Whether to write the full-size colour/overlay previews.</summary>
public enum PreviewMode
{
    /// <summary>Write previews for images small enough to keep in memory (<= 12 MP).</summary>
    Auto,

    /// <summary>Always write, however large the image — may use a lot of memory.</summary>
    Always,

    /// <summary>Never write.</summary>
    Never,
}

/// <summary>Everything the inference run needs, so the CLI and the GUI drive one code path.</summary>
public sealed record InferenceOptions(
    string InputPath,
    string ModelPath,
    int TileSize = 512,
    int Overlap = 64,
    int Threads = 0,
    bool CenterWeightedBlend = true,
    bool PostProcess = true,
    int MorphKernel = 3,
    int PostMinAreaPx = 64,
    PreviewMode Preview = PreviewMode.Auto,
    double MinAreaPx = 64,
    double SimplifyPx = 1.5,
    string? OutputPrefix = null,
    bool WriteShapefile = false,
    bool WriteGeoPackage = false,
    bool BuildOverviews = false,
    bool ForceInMemory = false,
    string? GeorefPath = null);

public sealed record ClassStat(int ClassId, string Name, long Pixels, double Percent);

public sealed record InferenceResult(
    string InputPath,
    string Prefix,
    int Width,
    int Height,
    bool Streamed,
    bool PreviewWritten,
    double InferSeconds,
    double PostSeconds,
    double VectorSeconds,
    int FeatureCount,
    IReadOnlyList<ClassStat> ClassStats,
    IReadOnlyList<string> WrittenFiles,
    IReadOnlyList<string> Warnings,
    GeoTiffGeoref? Georef,
    string? MaskPath,
    string? ColorPath,
    string? OverlayPath);

/// <summary>
/// The full classification pipeline: raster in, mask + statistics + vector layers out.
/// Shared by the console front-end (LandCoverDemo) and the GUI (LandCoverInfer) so the two
/// can never drift apart.
/// </summary>
public static class InferencePipeline
{
    public static InferenceResult Run(
        InferenceOptions o,
        Action<string> log,
        IProgress<(int Done, int Total)>? tileProgress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(o.InputPath))
            throw new FileNotFoundException($"找不到输入影像: {o.InputPath}", o.InputPath);
        if (!File.Exists(o.ModelPath))
            throw new FileNotFoundException($"找不到 ONNX 模型: {o.ModelPath}", o.ModelPath);

        var warnings = new List<string>();
        var written = new List<string>();

        int threads = o.Threads > 0 ? o.Threads : Math.Max(1, Environment.ProcessorCount / 2);
        string prefix = o.OutputPrefix ?? Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(o.InputPath))!,
            Path.GetFileNameWithoutExtension(o.InputPath));

        string? outDir = Path.GetDirectoryName(Path.GetFullPath(prefix));
        if (!string.IsNullOrEmpty(outDir))
            Directory.CreateDirectory(outDir);

        log($"输入: {o.InputPath}");
        log($"输出前缀: {prefix}");
        log($"模型: {o.ModelPath}");
        log($"切片: {o.TileSize}px, 重叠: {o.Overlap}px, 线程: {threads}");

        // --- open the raster. GDAL handles tiled / multi-page / multi-GB GeoTIFFs OpenCV rejects.
        RasterSource? raster = GdalRuntime.IsAvailable ? RasterSource.TryOpen(o.InputPath) : null;
        if (raster is null && !GdalRuntime.IsAvailable)
            log($"GDAL 不可用 ({GdalRuntime.FailureReason})，回退到 OpenCV 读图。");

        int imgW = 0, imgH = 0;
        bool streaming = false;
        Mat? image = null;

        if (raster is not null)
        {
            imgW = raster.Width;
            imgH = raster.Height;
            log($"驱动: {raster.DriverName}, 波段: {raster.BandCount}, "
                + $"位深: {(raster.IsByte ? "8U" : "非8位(自动拉伸)")}, 金字塔: {raster.OverviewCount} 级");

            if (o.BuildOverviews && raster.OverviewCount == 0)
            {
                try
                {
                    int[] built = raster.BuildOverviews();
                    log(built.Length == 0
                        ? "金字塔: 该格式不支持内建金字塔"
                        : $"金字塔: 已建立 {string.Join(",", built)}");
                }
                catch (Exception ex)
                {
                    warnings.Add($"建立金字塔失败: {ex.Message}");
                    log($"金字塔: 失败 — {ex.Message}");
                }
            }

            streaming = !o.ForceInMemory;
        }

        if (raster is null || o.ForceInMemory)
        {
            image = LoadImageRobust(o.InputPath, log);
            if (image is null || image.Empty())
                throw new InvalidDataException(
                    "影像读取失败。若输入是 GeoTIFF，请确认 GDAL 可用（GDAL 不可用时只能读 PNG/JPG）。");

            imgW = image.Cols;
            imgH = image.Rows;
        }

        long pixels = (long)imgW * imgH;
        log($"影像尺寸: {imgW} x {imgH} ({pixels / 1_000_000.0:F1} MP)");

        try
        {
            ct.ThrowIfCancellationRequested();

            bool wantPreview = o.Preview switch
            {
                PreviewMode.Always => true,
                PreviewMode.Never => false,
                _ => pixels <= 12_000_000,
            };

            if (o.Preview == PreviewMode.Auto && !wantPreview)
            {
                warnings.Add("影像超过 12 MP，已跳过彩色/叠加预览（内存考虑）。");
                log("预览: 跳过全图 color/overlay（大图省内存）。");
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var segmenter = new OnnxLandCoverSegmenter(o.ModelPath, o.TileSize, o.Overlap, intraOpThreads: 1);

            using Mat classMap = streaming
                ? segmenter.SegmentRaster(raster!, threads, o.CenterWeightedBlend, tileProgress, ct)
                : segmenter.Segment(image!, threads, o.CenterWeightedBlend, tileProgress, ct);
            sw.Stop();
            double inferSeconds = sw.Elapsed.TotalSeconds;
            log($"推理完成，耗时 {inferSeconds:F1}s");

            ct.ThrowIfCancellationRequested();

            Mat maskOut = classMap;
            Mat? postOwned = null;
            double postSeconds = 0;
            if (o.PostProcess)
            {
                var swPost = System.Diagnostics.Stopwatch.StartNew();
                postOwned = MaskPostProcessor.Process(classMap, new MaskPostProcessor.Options(
                    Enabled: true, MorphKernel: o.MorphKernel, Majority3: true, MinAreaPx: o.PostMinAreaPx));
                maskOut = postOwned;
                swPost.Stop();
                postSeconds = swPost.Elapsed.TotalSeconds;
                log($"后处理: 形态学(k={o.MorphKernel}) + 3×3多数滤波 + 碎斑过滤(min={o.PostMinAreaPx}px)，"
                    + $"{postSeconds:F1}s");
            }

            string maskPath = prefix + "_mask.png";
            string colorPath = prefix + "_color.png";
            string overlayPath = prefix + "_overlay.png";
            string statsPath = prefix + "_stats.txt";

            Cv2.ImWrite(maskPath, maskOut);
            written.Add(maskPath);

            string? wroteColor = null;
            string? wroteOverlay = null;
            if (wantPreview)
            {
                using var colorMask = Colorize(maskOut);
                Cv2.ImWrite(colorPath, colorMask);
                written.Add(colorPath);
                wroteColor = colorPath;

                Mat source = image ?? raster!.ReadPreview(4000);
                try
                {
                    if (source.Size() == maskOut.Size())
                    {
                        using var overlay = new Mat();
                        Cv2.AddWeighted(source, 0.55, colorMask, 0.45, 0, overlay);
                        Cv2.ImWrite(overlayPath, overlay);
                        written.Add(overlayPath);
                        wroteOverlay = overlayPath;
                    }
                    else
                    {
                        warnings.Add("源图与掩码尺寸不同，未生成叠加预览。");
                    }
                }
                finally
                {
                    if (image is null)
                        source.Dispose();
                }
            }

            var classStats = WriteStats(maskOut, statsPath);
            written.Add(statsPath);

            // --- georeference: GDAL/PROJ first, hand-parsed GeoTIFF tags as fallback ---
            GeoTiffGeoref? georef = raster?.Georef;
            if (georef is null && o.GeorefPath is not null)
            {
                using var gds = RasterSource.TryOpen(o.GeorefPath);
                georef = gds?.Georef ?? (GeoTiffGeoref.TryRead(o.GeorefPath, out var parsed) ? parsed : null);
            }

            if (georef is not null)
            {
                log($"地理参考: {georef.CrsName ?? "未知"}"
                    + (georef.Epsg is int e ? $" (EPSG:{e})" : "")
                    + $" | 像素 {georef.PixelSizeX:G6} x {georef.PixelSizeY:G6}"
                    + $" | KML 反算: {(georef.CanInverseToWgs84 ? "可用" : "不可用")}");
            }
            else
            {
                warnings.Add("未找到地理参考，矢量使用像素坐标。");
                log("地理参考: 未找到（矢量将使用像素坐标）。");
            }

            ct.ThrowIfCancellationRequested();

            var swVec = System.Diagnostics.Stopwatch.StartNew();
            var features = VectorExporter.Extract(maskOut, georef, o.MinAreaPx, o.SimplifyPx);
            swVec.Stop();

            string geojsonPath = prefix + ".geojson";
            string kmlPath = prefix + ".kml";
            VectorExporter.WriteGeoJson(geojsonPath, features, georef);
            VectorExporter.WriteKml(kmlPath, features, georef);
            written.Add(geojsonPath);
            written.Add(kmlPath);

            if (GdalRuntime.IsAvailable && (o.WriteShapefile || o.WriteGeoPackage))
            {
                try
                {
                    if (o.WriteShapefile)
                    {
                        string p = prefix + ".shp";
                        VectorWriter.Write(p, features, georef);
                        written.Add(p);
                    }

                    if (o.WriteGeoPackage)
                    {
                        string p = prefix + ".gpkg";
                        VectorWriter.Write(p, features, georef);
                        written.Add(p);
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"GDAL 写出矢量失败: {ex.Message}");
                    log($"[矢量] GDAL 写出失败: {ex.Message}");
                }
            }

            double vectorSeconds = swVec.Elapsed.TotalSeconds;
            log($"矢量化完成: {features.Count} 个面, 耗时 {vectorSeconds:F1}s");

            postOwned?.Dispose();

            return new InferenceResult(
                o.InputPath, prefix, imgW, imgH, streaming, wantPreview,
                inferSeconds, postSeconds, vectorSeconds,
                features.Count, classStats, written, warnings, georef,
                maskPath, wroteColor, wroteOverlay);
        }
        finally
        {
            image?.Dispose();
            raster?.Dispose();
        }
    }

    /// <summary>OpenCV cannot read many tiled GeoTIFFs; only used when GDAL is unavailable.</summary>
    private static Mat? LoadImageRobust(string path, Action<string> log)
    {
        foreach (var mode in new[] { ImreadModes.Color, ImreadModes.AnyColor | ImreadModes.AnyDepth, ImreadModes.Unchanged })
        {
            try
            {
                var img = Cv2.ImRead(path, mode);
                if (img.Empty())
                {
                    img.Dispose();
                    continue;
                }

                if (img.Depth() != MatType.CV_8U)
                {
                    var u8 = new Mat();
                    img.ConvertTo(u8, MatType.CV_8UC(img.Channels()));
                    img.Dispose();
                    img = u8;
                }

                if (img.Channels() == 1)
                {
                    var bgr = new Mat();
                    Cv2.CvtColor(img, bgr, ColorConversionCodes.GRAY2BGR);
                    img.Dispose();
                    return bgr;
                }

                if (img.Channels() == 4)
                {
                    var bgr = new Mat();
                    Cv2.CvtColor(img, bgr, ColorConversionCodes.BGRA2BGR);
                    img.Dispose();
                    return bgr;
                }

                if (img.Channels() >= 3)
                    return img;
                img.Dispose();
            }
            catch (Exception ex)
            {
                log($"[读图] 模式 {mode} 失败: {ex.Message}");
            }
        }

        string? pngSibling = Path.ChangeExtension(path, ".png");
        if (!string.Equals(pngSibling, path, StringComparison.OrdinalIgnoreCase) && File.Exists(pngSibling))
        {
            var img = Cv2.ImRead(pngSibling, ImreadModes.Color);
            if (!img.Empty())
            {
                log($"[读图] 已改用同名 PNG: {pngSibling}");
                return img;
            }

            img.Dispose();
        }

        return null;
    }

    public static Mat Colorize(Mat classMap)
    {
        var color = new Mat(classMap.Rows, classMap.Cols, MatType.CV_8UC3, Scalar.All(0));
        for (int y = 0; y < classMap.Rows; y++)
        {
            for (int x = 0; x < classMap.Cols; x++)
            {
                byte id = classMap.At<byte>(y, x);
                if (id < LandCoverPalette.ColorsBgr.Length)
                {
                    var (b, g, r) = LandCoverPalette.ColorsBgr[id];
                    color.Set(y, x, new Vec3b(b, g, r));
                }
            }
        }

        return color;
    }

    public static List<ClassStat> WriteStats(Mat classMap, string statsPath)
    {
        var counts = new long[Math.Max(256, (int)LandCoverPalette.ClassCount)];
        for (int y = 0; y < classMap.Rows; y++)
            for (int x = 0; x < classMap.Cols; x++)
                counts[classMap.At<byte>(y, x)]++;

        long total = (long)classMap.Rows * classMap.Cols;
        var stats = new List<ClassStat>();

        using var writer = new StreamWriter(statsPath, false, System.Text.Encoding.UTF8);
        writer.WriteLine("class_id\tclass_name\tpixels\tpercent");
        for (int i = 0; i < Math.Min(16, counts.Length); i++)
        {
            if (counts[i] == 0 && i >= (int)LandCoverPalette.ClassCount)
                continue;

            string name = i < LandCoverPalette.ClassNames.Length
                ? LandCoverPalette.ClassNames[i]
                : $"class_{i}";
            double pct = total == 0 ? 0 : counts[i] * 100.0 / total;
            writer.WriteLine($"{i}\t{name}\t{counts[i]}\t{pct:F2}");
            stats.Add(new ClassStat(i, name, counts[i], pct));
        }

        return stats;
    }
}
