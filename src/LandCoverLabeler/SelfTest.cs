using LandCover.Core;
using OpenCvSharp;
using CvPoint = OpenCvSharp.Point;
using CvSize = OpenCvSharp.Size;

namespace LandCoverLabeler;

/// <summary>
/// Headless exercise of everything behind the UI: label store edits, undo, persistence,
/// tile export and the georeferenced mask writer. Run with <c>--selftest [raster]</c>;
/// returns a non-zero exit code if any check fails.
/// </summary>
internal static class SelfTest
{
    private static int _failures;

    /// <summary>Any raster the user happens to keep in samples/. The repo ships none.</summary>
    private static string? FindSampleRaster()
    {
        string dir = Path.Combine(RepoLayout.Root, "samples");
        if (!Directory.Exists(dir))
            return null;

        foreach (string pattern in new[] { "*.tif", "*.tiff", "*.png", "*.jpg" })
        {
            string? hit = Directory.EnumerateFiles(dir, pattern).Order().FirstOrDefault();
            if (hit is not null)
                return hit;
        }

        return null;
    }

    public static int Run(string[] args)
    {
        string? raster = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'))
            ?? FindSampleRaster();

        Console.WriteLine($"=== LandCoverLabeler 自检 ===");
        Console.WriteLine($"类别表: {Classes.LoadedFrom} ({Classes.NumClasses} 类)");
        Console.WriteLine($"GDAL: {GdalRuntime.DescribeSupport()}");
        Console.WriteLine($"影像: {raster ?? "（未指定）"}");
        Console.WriteLine();

        if (raster is null || !File.Exists(raster))
        {
            Console.WriteLine(raster is null
                ? "没有测试影像。用法: --selftest \"path\\to\\image.tif\"（仓库不携带样例数据）"
                : $"找不到测试影像: {raster}");
            return 1;
        }

        string workDir = Path.Combine(RepoLayout.Root, "output", "selftest_" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(workDir);

        try
        {
            using RasterSource src = RasterSource.Open(raster);
            Console.WriteLine($"尺寸 {src.Width}x{src.Height}, 波段 {src.BandCount}, 驱动 {src.DriverName}");
            Console.WriteLine($"CRS: {src.Georef?.CrsName ?? "无"}, EPSG={src.Georef?.Epsg?.ToString() ?? "-"}");
            Console.WriteLine();

            CheckReads(src);
            CheckReaderMatchesOpenCv();
            CheckCanvasCaching(raster, src);
            CheckLabelRectRepaint(raster, src);
            CheckMaskCleanup();

            AnnotationProject project = AnnotationProject.Create(
                Path.Combine(workDir, "proj"), raster, src.Width, src.Height, 512);

            CheckPainting(project, src);
            CheckUndo(project);
            CheckPersistence(project, raster, src);
            CheckExport(project, src, workDir);
            CheckMaskTiff(project, src, workDir);
            CheckInteractiveSegmentation(src);
            CheckInteractiveCanvas(project, src);
            CheckVectorRoundTrip(project, src, workDir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"未捕获异常: {ex}");
            _failures++;
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? "=== 全部通过 ==="
            : $"=== {_failures} 项失败 ===");
        return _failures == 0 ? 0 : 1;
    }

    private static void CheckReads(RasterSource src)
    {
        Section("读取");

        using Mat full = src.ReadBgr(0, 0, src.Width, src.Height, 256, 256);
        Check(full.Cols == 256 && full.Rows == 256, "全幅降采样到 256x256");

        using Mat tile = src.ReadBgrNative(0, 0, Math.Min(512, src.Width), Math.Min(512, src.Height));
        Check(!tile.Empty() && tile.Channels() == 3, "原生分辨率窗口读取 (BGR 3通道)");

        // A window hanging off the right edge must come back white-filled, not throw.
        using Mat edge = src.ReadBgrNative(src.Width - 100, src.Height - 100, 300, 300);
        Check(edge.Cols == 300 && edge.Rows == 300, "越界窗口被裁剪并填充");

        if (src.Georef is { HasTransform: true } g)
        {
            var (wx, wy) = g.PixelToWorld(0, 0);
            var (col, row) = g.WorldToPixel(wx, wy);
            Check(Math.Abs(col) < 1e-6 && Math.Abs(row) < 1e-6, "像素->世界->像素 往返一致");

            if (g.CanInverseToWgs84)
            {
                var (wx2, wy2) = g.PixelToWorld(src.Width / 2.0, src.Height / 2.0);
                bool ok = g.TryNativeToWgs84(wx2, wy2, out double lon, out double lat);
                Check(ok && lon is > -180 and < 180 && lat is > -90 and < 90,
                    $"中心点可转 WGS84 -> ({lon:F6}, {lat:F6})");
            }
        }
    }

    /// <summary>
    /// Cross-checks our GDAL reader against OpenCV's PNG decoder, byte for byte.
    ///
    /// These are two completely independent implementations of the same read, so they only
    /// agree if the band mapping and the buffer layout are both right. This is the test that
    /// catches GDAL's band-sequential default for multi-band ReadRaster (which silently
    /// scrambles the channels and makes every model prediction meaningless).
    /// </summary>
    private static void CheckReaderMatchesOpenCv()
    {
        Section("读取一致性 (GDAL vs OpenCV)");

        string samplesDir = Path.Combine(RepoLayout.Root, "samples");
        string? png = Directory.Exists(samplesDir)
            ? Directory.EnumerateFiles(samplesDir, "*.png").Order().FirstOrDefault()
            : null;
        if (png is null)
        {
            Console.WriteLine("  [SKIP] samples/ 下没有 PNG 可作对照");
            return;
        }

        using RasterSource gdal = RasterSource.Open(png);
        using Mat cv = Cv2.ImRead(png, ImreadModes.Color);
        Check(!cv.Empty(), $"OpenCV 能读取 {Path.GetFileName(png)}");
        if (cv.Empty())
            return;

        Check(cv.Cols == gdal.Width && cv.Rows == gdal.Height,
            $"尺寸一致 ({gdal.Width}x{gdal.Height})");
        if (cv.Cols != gdal.Width || cv.Rows != gdal.Height)
            return;

        // Full-image compare where the image is small enough, plus a few scattered windows.
        CheckWindow(gdal, cv, 0, 0, Math.Min(128, gdal.Width), Math.Min(128, gdal.Height), "左上角");
        CheckWindow(gdal, cv,
            Math.Max(0, gdal.Width / 2 - 64), Math.Max(0, gdal.Height / 2 - 64),
            Math.Min(128, gdal.Width), Math.Min(128, gdal.Height), "中心");
        CheckWindow(gdal, cv,
            Math.Max(0, gdal.Width - 128), Math.Max(0, gdal.Height - 128),
            Math.Min(128, gdal.Width), Math.Min(128, gdal.Height), "右下角");
    }

    private static void CheckWindow(RasterSource gdal, Mat cv, int x, int y, int w, int h, string label)
    {
        w = Math.Min(w, Math.Min(gdal.Width - x, cv.Cols - x));
        h = Math.Min(h, Math.Min(gdal.Height - y, cv.Rows - y));
        if (w <= 0 || h <= 0)
            return;

        using Mat oursRaw = gdal.ReadBgrNative(x, y, w, h);
        using Mat theirsRoi = new(cv, new Rect(x, y, w, h));
        using Mat ours = oursRaw.Clone();
        // A ROI of a wider image is strided; copying w*h*3 raw bytes from it would run past
        // the end of each row, so materialise a compact copy first.
        using Mat theirs = theirsRoi.Clone();

        var a = new byte[w * h * 3];
        var b = new byte[w * h * 3];
        System.Runtime.InteropServices.Marshal.Copy(ours.Data, a, 0, a.Length);
        System.Runtime.InteropServices.Marshal.Copy(theirs.Data, b, 0, b.Length);

        Check(a.AsSpan().SequenceEqual(b), $"{label} {w}x{h} 像素逐字节相同 (BGR 通道顺序正确)");
    }

    /// <summary>
    /// Guards the render cache: panning and hovering must not re-read the raster, and a brush
    /// stroke must not rebuild the whole cache. Without this, every mouse-move on a large
    /// GeoTIFF goes back to disk and the UI becomes unusable.
    /// </summary>
    private static void CheckCanvasCaching(string rasterPath, RasterSource src)
    {
        Section("渲染缓存（性能回归）");

        string dir = Path.Combine(Path.GetTempPath(), "lc_canvas_" + Guid.NewGuid().ToString("N")[..8]);
        var store = new LabelStore(src.Width, src.Height, dir, 512);

        using var raster = RasterSource.Open(rasterPath);
        using var canvas = new AnnotationCanvas();
        canvas.Width = 1000;
        canvas.Height = 700;
        canvas.SetSource(raster, store);
        canvas.FitToWindow();

        using var surface = new Bitmap(1000, 700);
        using var g = Graphics.FromImage(surface);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        canvas.RenderFrame(g);
        sw.Stop();
        int afterFirst = canvas.CacheRebuildCount;
        Check(afterFirst >= 1, $"首次渲染建立缓存（rebuild={afterFirst}, {sw.ElapsedMilliseconds}ms）");

        // Building the cache and actually showing it are two different things: a blit that is
        // skipped (source offset negative) leaves the canvas black with every counter looking
        // healthy. This is the "打开影像后界面是黑的，放大一下才出来" regression.
        double covered = DrawnFraction(surface);
        Check(covered > 0.5, $"首帧真的把影像画到视口里（覆盖 {covered * 100:F1}%）");

        // Panning inside the cached margin must be pure blitting.
        sw.Restart();
        for (int i = 0; i < 15; i++)
        {
            canvas.PanByScreen(-18, 0);
            canvas.RenderFrame(g);
        }

        sw.Stop();
        Check(canvas.CacheRebuildCount == afterFirst,
            $"缓存范围内平移 15 次不重建（rebuild={canvas.CacheRebuildCount}，共 {sw.ElapsedMilliseconds}ms）");

        // A zoom change legitimately rebuilds — but only once.
        int before = canvas.CacheRebuildCount;
        canvas.ZoomToRasterPoint(src.Width / 2.0, src.Height / 2.0, 1.0);
        canvas.RenderFrame(g);
        canvas.RenderFrame(g);
        Check(canvas.CacheRebuildCount == before + 1,
            $"缩放重建一次后复用（rebuild +{canvas.CacheRebuildCount - before}）");

        // Painting must not throw away the cache.
        before = canvas.CacheRebuildCount;
        canvas.Tool = AnnotationCanvas.ToolKind.Brush;
        canvas.PaintValue = 2;
        canvas.BrushSize = 30;
        for (int i = 0; i < 20; i++)
        {
            canvas.PanByScreen(0, 0);
            canvas.RenderFrame(g);
        }

        Check(canvas.CacheRebuildCount == before, $"连续重绘不重建缓存（rebuild +{canvas.CacheRebuildCount - before}）");

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // temp dir; best effort
        }
    }

    /// <summary>
    /// A partial overlay repaint must land exactly where a full rebuild would put it.
    ///
    /// This is the regression for the System.ExecutionEngineException on Enter. RenderLabelRegion
    /// copied the region as one contiguous block of stride*Height ints — but LockBits on a
    /// sub-rectangle reports the <em>bitmap's</em> row stride and points Scan0 into the middle of a
    /// row. So every row ran off the right edge of the rect (zeroing the overlay beside it), and
    /// when the rect sat on the bottom edge of the cache the last row ran off the end of the
    /// buffer and corrupted the heap — which only surfaces later, as an ExecutionEngineException
    /// with no stack trace. Comparing against a rebuild catches the smear deterministically; the
    /// rect is placed on the cache's bottom-right corner so the overrun path is exercised too.
    /// </summary>
    private static void CheckLabelRectRepaint(string rasterPath, RasterSource src)
    {
        Section("局部重绘（标注层）");

        string dir = Path.Combine(Path.GetTempPath(), "lc_repaint_" + Guid.NewGuid().ToString("N")[..8]);
        var store = new LabelStore(src.Width, src.Height, dir, 512);

        using var raster = RasterSource.Open(rasterPath);
        using var canvas = new AnnotationCanvas();
        canvas.Width = 1000;
        canvas.Height = 700;
        // Picker draws no cursor outline: DrawToolPreview's brush circle is positioned from the
        // real mouse, so on the brush tool the comparison picks up wherever the mouse happens to be.
        canvas.Tool = AnnotationCanvas.ToolKind.Picker;
        canvas.SetSource(raster, store);
        canvas.FitToWindow();

        using var partial = new Bitmap(1000, 700);
        using var gp = Graphics.FromImage(partial);
        canvas.RenderFrame(gp);                 // build the cache, so the next repaint is partial

        int w = 150, h = 150;
        int x = src.Width - w - 10, y = src.Height - h - 10;
        canvas.RunAsUndoStep(s =>
        {
            for (int j = y; j < y + h; j++)
                for (int i = x; i < x + w; i++)
                    s.SetPixel(i, j, 2);
            return w * h;
        }, new Rectangle(x, y, w, h));

        canvas.RenderFrame(gp);                 // painted from the partial update only

        // Same view, full rebuild: SetSource drops the cache without touching the view transform.
        canvas.SetSource(raster, store);
        using var rebuilt = new Bitmap(1000, 700);
        using var gr = Graphics.FromImage(rebuilt);
        canvas.RenderFrame(gr);

        var (diff, where) = CountDifferingPixels(partial, rebuilt);
        Check(diff == 0, $"局部重绘与整幅重建逐像素一致（差 {diff} 个像素{(diff == 0 ? "" : $"，范围 {where}")}");

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // temp dir; best effort
        }
    }

    /// <summary>
    /// The three defects people notice in a model's mask, on synthetic cases where the answer is
    /// known: enclosed holes ("a big region with pixel-sized blanks in it"), isolated speckle, and
    /// a boundary sitting near but not on the object's edge ("边缘弥散"). Plus the two guards that
    /// keep the cleanup from doing damage — a thin feature in flat country must survive untouched,
    /// and the pixel the user pointed at must survive.
    /// </summary>
    private static void CheckMaskCleanup()
    {
        Section("掩码清洗（洞 / 噪点 / 贴边）");

        const int S = 200;
        byte[] Square()
        {
            var m = new byte[S * S];
            for (int y = 50; y < 150; y++)
                for (int x = 50; x < 150; x++)
                    m[y * S + x] = 255;
            return m;
        }

        static int Painted(byte[] m)
        {
            int n = 0;
            foreach (byte b in m)
                if (b != 0)
                    n++;
            return n;
        }

        // --- enclosed holes of three sizes: 1px, 5px, 20px ---
        var holed = Square();
        foreach (var (hx, hy, hs) in new[] { (60, 60, 1), (80, 80, 5), (110, 110, 20) })
        {
            for (int y = hy; y < hy + hs; y++)
                for (int x = hx; x < hx + hs; x++)
                    holed[y * S + x] = 0;
        }

        MaskPostProcessor.CleanBinary(holed, S, S, guideBgr: null);
        // Majority3x3 rounds a square's corners (a corner is not a majority in its own 3x3), so
        // count the holes rather than demanding an untouched square.
        int left = 0;
        foreach (var (hx, hy, hs) in new[] { (60, 60, 1), (80, 80, 5), (110, 110, 20) })
        {
            for (int y = hy; y < hy + hs; y++)
                for (int x = hx; x < hx + hs; x++)
                    if (holed[y * S + x] == 0)
                        left++;
        }

        Check(left == 0, $"三种尺寸的内部空洞都补上了（还剩 {left} 个像素的洞）");
        Check(Painted(holed) >= 100 * 100 - 16, $"主体基本完整（{Painted(holed)}/10000，角落被多数滤波圆掉是正常的）");
        Check(holed[10 * S + 10] == 0 && holed[190 * S + 190] == 0, "填洞不会把物体外的背景也填进来");

        // --- isolated single-pixel speckle ---
        var specked = Square();
        var spots = new[] { (10, 10), (20, 30), (180, 40), (170, 170), (30, 160), (90, 20), (15, 90), (185, 100) };
        foreach (var (sx, sy) in spots)
            specked[sy * S + sx] = 255;

        MaskPostProcessor.CleanBinary(specked, S, S, guideBgr: null);
        Check(spots.All(p => specked[p.Item2 * S + p.Item1] == 0), "孤立的单像素噪点被多数滤波去掉");
        Check(Painted(specked) >= 100 * 100 - 16, $"主体不受多数滤波影响（{Painted(specked)}/10000）");

        // --- a 3px feature in flat country must come through untouched ---
        var bar = new byte[S * S];
        for (int y = 98; y < 101; y++)
            for (int x = 40; x < 160; x++)
                bar[y * S + x] = 255;

        using (var flat = new Mat(S, S, MatType.CV_8UC3, new Scalar(128, 128, 128)))
        {
            MaskPostProcessor.CleanBinary(bar, S, S, flat);
        }

        Check(Painted(bar) >= 3 * 120 * 95 / 100,
            $"平地上的细长目标（3px 条）不被贴边吃掉（剩 {Painted(bar)}/{3 * 120}）");

        // --- and the boundary really does move onto the image edge when there is one ---
        using var guide = new Mat(S, S, MatType.CV_8UC3, new Scalar(30, 30, 30));
        guide[new Rect(100, 0, 100, S)].SetTo(new Scalar(220, 220, 220));   // edge at x=100
        // A hard two-level edge gives the guided filter an exact fit and nothing to interpolate
        // along — it reproduces the mask unchanged. Real imagery has a soft, textured edge, so
        // blur this one to give the filter a gradient to follow.
        Cv2.GaussianBlur(guide, guide, new CvSize(7, 7), 0);
        var mask = new byte[S * S];
        for (int y = 40; y < 160; y++)
            for (int x = 40; x < 97; x++)      // right edge at x=97, three px shy of the guide's
                mask[y * S + x] = 255;

        static int RightEdge(byte[] m, int y0, int y1)
        {
            int last = 0;
            for (int y = y0; y < y1; y++)
                for (int x = 0; x < S; x++)
                    if (m[y * S + x] != 0 && x > last)
                        last = x;
            return last;
        }

        int before = RightEdge(mask, 40, 160);
        MaskPostProcessor.CleanBinary(mask, S, S, guide);
        int after = RightEdge(mask, 40, 160);
        Check(Math.Abs(after - 100) < Math.Abs(before - 100),
            $"边缘被吸向影像边缘（{before} -> {after}，影像边在 100）");

        // --- and a click survives all of it ---
        var empty = new byte[S * S];
        InteractiveSegmenter.ForcePositiveDisks(empty, S, S,
            [new SeedPoint(50, 50, true)], InteractiveSegmenter.DefaultSeedRadius);
        Check(empty[50 * S + 50] == 255, "正点所在像素被强制成前景（清洗吃不掉它）");
    }

    /// <summary>How many pixels differ between two same-sized surfaces, and their bounding box.</summary>
    private static (int Diff, Rectangle Bounds) CountDifferingPixels(Bitmap a, Bitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
            return (int.MaxValue, Rectangle.Empty);

        int diff = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int y = 0; y < a.Height; y++)
        for (int x = 0; x < a.Width; x++)
        {
            if (a.GetPixel(x, y).ToArgb() != b.GetPixel(x, y).ToArgb())
            {
                diff++;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        return diff == 0
            ? (0, Rectangle.Empty)
            : (diff, Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1));
    }

    /// <summary>
    /// Fraction of the surface anything was drawn onto. The canvas never clears the surface it is
    /// handed, so a pixel with zero alpha is one that neither the imagery nor the overlay reached.
    /// </summary>
    private static double DrawnFraction(Bitmap surface)
    {
        System.Drawing.Imaging.BitmapData bd = surface.LockBits(
            new Rectangle(0, 0, surface.Width, surface.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var row = new int[surface.Width];
            long drawn = 0;
            for (int y = 0; y < surface.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, surface.Width);
                foreach (int p in row)
                    if ((p & unchecked((int)0xFF000000)) != 0)
                        drawn++;
            }

            return drawn / (double)((long)surface.Width * surface.Height);
        }
        finally
        {
            surface.UnlockBits(bd);
        }
    }

    private static void CheckPainting(AnnotationProject p, RasterSource src)
    {
        Section("标注");
        LabelStore labels = p.Labels;

        Check(labels.GetPixel(5, 5) == Classes.Ignore, "空白处初始为 忽略(255)");

        labels.StampBrush(100.5, 100.5, 10, 1);
        Check(labels.GetPixel(100, 100) == 1, "画笔圆心写入类别 1");
        Check(labels.GetPixel(100 + 30, 100) == Classes.Ignore, "画笔半径外不受影响");

        labels.StrokeLine(200, 200, 400, 260, 5, 2);
        Check(labels.GetPixel(300, 230) == 2, "拖拽轨迹上像素被填充");

        labels.FillRect(600, 600, 660, 660, 4);
        Check(labels.GetPixel(630, 630) == 4, "矩形填充");
        Check(labels.GetPixel(700, 700) == Classes.Ignore, "矩形外不受影响");

        labels.FillPolygon([(800, 800), (900, 820), (860, 940)], 5);
        Check(labels.GetPixel(865, 860) == 5, "多边形内部被填充");
        Check(labels.GetPixel(805, 935) == Classes.Ignore, "多边形外部不受影响");

        // A brush centred on the corner where four tiles meet must write into all of them.
        int ts = labels.TileSize;
        labels.StampBrush(ts, ts, 6, 3);
        Check(labels.GetPixel(ts - 2, ts - 2) == 3 && labels.GetPixel(ts + 2, ts + 2) == 3,
            "跨瓦片边界的画笔（四角瓦片都被写入）");
        Check(labels.TileHasData(0, 0) && labels.TileHasData(1, 1)
              && labels.TileHasData(1, 0) && labels.TileHasData(0, 1),
            "四个相邻瓦片都记录了标注");

        Check(!labels.TileHasData(3, 3), "未触碰的瓦片不参与训练");

        var labeled = p.LabeledTiles();
        Console.WriteLine($"  已标注瓦片: {string.Join(", ", labeled.Select(t => $"({t.Tx},{t.Ty})"))}");
    }

    private static void CheckUndo(AnnotationProject p)
    {
        Section("撤销/重做");
        LabelStore labels = p.Labels;

        labels.BeginUndoRecord();
        labels.FillRect(1000, 1000, 1040, 1040, 6);
        List<(long Key, byte[] Before, byte[] After)> entry = labels.EndUndoRecord()!;
        Check(entry.Count > 0, "撤销记录捕获到被修改的瓦片");
        Check(labels.GetPixel(1020, 1020) == 6, "撤销前写入生效");

        labels.ApplyUndoTiles(entry, forward: false);
        Check(labels.GetPixel(1020, 1020) == Classes.Ignore, "撤销恢复到 忽略");

        labels.ApplyUndoTiles(entry, forward: true);
        Check(labels.GetPixel(1020, 1020) == 6, "重做恢复写入");
    }

    private static void CheckPersistence(AnnotationProject p, string raster, RasterSource src)
    {
        Section("保存/载入");
        p.Labels.SaveDirty();

        AnnotationProject reloaded = AnnotationProject.Load(p.ProjectFile);
        Check(reloaded.Width == src.Width && reloaded.Height == src.Height, "工程文件保留影像尺寸");
        Check(reloaded.Source == Path.GetFullPath(raster), "工程文件保留影像路径");
        Check(reloaded.Labels.GetPixel(100, 100) == 1, "重新载入后类别 1 仍在");
        Check(reloaded.Labels.GetPixel(630, 630) == 4, "重新载入后类别 4 仍在");
        Check(reloaded.LabeledTiles().Count > 0, "重新载入后能列出已标注瓦片");
    }

    private static void CheckExport(AnnotationProject p, RasterSource src, string workDir)
    {
        Section("导出训练瓦片");
        string dataDir = Path.Combine(workDir, "data");

        ExportResult r = TileExporter.Export(p, src, dataDir, valRatio: 0.0, minPaintedFraction: 0.0001);
        Check(r.Exported > 0, $"导出了 {r.Exported} 个瓦片");
        Check(Directory.Exists(r.ImagesDir) && Directory.Exists(r.MasksDir), "images/masks 目录已创建");

        string[] imgs = Directory.GetFiles(r.ImagesDir, "*.png");
        string[] msks = Directory.GetFiles(r.MasksDir, "*.png");
        Check(imgs.Length == msks.Length && imgs.Length > 0, "image 与 mask 数量一致");

        if (imgs.Length > 0)
        {
            using var img = Cv2.ImRead(imgs[0], ImreadModes.Color);
            using var msk = Cv2.ImRead(msks[0], ImreadModes.Grayscale);
            Check(img.Rows == p.TileSize && img.Cols == p.TileSize, $"影像瓦片为 {p.TileSize}x{p.TileSize}");
            Check(msk.Rows == p.TileSize && msk.Cols == p.TileSize, "掩码瓦片尺寸一致");

            var hist = new int[256];
            for (int y = 0; y < msk.Rows; y++)
                for (int x = 0; x < msk.Cols; x++)
                    hist[msk.At<byte>(y, x)]++;

            int labeled = hist.Where((n, id) => id < Classes.NumClasses).Sum();
            Check(labeled > 0, $"掩码含 {labeled} 个有效类别像素");
            Check(hist[Classes.Ignore] > 0, "未标注像素保持为 255（不当作背景参与训练）");
            Check(hist.Where((n, id) => id >= Classes.NumClasses && id != Classes.Ignore).Sum() == 0,
                "掩码中没有越界类别 ID");
        }

        // Skipping nearly-empty tiles keeps the class balance usable.
        ExportResult strict = TileExporter.Export(p, src, Path.Combine(workDir, "data_strict"),
            valRatio: 0.0, minPaintedFraction: 0.99);
        Check(strict.Exported == 0, "标注占比过低的瓦片被跳过");
    }

    private static void CheckMaskTiff(AnnotationProject p, RasterSource src, string workDir)
    {
        Section("导出全幅标注 GeoTIFF");
        string outPath = Path.Combine(workDir, "labels.tif");

        TileExporter.ExportFullMaskTiff(p, src.Georef, outPath);
        Check(File.Exists(outPath), "文件已生成");

        using RasterSource back = RasterSource.Open(outPath);
        Check(back.Width == p.Width && back.Height == p.Height, "尺寸与源影像一致");
        Check(back.Georef?.SourceWkt is not null, "保留了原影像的 CRS");

        byte[] sample = back.ReadBandBytes(1, 96, 96, 16, 16);
        Check(sample.Any(v => v == 1), "读回的掩码包含此前写入的类别 1");
        Check(sample.Any(v => v == Classes.Ignore), "未标注区域读回为 255");
    }

    /// <summary>
    /// Exercises the click-driven segmenter. The first half runs on a synthetic patch, where the
    /// right answer is known, so the assertions mean something; the second half runs on real
    /// imagery at the size the UI uses, purely to keep an eye on the latency budget — this has
    /// to stay fast enough to feel like a click, not a batch job.
    /// </summary>
    private static void CheckInteractiveSegmentation(RasterSource src)
    {
        Section("交互式分割");

        const int size = 256;
        using var patch = new Mat(size, size, MatType.CV_8UC3, new Scalar(70, 80, 75));
        var roof = new Rect(80, 70, 96, 110); // 10560 px
        Cv2.Rectangle(patch, roof, new Scalar(210, 205, 200), -1);

        const int seedX = 128;
        const int seedY = 125;

        InteractiveResult one = InteractiveSegmenter.Segment(
            patch, [new SeedPoint(seedX, seedY, true)], iterations: 3);
        Check(one.Width == size && one.Height == size, "结果尺寸与输入一致");
        Check(one.Foreground[seedY * size + seedX] == 255, "正点所在像素被判为前景");
        Check(one.ForegroundPixels > 2000, $"正点分出了区域（{one.ForegroundPixels} 像素，{one.Components} 块）");
        Check(one.Foreground[30 * size + 30] == 0, "远处背景没有被卷进前景");

        // A negative click inside the same blob must carve that part back out. Placed well away
        // from the positive click so the seeded blob survives the cut instead of splitting.
        InteractiveResult two = InteractiveSegmenter.Segment(
            patch, [new SeedPoint(seedX, seedY, true), new SeedPoint(seedX, 90, false)], iterations: 3);
        Check(two.Foreground[90 * size + seedX] == 0, "负点被排除在前景之外");
        Check(two.ForegroundPixels < one.ForegroundPixels,
            $"加负点后区域变小（{one.ForegroundPixels} -> {two.ForegroundPixels}）");

        // Real imagery, at the size the interactive tool works in. A patch of blank margin says
        // nothing about the tool, so put the ROI — and the click — where the imagery actually has
        // something in it, which is where a user would click.
        const int roi = 512;
        if (src.Width < roi || src.Height < roi)
        {
            Console.WriteLine("  [SKIP] 影像太小，跳过实图验证");
            return;
        }

        (int cx, int cy) = BusiestPixel(src, roi);
        int x = Math.Clamp(cx - roi / 2, 0, src.Width - roi);
        int y = Math.Clamp(cy - roi / 2, 0, src.Height - roi);
        using Mat real = src.ReadBgrNative(x, y, roi, roi);

        // A user clicks something solid — a roof, a road, a field — not a seam between two things.
        // Try the centres of the brightest and the darkest flat areas (on an orthophoto those are
        // the roofs and the water/tarmac) plus a couple of neutral spots, and keep the best.
        // Reported so a regression — the segmenter returning only the click's own disk — shows up
        // in the numbers.
        var clicks = new List<SeedPoint> { BrightestBlobCentre(real, bright: true) };
        clicks.Add(BrightestBlobCentre(real, bright: false));
        clicks.Add(new SeedPoint(roi / 3.0, roi / 3.0, true));
        clicks.Add(new SeedPoint(roi * 2 / 3.0, roi * 2 / 3.0, true));

        InteractiveResult? best = null;
        SeedPoint bestSeed = default;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (SeedPoint candidate in clicks)
        {
            InteractiveResult r = InteractiveSegmenter.Segment(real, [candidate]);
            if (best is null || r.ForegroundPixels > best.ForegroundPixels)
            {
                best = r;
                bestSeed = candidate;
            }
        }

        sw.Stop();
        InteractiveResult winner = best!;
        Console.WriteLine($"  实图 {roi}x{roi} @({x},{y}) 单击最优点"
            + $" @({bestSeed.X:F0},{bestSeed.Y:F0}) {sw.ElapsedMilliseconds / clicks.Count} ms/次"
            + $"（生长 {winner.GrownPixels} -> 前景 {winner.ForegroundPixels} 像素 = 区域的 "
            + $"{100.0 * winner.ForegroundPixels / (roi * roi):F1}%，{winner.Components} 块）");

        // What a user does next: clicks on a few things that are not the object.
        var guided = new List<SeedPoint> { bestSeed };
        foreach ((double fx, double fy) in new[] { (0.03, 0.03), (0.97, 0.03), (0.03, 0.97), (0.97, 0.97) })
            guided.Add(new SeedPoint(roi * fx, roi * fy, false));

        sw.Restart();
        InteractiveResult withNegatives = InteractiveSegmenter.Segment(real, guided);
        sw.Stop();
        Console.WriteLine($"  实图同一点 + 四角负点 {sw.ElapsedMilliseconds} ms"
            + $"（前景 {withNegatives.ForegroundPixels} 像素，{withNegatives.Components} 块）");

        Check(winner.ForegroundPixels > roi * roi * 0.01,
            $"实图上单击能分出像样的区域（{winner.ForegroundPixels} 像素 ≥ 区域的 1%）");
        Check(winner.Foreground[(int)bestSeed.Y * roi + (int)bestSeed.X] == 255, "实图上正点仍被判为前景");
        Check(sw.ElapsedMilliseconds < 20_000, $"实图分割在 20 秒以内（{sw.ElapsedMilliseconds} ms）");

        DumpSegmentationDiagnostics(real, winner, x, y, roi, bestSeed);
    }

    /// <summary>
    /// Writes the patch the segmenter saw, and the mask it produced, next to the other self-test
    /// output. GrabCut's failures are visual — "it found nothing" on real imagery is usually
    /// "there was no separable object there" — and that is impossible to judge from a pixel count.
    /// </summary>
    /// <summary>
    /// The centre of the brightest (or darkest) flat patch in the image: on an orthophoto that is
    /// a roof, a road or a field, which is what someone would actually click on. Test-only.
    /// </summary>
    private static SeedPoint BrightestBlobCentre(Mat bgr, bool bright)
    {
        using Mat gray = new();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);

        // Blurring first means the winner is the middle of a broad patch rather than one hot pixel.
        using Mat smoothed = new();
        Cv2.GaussianBlur(gray, smoothed, new CvSize(31, 31), 0);

        using Mat probe = new();
        if (bright)
            smoothed.CopyTo(probe);
        else
            Cv2.BitwiseNot(smoothed, probe);

        Cv2.MinMaxLoc(probe, out _, out _, out _, out CvPoint best);
        return new SeedPoint(best.X, best.Y, true);
    }

    /// <summary>
    /// The whole interactive path through the canvas: a click produces a request describing the
    /// right region, a result becomes a preview that writes nothing, accepting it writes labels in
    /// one undoable step, and — the pinned invariant — neither the preview nor the commit rebuilds
    /// the render cache. A stale result must never overwrite a newer one.
    /// </summary>
    private static void CheckInteractiveCanvas(AnnotationProject project, RasterSource src)
    {
        Section("交互标注（画布到标注）");

        using var canvas = new AnnotationCanvas { Width = 900, Height = 600 };
        canvas.SetSource(src, project.Labels);
        canvas.FitToWindow();
        canvas.Tool = AnnotationCanvas.ToolKind.Segment;
        canvas.PaintValue = 3;
        canvas.SegmentRegionSize = 256;

        using var surface = new Bitmap(900, 600);
        using var g = Graphics.FromImage(surface);
        canvas.RenderFrame(g);
        int baseline = canvas.CacheRebuildCount;

        AnnotationCanvas.SegmentRequest? request = null;
        canvas.SegmentRequested += r => request = r;

        double cx = src.Width / 2.0;
        double cy = src.Height / 2.0;
        canvas.AddSegmentSeed(cx, cy, positive: true);

        Check(request is not null, "点一下就会发出分割请求");
        if (request is null)
            return;

        AnnotationCanvas.SegmentRequest first = request;
        Check(first.Width == 256 && first.Height == 256,
            $"区域是设定的 256×256（实际 {first.Width}×{first.Height}）");
        Check(first.ClassId == 3, $"请求带上了当前类别（{first.ClassId}）");
        Check(first.Seeds.Count == 1 && first.Seeds[0].Positive, "正点被换算成区域内的坐标传出去");
        Check(Math.Abs(first.X + first.Width / 2.0 - cx) <= 1 && Math.Abs(first.Y + first.Height / 2.0 - cy) <= 1,
            "区域以正点为中心");

        int insideX = first.X + 128;
        int insideY = first.Y + 128;
        byte beforeInside = project.Labels.GetPixel(insideX, insideY);
        byte beforeBeside = project.Labels.GetPixel(first.X + 8, first.Y + 8);

        // A result shaped the way the segmenter returns one: a filled square inside the region.
        var mask = new byte[first.Width * first.Height];
        for (int y = 64; y < 192; y++)
            for (int x = 64; x < 192; x++)
                mask[y * first.Width + x] = 255;

        var result = new InteractiveResult(mask, first.Width, first.Height, 128 * 128, 1);
        canvas.SetSegmentPreview(result, first.JobId);
        Check(canvas.HasPendingSegment, "结果作为预览装进画布");

        canvas.RenderFrame(g);
        Check(canvas.CacheRebuildCount == baseline,
            $"预览不重建渲染缓存（rebuild +{canvas.CacheRebuildCount - baseline}）");
        Check(project.Labels.GetPixel(insideX, insideY) == beforeInside, "预览阶段还没有写进标注");

        int painted = canvas.ApplySegment();
        Check(painted == 128 * 128, $"应用写入 {painted} 像素（期望 {128 * 128}）");
        Check(project.Labels.GetPixel(insideX, insideY) == 3, "应用后该像素变成当前类别");
        Check(project.Labels.GetPixel(first.X + 8, first.Y + 8) == beforeBeside, "方框内、掩码外的像素没被涂改");
        Check(!canvas.HasPendingSegment, "应用后预览被清空");

        canvas.RenderFrame(g);
        Check(canvas.CacheRebuildCount == baseline,
            $"应用不重建渲染缓存（rebuild +{canvas.CacheRebuildCount - baseline}）");

        canvas.Undo();
        Check(project.Labels.GetPixel(insideX, insideY) == beforeInside, "一次撤销把整块区域退回");
        canvas.Redo();
        Check(project.Labels.GetPixel(insideX, insideY) == 3, "重做把区域放回来");
        canvas.Undo();

        // Two quick clicks: the first result must not land on top of the second.
        canvas.AddSegmentSeed(cx + 100, cy, positive: true);
        int staleJob = request!.JobId;
        canvas.AddSegmentSeed(cx + 140, cy, positive: true);
        Check(request.JobId > staleJob, "每次点击都会推进任务号");
        canvas.SetSegmentPreview(result, staleJob);
        Check(!canvas.HasPendingSegment, "过期结果不会覆盖更新的那次点击");

        canvas.AddSegmentSeed(cx + 60, cy, positive: false);
        Check(request.Seeds.Count(s => !s.Positive) == 1, "右键负点也进入请求");

        // --- bounding box prompt (左键拖拽) ---
        request = null;
        canvas.DiscardSegment();
        canvas.SetSegmentBox(new System.Drawing.Rectangle(200, 200, 300, 240));
        Check(canvas.SegmentBox == new System.Drawing.Rectangle(200, 200, 300, 240), "框被记下来");
        Check(request is null, "只有框、没有正点时不发请求（没有目标可问）");

        canvas.AddSegmentSeed(350, 320, positive: true);
        Check(request is not null, "框 + 正点才发请求");
        if (request is not null)
        {
            var bx = request.Box ?? System.Drawing.Rectangle.Empty;
            Check(request.Box is not null, "请求带上了框（给 SAM 当 box 提示）");
            Check(bx == new System.Drawing.Rectangle(200 - request.X, 200 - request.Y, 300, 240),
                "框换算到窗口坐标（和正点同一套坐标系）");
            Check(request.Width > 300 && request.Height > 240,
                $"窗口比框大（{request.Width}×{request.Height}），目标不会贴着窗口边");
            Check(bx.Left >= 0 && bx.Top >= 0 && bx.Right <= request.Width && bx.Bottom <= request.Height,
                "框落在窗口内");
        }

        // --- Z: undo one prompt at a time ---
        int jobBefore = request!.JobId;
        int seedsBefore = canvas.SegmentSeeds.Count;
        canvas.UndoSegmentPrompt();
        Check(canvas.SegmentSeeds.Count == seedsBefore - 1, "Z 撤销掉上一个点");
        Check(canvas.SegmentBox is not null, "Z 只撤一个提示，框还在");
        Check(request.JobId == jobBefore, "剩下的提示不足以算时不推进任务号");

        canvas.UndoSegmentPrompt();
        Check(canvas.SegmentBox is null, "再 Z 一次把框也撤掉");
        Check(canvas.PendingPromptCount == 0, "提示历史随之清空");

        canvas.UndoSegmentPrompt();
        Check(canvas.SegmentSeeds.Count == 0 && canvas.SegmentBox is null, "Z 到底之后是无操作");

        // Esc / switching tools mid-computation: the result that is still in flight must not drop
        // a preview back onto a canvas the user has already moved on from.
        int inFlight = request.JobId;
        canvas.DiscardSegment();
        Check(canvas.SegmentSeeds.Count == 0, "Esc/切换工具会清掉所有点");
        canvas.SetSegmentPreview(result, inFlight);
        Check(!canvas.HasPendingSegment, "放弃之后才到达的结果不会又冒出来");
    }

    private static void DumpSegmentationDiagnostics(
        Mat patch, InteractiveResult result, int x, int y, int roi, SeedPoint seed)
    {
        string dir = Path.Combine(RepoLayout.Root, "output", "selftest_seg");
        Directory.CreateDirectory(dir);

        using (Mat mask = Mat.FromPixelData(result.Height, result.Width, MatType.CV_8UC1, result.Foreground))
        {
            Cv2.ImWrite(Path.Combine(dir, "patch.png"), patch);
            Cv2.ImWrite(Path.Combine(dir, "mask.png"), mask);

            using var overlay = patch.Clone();
            overlay.SetTo(new Scalar(0, 0, 255), mask);
            Cv2.Circle(overlay, new CvPoint((int)seed.X, (int)seed.Y), 6, new Scalar(0, 255, 0), 2);
            Cv2.ImWrite(Path.Combine(dir, "overlay.png"), overlay);
        }

        Console.WriteLine($"  诊断图（取景 {roi}x{roi} @({x},{y})，绿圈=正点）: {dir}");
    }

    /// <summary>
    /// The pixel with the most edge energy in the whole raster, found on a decimated copy.
    /// Returns full-resolution coordinates. Test-only; blocking.
    /// </summary>
    private static (int X, int Y) BusiestPixel(RasterSource src, int window)
    {
        const int preview = 2048;
        double scale = Math.Max(1.0, (double)Math.Max(src.Width, src.Height) / preview);
        int pw = Math.Max(1, (int)(src.Width / scale));
        int ph = Math.Max(1, (int)(src.Height / scale));

        using Mat small = src.ReadBgr(0, 0, src.Width, src.Height, pw, ph);
        using Mat gray = new();
        Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);

        using Mat edges = new();
        Cv2.Laplacian(gray, edges, MatType.CV_32F);
        using Mat magnitude = new();
        Cv2.ConvertScaleAbs(edges, magnitude);

        // Average the edge response over roughly a quarter of the ROI, so the winner is an
        // area with structure rather than one noisy pixel.
        int k = Math.Max(3, (int)(window / scale / 4) | 1);
        using Mat density = new();
        Cv2.Blur(magnitude, density, new CvSize(k, k));
        Cv2.MinMaxLoc(density, out _, out _, out _, out CvPoint best);

        return (Math.Clamp((int)(best.X * scale), 0, src.Width - 1),
                Math.Clamp((int)(best.Y * scale), 0, src.Height - 1));
    }

    /// <summary>
    /// Drives <see cref="SamSegmentService"/> against the resident Python SAM service: start it,
    /// encode a real window, query it, and check the two conventions that make the tool feel
    /// right — a second prompt on the same window must be far faster than the first (the feature
    /// cache is doing its job), and a negative click must win over the model.
    ///
    /// Separate from <c>--selftest</c> for the same reason <c>--traincheck</c> is: it needs the
    /// venv, the <c>sam2</c> package and weights in <c>models/sam/</c>.
    /// </summary>
    public static int SamCheck(string[] args)
    {
        Console.WriteLine("=== SAM 后端自检（SamSegmentService -> sam_service.py）===");

        string weightDir = Path.Combine(RepoLayout.ModelsDir, "sam");
        Console.WriteLine($"权重目录: {weightDir}");
        if (!Directory.Exists(weightDir) || !Directory.EnumerateFiles(weightDir, "*.pt").Any()
                                          && !Directory.EnumerateFiles(weightDir, "*.pth").Any())
        {
            Console.WriteLine("没有 SAM 权重，跳过。从 https://huggingface.co/facebook/sam2.1-hiera-tiny "
                              + $"下载 .pt 放到 {weightDir} 后重试。");
            return 1;
        }

        int failures = 0;
        void Expect(bool ok, string what)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
            if (!ok)
                failures++;
        }

        string tile = Directory.EnumerateFiles(Path.Combine(RepoLayout.DataDir, "val_images"), "*.png")
            .FirstOrDefault() ?? string.Empty;
        if (tile.Length == 0)
        {
            Console.WriteLine("没有可用的样例瓦片（train/data/val_images），跳过。");
            return 1;
        }

        Console.WriteLine($"样例: {Path.GetFileName(tile)}");
        var swAll = System.Diagnostics.Stopwatch.StartNew();
        var (svc, error) = SamSegmentService.Start(log: line => Console.WriteLine($"  | {line}"));
        swAll.Stop();

        Expect(svc is not null, $"服务启动并加载模型（{swAll.Elapsed.TotalSeconds:F1}s）");
        if (svc is null)
        {
            Console.WriteLine($"  原因: {error}");
            return 1;
        }

        Expect(svc.Device is "cuda" or "mps" or "cpu", $"设备选择有效（{svc.Device}）");
        Expect(svc.ModelName.Length > 0 && svc.Family.Length > 0,
            $"模型已载入（{svc.ModelName}, {svc.Family}）");

        using var guard = svc;
        using var img = Cv2.ImRead(tile, ImreadModes.Color);
        Expect(!img.Empty(), "样例瓦片可读");

        // The same window twice must hit the service's feature cache; if it does not, every
        // refinement click pays for a full image encode and the tool feels dead.
        var seeds = new List<SeedPoint> { new(img.Cols / 2.0, img.Rows / 2.0, true) };
        var swFirst = System.Diagnostics.Stopwatch.StartNew();
        var (first, err1) = svc.Segment(img, seeds);
        swFirst.Stop();
        Expect(first is not null,
            $"单击分出结果（{swFirst.ElapsedMilliseconds}ms）{(err1 is null ? "" : " — " + err1)}");
        if (first is null)
            return 1;

        Expect(first.ForegroundPixels > 0, $"掩码非空（{first.ForegroundPixels:N0} 像素）");
        Expect(first.Foreground[img.Rows / 2 * img.Cols + img.Cols / 2] != 0, "正点落在前景里");

        seeds.Add(new SeedPoint(img.Cols / 2.0 + 30, img.Rows / 2.0 + 30, true));
        var swSecond = System.Diagnostics.Stopwatch.StartNew();
        var (second, _) = svc.Segment(img, seeds);
        swSecond.Stop();
        Expect(second is not null, $"加一个正点能重算（{swSecond.ElapsedMilliseconds}ms）");

        // The cache claim, checked rather than assumed: refinement must not re-encode.
        Expect(swSecond.ElapsedMilliseconds < Math.Max(1500, swFirst.ElapsedMilliseconds / 2),
            $"同一窗口第二次查询走缓存（首次 {swFirst.ElapsedMilliseconds}ms → {swSecond.ElapsedMilliseconds}ms）");

        // A negative click is a statement, not a hint: its disk is clear whatever SAM thought.
        var neg = new SeedPoint(img.Cols / 2.0 + 30, img.Rows / 2.0 + 30, false);
        var (withNeg, _) = svc.Segment(img, [seeds[0], neg]);
        Expect(withNeg is not null, "带负点能重算");
        if (withNeg is not null)
        {
            bool cleared = true;
            for (int dy = -3; dy <= 3; dy++)
            for (int dx = -3; dx <= 3; dx++)
            {
                int px = img.Cols / 2 + 30 + dx, py = img.Rows / 2 + 30 + dy;
                if ((uint)px < (uint)img.Cols && (uint)py < (uint)img.Rows
                    && withNeg.Foreground[py * img.Cols + px] != 0)
                    cleared = false;
            }

            Expect(cleared, "负点半径内没有前景像素");
            Expect(withNeg.ForegroundPixels < second!.ForegroundPixels,
                $"加负点后区域变小（{second.ForegroundPixels:N0} -> {withNeg.ForegroundPixels:N0}）");
        }

        Expect(svc.Diagnostics.Count > 0, "服务的 stderr 被逐行收进诊断（否则会堵死管道）");

        // The box prompt is the whole reason to drag one — it has to survive the wire format.
        var boxRect = new System.Drawing.Rectangle(img.Cols / 4, img.Rows / 4, img.Cols / 2, img.Rows / 2);
        var (boxed, boxErr) = svc.Segment(img, [seeds[0]], boxRect);
        Expect(boxed is not null, $"带 box 提示能出结果{(boxErr is null ? "" : " — " + boxErr)}");
        if (boxed is not null)
            Expect(boxed.ForegroundPixels > 0, $"box 提示下掩码非空（{boxed.ForegroundPixels:N0} 像素）");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "=== 全部通过 ===" : $"=== {failures} 项失败 ===");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Drives <see cref="TrainingService"/> exactly the way the 开始训练 button does, for a single
    /// epoch, and checks that the run finishes and its output is captured.
    ///
    /// This is the piece the self-test cannot cover on its own: "点完训练一直显示训练中" lived in the
    /// plumbing between the button and Python (worker re-spawn cost, a status label never reset, a
    /// log nobody could find), not in train.py. Run with <c>--traincheck</c>; needs the venv and
    /// exported tiles, so it is not part of <c>--selftest</c>.
    /// </summary>
    public static int TrainCheck(string[] args)
    {
        Console.WriteLine("=== 训练链路自检（TrainingService -> train.py，1 轮）===");

        string? python = RepoLayout.FindPython();
        if (python is null)
        {
            Console.WriteLine("找不到 Python 解释器，跳过。建 .venv 后重试。");
            return 1;
        }

        Console.WriteLine($"解释器: {python}");

        string images = Path.Combine(RepoLayout.DataDir, "images");
        string masks = Path.Combine(RepoLayout.DataDir, "masks");
        if (!Directory.Exists(images) || !Directory.EnumerateFiles(images, "*.png").Any())
        {
            Console.WriteLine($"还没有导出过训练瓦片（{images}），跳过。先在界面里点“导出瓦片”。");
            return 1;
        }

        string work = Path.Combine(Path.GetTempPath(), "lc_traincheck");
        Directory.CreateDirectory(work);
        string ckpt = Path.Combine(work, "best.pt");
        string logPath = Path.Combine(work, "train.log");

        var scriptArgs = new List<string>
        {
            "--images", images,
            "--masks", masks,
            "--out", ckpt,
            "--epochs", "1",
            "--batch", "2",
            "--size", "512",
            "--encoder", "mit_b0",
            // Same value the GUI passes: the Windows spawn workers cost ~10 s per epoch each.
            "--workers", "0",
        };

        string valImages = Path.Combine(RepoLayout.DataDir, "val_images");
        if (Directory.Exists(valImages) && Directory.EnumerateFiles(valImages).Any())
            scriptArgs.AddRange(["--val-images", valImages, "--val-masks", Path.Combine(RepoLayout.DataDir, "val_masks")]);
        else
            scriptArgs.AddRange(["--val-ratio", "0.15"]);

        var lines = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Print as we go: the complaint that started this was not being able to see the console,
        // so the check shows the same stream the UI's log pane gets.
        var service = new TrainingService();
        ProcessResult result = service.RunScriptAsync(python, RepoLayout.TrainScript, scriptArgs, line =>
        {
            lock (lines) lines.Add(line);
            Console.WriteLine($"  | {line}");
        }, logPath).GetAwaiter().GetResult();
        sw.Stop();

        Console.WriteLine();
        Console.WriteLine($"退出码 {result.ExitCode}，用时 {sw.Elapsed.TotalSeconds:F1}s，捕获 {lines.Count} 行");

        int failures = 0;
        void Expect(bool ok, string what)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
            if (!ok)
                failures++;
        }

        Expect(result.ExitCode == 0, "train.py 正常退出");
        Expect(lines.Count > 5, "输出被逐行捕获（不是一片空白）");
        Expect(File.Exists(logPath) && new FileInfo(logPath).Length > 0, $"日志文件已写出（{logPath}）");

        // history.json is written only after the last epoch, so its presence is the difference
        // between "the run finished" and "best.pt happened to be saved on the way" — which is
        // exactly how the earlier half-finished run looked.
        string history = Path.Combine(work, "history.json");
        Expect(File.Exists(history), "跑满全部 epoch（history.json 存在）");
        Expect(File.Exists(ckpt), "权重文件已生成");

        if (lines.Count > 0)
        {
            Expect(lines.Any(l => l.StartsWith("$ ", StringComparison.Ordinal)), "命令本身也进了日志（可回溯）");
            Expect(lines.Any(l => l.Contains("done. best mIoU")), "收尾行没有丢（读取器已排空）");
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "=== 训练链路自检通过 ===" : $"=== 训练链路自检 {failures} 项失败 ===");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The annotation ↔ shapefile round trip: paint → export → read back → paint again. A GIS edit
    /// is only useful if the coordinates survive the trip, and pixel/CRS mistakes here are silent —
    /// the file looks fine and the labels land somewhere else.
    /// </summary>
    private static void CheckVectorRoundTrip(AnnotationProject project, RasterSource src, string workDir)
    {
        Section("标注矢量往返（shp）");

        if (src.Georef is not { HasTransform: true } georef)
        {
            Console.WriteLine("  [SKIP] 该影像没有地理参考，矢量往返不适用");
            return;
        }

        string shp = Path.Combine(workDir, "labels.shp");
        AnnotationVectorExport exported = AnnotationVectorIo.Export(project, georef, shp);
        Check(exported.Polygons > 0, $"导出 {exported.Polygons} 个面");
        Check(File.Exists(shp), "shp 已生成");
        Check(File.Exists(Path.ChangeExtension(shp, ".dbf")), "dbf 属性表已生成（中文类名需要它）");
        Check(File.Exists(Path.ChangeExtension(shp, ".prj")), "prj 坐标系已生成");

        // A hand-built polygon first: this is the part a GIS edit depends on, and it has an exact
        // expected answer, unlike a traced contour.
        var ring = new double[][]
        {
            [10.5, 20.5], [30.5, 20.5], [30.5, 40.5], [10.5, 40.5], [10.5, 20.5],
        };
        AnnotationProject synth = AnnotationProject.Create(
            Path.Combine(workDir, "synth"), project.Source, project.Width, project.Height, project.TileSize);
        AnnotationVectorIo.Rasterize(synth.Labels, [new VectorPolygon(3, [ring])]);
        Check(synth.Labels.GetPixel(15, 25) == 3, "手写多边形落在它指定的像素上");
        Check(synth.Labels.GetPixel(5, 25) == Classes.Ignore, "多边形外的像素没有被涂");

        // Now the real round trip, into a different empty project so nothing compares against itself.
        AnnotationProject target = AnnotationProject.Create(
            Path.Combine(workDir, "roundtrip"), project.Source, project.Width, project.Height, project.TileSize);
        VectorReadResult read = VectorReader.ReadPolygons(shp, georef, defaultClassId: 1);
        Check(read.Polygons.Count == exported.Polygons,
            $"读回的面数与导出的一致（{read.Polygons.Count} / {exported.Polygons}）");
        Check(read.Warnings.Count == 0, $"读回没有告警（{string.Join("；", read.Warnings)}）");

        AnnotationVectorImport imported = AnnotationVectorIo.Rasterize(target.Labels, read.Polygons);
        Check(imported.ClassPixels > 0, $"写回 {imported.ClassPixels} 个类别像素");

        (int x, int y, int w, int h) = project.LabeledBounds();
        long same = 0, total = 0, extra = 0;
        for (int gy = y; gy < y + h; gy++)
        {
            for (int gx = x; gx < x + w; gx++)
            {
                byte before = project.Labels.GetPixel(gx, gy);
                byte after = target.Labels.GetPixel(gx, gy);

                if (before != Classes.Ignore)
                {
                    total++;
                    if (before == after)
                        same++;
                }
                else if (after != Classes.Ignore)
                {
                    extra++;
                }
            }
        }

        double agree = total == 0 ? 0 : (double)same / total;
        Console.WriteLine($"  像素一致率 {agree:P1}（{same}/{total}），多涂 {extra} 个");

        // Measured 99.9% on this fixture. The margin covers boundary pixels moving when a contour is
        // traced and refilled; a CRS or offset mistake collapses this to near zero.
        Check(agree > 0.95, $"往返后像素一致率 > 95%（{agree:P1}）");
        Check(extra < total * 0.05, $"往返没有大面积多涂（多涂 {extra}，原始 {total}）");

        // The class attribute has to survive too, not just the geometry.
        Check(target.Labels.GetPixel(630, 630) == 4 || project.Labels.GetPixel(630, 630) == Classes.Ignore,
            "类别 ID 跟着多边形一起回来了（class_id 字段）");

        // Annotations in opposite corners: the export takes each cluster of painted tiles separately,
        // so a stroke in each corner must not force one buffer covering the whole raster.
        if (project.Width > project.TileSize * 2 && project.Height > project.TileSize * 2)
        {
            AnnotationProject scattered = AnnotationProject.Create(
                Path.Combine(workDir, "scattered"), project.Source, project.Width, project.Height, project.TileSize);
            scattered.Labels.StampBrush(5.5, 5.5, 4, 2);
            scattered.Labels.StampBrush(project.Width - 5.5, project.Height - 5.5, 4, 5);

            string shp2 = Path.Combine(workDir, "scattered.shp");
            AnnotationVectorExport spread = AnnotationVectorIo.Export(scattered, georef, shp2);
            Check(spread.Polygons == 2, $"分散两处的标注各导出一个面（{spread.Polygons}）");
            Check(spread.Warnings.Any(w => w.Contains("片区域")), "多片区域时给出提示");

            VectorReadResult back = VectorReader.ReadPolygons(shp2, georef, defaultClassId: 1);
            Check(back.Polygons.Count == 2, "两片区域都读得回来");
            Check(back.Polygons.Any(p => p.ClassId == 2) && back.Polygons.Any(p => p.ClassId == 5),
                "两个面的类别都没串");
        }
    }

    private static void Section(string name) => Console.WriteLine($"--- {name} ---");

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        if (!ok)
            _failures++;
    }
}
