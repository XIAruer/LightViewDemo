using System.Runtime.InteropServices;
using LandCover.Core;
using OpenCvSharp;
using CvPoint = OpenCvSharp.Point;

namespace LandCoverLabeler;

public sealed record AnnotationVectorExport(
    int Polygons,
    Rectangle Bounds,
    IReadOnlyDictionary<int, int> ByClass,
    List<string> Warnings);

public sealed record AnnotationVectorImport(
    int Polygons,
    int ClassPixels,
    int BackgroundPixels,
    int Skipped,
    Rectangle Bounds);

/// <summary>
/// The annotation ↔ vector round trip: trace the painted labels out to a shapefile so they can be
/// tidied up in QGIS/ArcGIS, then read the edited polygons back in and paint them.
///
/// The two halves have to agree about coordinates, and they do it through the pixel centre: the
/// export places a vertex at <c>PixelToWorld(px + 0.5)</c>, the import takes the pixel that
/// contains the incoming point, and a polygon that was not moved comes back on exactly the pixels
/// it left on. <see cref="SelfTest"/> checks that with a real export/import cycle.
/// </summary>
public static class AnnotationVectorIo
{
    /// <summary>
    /// Smallest polygon the export keeps, in pixels — far below the inference pipeline's default of
    /// 64. This is a hand-edited round trip, so silently dropping a small building would be worse
    /// than carrying a few specks.
    /// </summary>
    public const double ExportMinAreaPx = 4;

    /// <summary>Simplification tolerance for the export, in pixels; kept tight for the same reason.</summary>
    public const double ExportSimplifyPx = 0.5;

    // ------------------------------------------------------------------ export

    public static AnnotationVectorExport Export(
        AnnotationProject project,
        GeoTiffGeoref? georef,
        string path,
        double minAreaPx = ExportMinAreaPx,
        double simplifyPx = ExportSimplifyPx)
    {
        var warnings = new List<string>();
        List<(int X, int Y, int W, int H)> windows = ClusterBounds(project);
        if (windows.Count == 0)
            return new AnnotationVectorExport(0, Rectangle.Empty, new Dictionary<int, int>(), ["没有已标注的瓦片"]);

        var features = new List<VectorFeature>();
        Rectangle overall = Rectangle.Empty;
        int exportedWindows = 0;

        foreach ((int wx, int wy, int ww, int wh) in windows)
        {
            if (ww <= 0 || wh <= 0)
                continue;

            (byte[] plane, int pw, int ph, int px, int py) = project.Labels.SnapshotRect(wx, wy, ww, wh);
            using var mat = Mat.FromPixelData(ph, pw, MatType.CV_8UC1, plane);

            // minClass 0: in the annotation workflow "background" is something the user can paint, so
            // it has to survive the round trip. The inference pipeline keeps its own default of 1.
            features.AddRange(VectorExporter.Extract(
                mat, georef, minAreaPx, simplifyPx, offsetX: px, offsetY: py, minClass: 0));

            var window = new Rectangle(px, py, pw, ph);
            overall = overall.IsEmpty ? window : Rectangle.Union(overall, window);
            exportedWindows++;
        }

        if (features.Count == 0)
            warnings.Add("范围内没有可导出的多边形（都被最小面积过滤掉了？）");

        if (exportedWindows > 1)
            warnings.Add($"标注分散在 {exportedWindows} 片区域，已分别取景后写进同一个文件。");

        if (georef is not { HasTransform: true })
            warnings.Add("影像没有地理参考，导出的是像素坐标（Y 向上），导入时也按像素读回。");

        VectorWriter.Write(path, features, georef);

        var byClass = features.GroupBy(f => f.ClassId).ToDictionary(g => g.Key, g => g.Count());
        return new AnnotationVectorExport(features.Count, overall, byClass, warnings);
    }

    /// <summary>
    /// Bounding boxes of each connected group of painted tiles, padded by one tile.
    ///
    /// Tracing per cluster rather than over one overall bounding box matters when annotations are
    /// scattered: a couple of tiles in opposite corners of a several-hundred-MP image would otherwise force a
    /// full-image buffer and eight full-image contour passes. Clustering is safe because a polygon
    /// cannot span a tile with no paint in it — a shape that covered both would make both tiles
    /// painted, and therefore adjacent.
    /// </summary>
    private static List<(int X, int Y, int W, int H)> ClusterBounds(AnnotationProject project, int padTiles = 1)
    {
        var remaining = new HashSet<(int Tx, int Ty)>(project.LabeledTiles());
        var result = new List<(int, int, int, int)>();
        int ts = project.Labels.TileSize;

        while (remaining.Count > 0)
        {
            (int Tx, int Ty) seed = remaining.First();
            remaining.Remove(seed);

            var queue = new Queue<(int Tx, int Ty)>();
            queue.Enqueue(seed);
            int minTx = seed.Tx, maxTx = seed.Tx, minTy = seed.Ty, maxTy = seed.Ty;

            while (queue.Count > 0)
            {
                (int tx, int ty) = queue.Dequeue();
                minTx = Math.Min(minTx, tx);
                maxTx = Math.Max(maxTx, tx);
                minTy = Math.Min(minTy, ty);
                maxTy = Math.Max(maxTy, ty);

                foreach ((int nx, int ny) in new[] { (tx - 1, ty), (tx + 1, ty), (tx, ty - 1), (tx, ty + 1) })
                    if (remaining.Remove((nx, ny)))
                        queue.Enqueue((nx, ny));
            }

            int x = Math.Max(0, (minTx - padTiles) * ts);
            int y = Math.Max(0, (minTy - padTiles) * ts);
            int right = Math.Min(project.Width, (maxTx + 1 + padTiles) * ts);
            int bottom = Math.Min(project.Height, (maxTy + 1 + padTiles) * ts);
            result.Add((x, y, Math.Max(0, right - x), Math.Max(0, bottom - y)));
        }

        return result;
    }

    // ------------------------------------------------------------------ import

    /// <summary>
    /// Paints <paramref name="polygons"/> into the label store. Must run on the UI thread, inside a
    /// canvas undo step — it writes labels directly.
    /// </summary>
    public static AnnotationVectorImport Rasterize(LabelStore labels, IReadOnlyList<VectorPolygon> polygons)
    {
        int classPixels = 0, backgroundPixels = 0, skipped = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

        foreach (VectorPolygon poly in polygons)
        {
            if (!TryBounds(poly, labels, out int x0, out int y0, out int x1, out int y1))
            {
                skipped++;
                continue;
            }

            minX = Math.Min(minX, x0);
            minY = Math.Min(minY, y0);
            maxX = Math.Max(maxX, x1);
            maxY = Math.Max(maxY, y1);

            int w = x1 - x0;

            // A polygon covering the whole raster would need a 454 MB mask on a 31440x14450 image;
            // filling in horizontal strips caps the buffer regardless of how big the shape is.
            int rowsPerStrip = Math.Max(1, MaxMaskPixels / Math.Max(1, w));

            for (int sy = y0; sy < y1; sy += rowsPerStrip)
            {
                int sh = Math.Min(rowsPerStrip, y1 - sy);
                using var mask = new Mat(sh, w, MatType.CV_8UC1, Scalar.All(0));

                // Exterior first, then each hole back to background: a courtyard inside a building
                // polygon is not part of the building.
                FillRing(mask, poly.Rings[0], x0, sy);
                classPixels += labels.ApplyMask(MaskBytes(mask, w, sh), x0, sy, w, sh, (byte)poly.ClassId);

                for (int r = 1; r < poly.Rings.Count; r++)
                {
                    mask.SetTo(Scalar.All(0));
                    FillRing(mask, poly.Rings[r], x0, sy);
                    backgroundPixels += labels.ApplyMask(MaskBytes(mask, w, sh), x0, sy, w, sh, 0);
                }
            }
        }

        Rectangle bounds = maxX > minX && maxY > minY
            ? new Rectangle(minX, minY, maxX - minX, maxY - minY)
            : Rectangle.Empty;

        return new AnnotationVectorImport(polygons.Count - skipped, classPixels, backgroundPixels, skipped, bounds);
    }

    /// <summary>Raster rectangle the polygons will touch, or empty when none of them land on the raster.</summary>
    public static Rectangle ComputeBounds(IReadOnlyList<VectorPolygon> polygons, LabelStore labels)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

        foreach (VectorPolygon poly in polygons)
        {
            if (!TryBounds(poly, labels, out int x0, out int y0, out int x1, out int y1))
                continue;

            minX = Math.Min(minX, x0);
            minY = Math.Min(minY, y0);
            maxX = Math.Max(maxX, x1);
            maxY = Math.Max(maxY, y1);
        }

        return maxX > minX && maxY > minY
            ? new Rectangle(minX, minY, maxX - minX, maxY - minY)
            : Rectangle.Empty;
    }

    /// <summary>Largest mask buffer used while filling one polygon, in pixels (16 MB at one byte each).</summary>
    private const int MaxMaskPixels = 16_000_000;

    private static bool TryBounds(
        VectorPolygon poly, LabelStore labels, out int x0, out int y0, out int x1, out int y1)
    {
        x0 = y0 = x1 = y1 = 0;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (double[][] ring in poly.Rings)
        {
            foreach (double[] pt in ring)
            {
                minX = Math.Min(minX, pt[0]);
                maxX = Math.Max(maxX, pt[0]);
                minY = Math.Min(minY, pt[1]);
                maxY = Math.Max(maxY, pt[1]);
            }
        }

        if (minX > maxX || minY > maxY)
            return false;

        x0 = Math.Max(0, (int)Math.Floor(minX));
        y0 = Math.Max(0, (int)Math.Floor(minY));
        x1 = Math.Min(labels.Width, (int)Math.Ceiling(maxX) + 1);
        y1 = Math.Min(labels.Height, (int)Math.Ceiling(maxY) + 1);
        return x1 > x0 && y1 > y0;
    }

    private static void FillRing(Mat mask, double[][] ring, int offsetX, int offsetY)
    {
        var points = new CvPoint[ring.Length];
        for (int i = 0; i < ring.Length; i++)
        {
            // Floor, not round: the export emits pixel centres (px + 0.5), and the pixel containing
            // a point is what we want. Floor also absorbs the last few bits of float error in a
            // PixelToWorld -> WorldToPixel round trip, where round() would occasionally flip a cell.
            points[i] = new CvPoint(
                (int)Math.Floor(ring[i][0]) - offsetX,
                (int)Math.Floor(ring[i][1]) - offsetY);
        }

        Cv2.FillPoly(mask, [points], Scalar.All(255));
    }

    private static byte[] MaskBytes(Mat mask, int w, int h)
    {
        var bytes = new byte[(long)w * h];
        int step = (int)mask.Step();

        unsafe
        {
            byte* p = (byte*)mask.Data;
            for (int y = 0; y < h; y++)
                Marshal.Copy((IntPtr)(p + (long)y * step), bytes, y * w, w);
        }

        return bytes;
    }
}
