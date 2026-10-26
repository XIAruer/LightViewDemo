using LandCover.Core;
using OpenCvSharp;

namespace LandCoverLabeler;

public sealed record ExportResult(
    int TotalTiles,
    int Exported,
    int Skipped,
    int TrainTiles,
    int ValTiles,
    string ImagesDir,
    string MasksDir,
    List<string> Warnings);

/// <summary>
/// Turns an <see cref="AnnotationProject"/> into the image/mask tile pairs that
/// <c>train/train.py</c> consumes. Image tiles are cut straight from the source raster
/// through GDAL, so this works for rasters OpenCV cannot even open, and nothing but one
/// tile is ever held in memory.
/// </summary>
public static class TileExporter
{
    /// <summary>
    /// Writes georeferenced-free PNG tile pairs under <paramref name="dataDir"/>.
    /// Tiles with fewer than <paramref name="minPaintedFraction"/> labelled pixels are skipped:
    /// a tile that is 99% "ignore" contributes almost no gradient, and including it just
    /// biases the class balance.
    /// </summary>
    public static ExportResult Export(
        AnnotationProject project,
        RasterSource raster,
        string dataDir,
        double valRatio = 0.15,
        double minPaintedFraction = 0.02,
        IProgress<(int done, int total)>? progress = null)
    {
        string imagesDir = Path.Combine(dataDir, "images");
        string masksDir = Path.Combine(dataDir, "masks");
        string valImagesDir = Path.Combine(dataDir, "val_images");
        string valMasksDir = Path.Combine(dataDir, "val_masks");

        Directory.CreateDirectory(imagesDir);
        Directory.CreateDirectory(masksDir);

        var warnings = new List<string>();
        var labeled = project.LabeledTiles();
        int tileSize = project.TileSize;
        int minPainted = (int)(tileSize * tileSize * minPaintedFraction);

        var kept = new List<(int Tx, int Ty, byte[] Mask, int Painted)>();

        foreach (var (tx, ty) in labeled)
        {
            byte[] mask = project.Labels.GetOrCreateTile(tx, ty);
            int painted = 0;
            foreach (byte b in mask)
                if (b != Classes.Ignore)
                    painted++;

            if (painted < minPainted)
                continue;

            kept.Add((tx, ty, mask, painted));
        }

        // Deterministic, spatially-spread validation split: every k-th tile in raster order,
        // so neighbouring (near-identical) tiles do not land on both sides of the split.
        int nVal = kept.Count <= 1 ? 0 : (int)Math.Round(kept.Count * Math.Clamp(valRatio, 0, 0.9));
        var valIndexes = new HashSet<int>();
        if (nVal > 0)
        {
            double stride = (double)kept.Count / nVal;
            for (int i = 0; i < nVal; i++)
                valIndexes.Add(Math.Min(kept.Count - 1, (int)(i * stride)));
        }

        if (nVal > 0)
        {
            Directory.CreateDirectory(valImagesDir);
            Directory.CreateDirectory(valMasksDir);
        }

        string stem = Path.GetFileNameWithoutExtension(project.Source);
        int exported = 0;

        for (int i = 0; i < kept.Count; i++)
        {
            var (tx, ty, mask, _) = kept[i];
            int x = tx * tileSize;
            int y = ty * tileSize;

            bool isVal = valIndexes.Contains(i);
            string targetImages = isVal ? valImagesDir : imagesDir;
            string targetMasks = isVal ? valMasksDir : masksDir;

            string name = $"{stem}_x{x}_y{y}.png";

            try
            {
                using Mat img = raster.ReadBgr(x, y, tileSize, tileSize, tileSize, tileSize);
                Cv2.ImWrite(Path.Combine(targetImages, name), img);

                using var maskMat = Mat.FromPixelData(tileSize, tileSize, MatType.CV_8UC1, mask);
                Cv2.ImWrite(Path.Combine(targetMasks, name), maskMat);
                exported++;
            }
            catch (Exception ex)
            {
                warnings.Add($"瓦片 x{x}_y{y} 导出失败: {ex.Message}");
            }

            progress?.Report((i + 1, kept.Count));
        }

        bool anyVal = exported > 0 && valIndexes.Count > 0 && Directory.Exists(valImagesDir)
                      && Directory.EnumerateFiles(valImagesDir).Any();
        if (!anyVal && Directory.Exists(valImagesDir))
        {
            // train.py falls back to a random split when these dirs are absent/empty.
            warnings.Add("没有生成验证集瓦片；训练脚本会退回随机划分。");
        }

        return new ExportResult(
            labeled.Count,
            exported,
            labeled.Count - kept.Count,
            exported - (anyVal ? valIndexes.Count : 0),
            anyVal ? valIndexes.Count : 0,
            imagesDir,
            masksDir,
            warnings);
    }

    /// <summary>
    /// Writes the whole label plane as one georeferenced byte GeoTIFF, streamed tile by tile.
    /// Handy for reviewing annotations in QGIS next to the source imagery.
    /// </summary>
    public static void ExportFullMaskTiff(AnnotationProject project, GeoTiffGeoref? georef, string outPath)
    {
        int ts = project.TileSize;
        using var writer = RasterWriter.CreateLabelTiff(outPath, project.Width, project.Height, georef);

        for (int ty = 0; ty < project.Labels.TilesY; ty++)
        {
            for (int tx = 0; tx < project.Labels.TilesX; tx++)
            {
                if (!project.Labels.TileHasData(tx, ty))
                    continue;

                byte[] tile = project.Labels.GetOrCreateTile(tx, ty);
                int x = tx * ts;
                int y = ty * ts;
                int w = Math.Min(ts, project.Width - x);
                int h = Math.Min(ts, project.Height - y);
                if (w <= 0 || h <= 0)
                    continue;

                byte[] block = tile;
                if (w != ts || h != ts)
                {
                    block = new byte[w * h];
                    for (int j = 0; j < h; j++)
                        Array.Copy(tile, j * ts, block, j * w, w);
                }

                writer.WriteWindow(x, y, w, h, block);
            }
        }

        writer.BuildOverviews();
    }
}
