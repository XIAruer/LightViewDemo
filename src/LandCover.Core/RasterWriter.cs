using OSGeo.GDAL;

namespace LandCover.Core;

/// <summary>
/// Windowed single-band byte raster writer. Creating the dataset once and streaming
/// 512-pixel blocks into it keeps a full-resolution label mask (a several-hundred-MP scene
/// would be that many bytes as one array) off the managed heap entirely.
/// </summary>
public sealed class RasterWriter : IDisposable
{
    private readonly Dataset _ds;
    private bool _disposed;

    public string Path { get; }
    public int Width { get; }
    public int Height { get; }

    private RasterWriter(Dataset ds, string path, int width, int height)
    {
        _ds = ds;
        Path = path;
        Width = width;
        Height = height;
    }

    /// <summary>Creates a deflate-compressed, internally tiled byte GeoTIFF, georeferenced like the source.</summary>
    public static RasterWriter CreateLabelTiff(string path, int width, int height, GeoTiffGeoref? georef)
    {
        if (!GdalRuntime.EnsureInitialized())
            throw new InvalidOperationException($"GDAL 初始化失败: {GdalRuntime.FailureReason}");

        Driver drv = Gdal.GetDriverByName("GTiff")
            ?? throw new InvalidOperationException("GDAL 缺少 GTiff 驱动");

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        if (File.Exists(path))
            File.Delete(path);

        const int block = 512;
        var options = new[]
        {
            "TILED=YES",
            $"BLOCKXSIZE={block}",
            $"BLOCKYSIZE={block}",
            "COMPRESS=DEFLATE",
            "PREDICTOR=1",
            "BIGTIFF=IF_SAFER",
            "NUM_THREADS=ALL_CPUS",
        };

        Dataset? ds = drv.Create(path, width, height, 1, DataType.GDT_Byte, options);
        if (ds is null)
            throw new IOException($"无法创建 GeoTIFF: {path}");

        if (georef?.Affine is { Length: 6 } gt)
            ds.SetGeoTransform(gt);
        if (!string.IsNullOrWhiteSpace(georef?.SourceWkt))
            ds.SetProjection(georef!.SourceWkt);

        // 255 = unlabelled; without a NoData value GIS tools would render it as a class.
        ds.GetRasterBand(1).SetNoDataValue(255);

        return new RasterWriter(ds, path, width, height);
    }

    /// <summary>Writes one block; the caller must stay inside the raster.</summary>
    public void WriteWindow(int x, int y, int w, int h, byte[] data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (x < 0 || y < 0 || x + w > Width || y + h > Height)
            throw new ArgumentOutOfRangeException(nameof(x), $"窗口 ({x},{y},{w},{h}) 超出 {Width}x{Height}");
        if (data.Length < (long)w * h)
            throw new ArgumentException("数据长度不足", nameof(data));

        _ds.GetRasterBand(1).WriteRaster(x, y, w, h, data, w, h, 0, 0);
    }

    /// <summary>
    /// Builds internal overviews so GIS tools (and our own preview) can read the label
    /// raster zoomed out without touching every full-resolution block.
    /// </summary>
    public void BuildOverviews(string resampling = "NEAREST")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var factors = new List<int>();
        for (int f = 2; Math.Max(Width, Height) / f >= 256; f *= 2)
            factors.Add(f);
        if (factors.Count == 0)
            return;

        int[] arr = [.. factors];
        _ds.BuildOverviews(resampling, arr);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _ds.FlushCache();
        _ds.Dispose();
    }
}
