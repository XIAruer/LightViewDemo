using OSGeo.GDAL;
using OpenCvSharp;

namespace LandCover.Core;

/// <summary>
/// GDAL-backed raster reader: opens any GDAL-supported raster (GeoTIFF incl. tiled /
/// multi-page / COG, JPEG2000, PNG, ...) and serves arbitrary windowed, decimated reads.
///
/// This replaces <c>Cv2.ImRead</c> for geo data — OpenCV cannot open tiled or
/// multi-page GeoTIFFs, and it can never read part of a raster, so a 400 MP orthophoto
/// would have to be fully materialised in memory first.
/// </summary>
public sealed class RasterSource : IDisposable
{
    private readonly Dataset _ds;
    private readonly int[] _rgbBands;
    private readonly double[,]? _stretch; // per-band [min,max] for non-Byte data

    private bool _disposed;

    public string Path { get; }
    public int Width { get; }
    public int Height { get; }
    public int BandCount { get; }
    public string DriverName { get; }
    public string ProjectionWkt { get; }
    public GeoTiffGeoref? Georef { get; }

    /// <summary>All bands are GDT_Byte — the fast path.</summary>
    public bool IsByte { get; }

    /// <summary>1-based band index used for each of B,G,R (gray rasters repeat band 1).</summary>
    public int[] BgrBands { get; }

    /// <summary>1-based alpha band index, or null.</summary>
    public int? AlphaBand { get; }

    /// <summary>True when band 1 is a palette index; colours come from its colour table.</summary>
    public bool IsPalette { get; }

    public long PixelCount => (long)Width * Height;

    private RasterSource(Dataset ds, string path)
    {
        _ds = ds;
        Path = path;
        Width = ds.RasterXSize;
        Height = ds.RasterYSize;
        BandCount = ds.RasterCount;
        DriverName = ds.GetDriver().ShortName;
        ProjectionWkt = ds.GetProjectionRef() ?? string.Empty;

        if (BandCount <= 0)
            throw new InvalidDataException($"栅格不含任何波段: {path}");

        var interp = new ColorInterp[BandCount + 1];
        var type = new DataType[BandCount + 1];
        int ovrCount = 0;
        for (int i = 1; i <= BandCount; i++)
        {
            Band b = ds.GetRasterBand(i);
            interp[i] = b.GetColorInterpretation();
            type[i] = b.DataType;
            if (i == 1)
                ovrCount = b.GetOverviewCount();
        }

        HasOverviews = ovrCount > 0;
        OverviewCount = ovrCount;

        int Find(ColorInterp ci)
        {
            for (int i = 1; i <= BandCount; i++)
                if (interp[i] == ci)
                    return i;
            return -1;
        }

        int r = Find(ColorInterp.GCI_RedBand);
        int g = Find(ColorInterp.GCI_GreenBand);
        int bIdx = Find(ColorInterp.GCI_BlueBand);
        IsPalette = BandCount == 1 && interp[1] == ColorInterp.GCI_PaletteIndex;

        if (r > 0 && g > 0 && bIdx > 0)
            BgrBands = [bIdx, g, r];
        else if (BandCount >= 3)
            BgrBands = [3, 2, 1];
        else
            BgrBands = [1, 1, 1];

        _rgbBands = BgrBands;

        int alpha = Find(ColorInterp.GCI_AlphaBand);
        AlphaBand = alpha > 0 ? alpha : null;

        IsByte = true;
        for (int i = 1; i <= BandCount; i++)
            if (type[i] != DataType.GDT_Byte)
                IsByte = false;

        if (!IsByte)
        {
            _stretch = new double[BandCount + 1, 2];
            for (int i = 1; i <= BandCount; i++)
            {
                double lo = 0, hi = 255;
                if (type[i] != DataType.GDT_Byte)
                {
                    try
                    {
                        ds.GetRasterBand(i).GetStatistics(1, 1, out double mn, out double mx, out _, out _);
                        if (mx > mn)
                        {
                            lo = mn;
                            hi = mx;
                        }
                        else
                        {
                            // No useful statistics — fall back to the type's own range.
                            (lo, hi) = DefaultRange(type[i]);
                        }
                    }
                    catch
                    {
                        (lo, hi) = DefaultRange(type[i]);
                    }
                }

                _stretch[i, 0] = lo;
                _stretch[i, 1] = hi;
            }
        }

        Georef = GeoTiffGeoref.FromGdal(ds);
    }

    /// <summary>
    /// A second independent handle on the same file. GDAL datasets may not be used from two
    /// threads at once, so parallel tile workers each take their own fork.
    /// </summary>
    public RasterSource Fork() => Open(Path);

    public bool HasOverviews { get; }

    public int OverviewCount { get; }

    private static (double Lo, double Hi) DefaultRange(DataType t) => t switch
    {
        DataType.GDT_UInt16 => (0, 65535),
        DataType.GDT_Int16 => (-32768, 32767),
        DataType.GDT_UInt32 => (0, 4294967295),
        DataType.GDT_Int32 => (-2147483648, 2147483647),
        DataType.GDT_Float32 => (0, 1),
        DataType.GDT_Float64 => (0, 1),
        _ => (0, 255),
    };

    /// <summary>Opens with GDAL, or returns null (check <see cref="GdalRuntime.FailureReason"/>).</summary>
    public static RasterSource? TryOpen(string path)
    {
        if (!GdalRuntime.EnsureInitialized())
            return null;
        if (!File.Exists(path))
            return null;

        try
        {
            Dataset? ds = Gdal.Open(path, Access.GA_ReadOnly);
            if (ds is null)
                return null;
            return new RasterSource(ds, path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Opens with GDAL and throws a descriptive error if that is impossible.</summary>
    public static RasterSource Open(string path)
    {
        if (!GdalRuntime.EnsureInitialized())
            throw new InvalidOperationException($"GDAL 初始化失败: {GdalRuntime.FailureReason}");
        if (!File.Exists(path))
            throw new FileNotFoundException($"影像不存在: {path}", path);

        Dataset ds = Gdal.Open(path, Access.GA_ReadOnly)
            ?? throw new InvalidDataException($"GDAL 无法读取: {path}");
        return new RasterSource(ds, path);
    }

    /// <summary>Opens the first sub-dataset if this raster is a container (e.g. HDF). Usually unnecessary.</summary>
    public string[] ListSubDatasets()
    {
        string[]? md = _ds.GetMetadata("SUBDATASETS");
        if (md is null)
            return [];
        return md.Where(s => s.Contains("_NAME="))
                 .Select(s => s[(s.IndexOf('=') + 1)..])
                 .ToArray();
    }

    // ---------------------------------------------------------------- reading

    /// <summary>
    /// Reads <paramref name="xsize"/>×<paramref name="ysize"/> at (<paramref name="xoff"/>,<paramref name="yoff"/>),
    /// resampled to <paramref name="outW"/>×<paramref name="outH"/>, as a 3-channel BGR 8U Mat.
    /// The source window may extend past the raster edge — the outside is filled white.
    /// </summary>
    public Mat ReadBgr(int xoff, int yoff, int xsize, int ysize, int outW, int outH)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(outW, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outH, 1);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var dst = new Mat(outH, outW, MatType.CV_8UC3, Scalar.All(255));

        int sx0 = Math.Clamp(xoff, 0, Width);
        int sy0 = Math.Clamp(yoff, 0, Height);
        int sx1 = Math.Clamp(xoff + xsize, 0, Width);
        int sy1 = Math.Clamp(yoff + ysize, 0, Height);
        if (sx1 <= sx0 || sy1 <= sy0)
            return dst; // entirely outside

        int dx0 = (int)Math.Round((sx0 - (double)xoff) / xsize * outW);
        int dy0 = (int)Math.Round((sy0 - (double)yoff) / ysize * outH);
        int dx1 = (int)Math.Round((sx1 - (double)xoff) / xsize * outW);
        int dy1 = (int)Math.Round((sy1 - (double)yoff) / ysize * outH);
        int dw = Math.Clamp(dx1 - dx0, 1, outW - Math.Clamp(dx0, 0, outW - 1));
        int dh = Math.Clamp(dy1 - dy0, 1, outH - Math.Clamp(dy0, 0, outH - 1));
        dx0 = Math.Clamp(dx0, 0, outW - dw);
        dy0 = Math.Clamp(dy0, 0, outH - dh);

        byte[] buffer = ReadBgrCore(sx0, sy0, sx1 - sx0, sy1 - sy0, dw, dh);

        unsafe
        {
            byte* s = (byte*)System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0);
            byte* d = (byte*)dst.Data;
            int dstep = (int)dst.Step();
            for (int y = 0; y < dh; y++)
                Buffer.MemoryCopy(s + (long)y * dw * 3, d + (long)(dy0 + y) * dstep + dx0 * 3, dw * 3, dw * 3);
        }

        return dst;
    }

    /// <summary>Reads a window at native resolution, cropped to the raster bounds.</summary>
    public Mat ReadBgrNative(int x, int y, int w, int h) => ReadBgr(x, y, w, h, w, h);

    /// <summary>
    /// Whole raster decimated so the long side is <paramref name="maxDim"/> (never upscaled).
    /// Uses GDAL's internal overviews when the file has them.
    /// </summary>
    public Mat ReadPreview(int maxDim, out double scale)
    {
        scale = Math.Max(Width, Height) <= maxDim ? 1.0 : (double)Math.Max(Width, Height) / maxDim;
        int ow = Math.Max(1, (int)Math.Round(Width / scale));
        int oh = Math.Max(1, (int)Math.Round(Height / scale));
        return ReadBgr(0, 0, Width, Height, ow, oh);
    }

    /// <summary>As above, discarding the decimation factor.</summary>
    public Mat ReadPreview(int maxDim = 1600) => ReadPreview(maxDim, out _);

    /// <summary>Precondition: the window is fully inside the raster.</summary>
    private byte[] ReadBgrCore(int sx, int sy, int sw, int sh, int dw, int dh)
    {
        var outBuf = new byte[dw * dh * 3];

        if (IsPalette)
        {
            ReadPalette(sx, sy, sw, sh, dw, dh, outBuf);
            return outBuf;
        }

        if (IsByte)
        {
            int[] bandMap = [.. _rgbBands];
            int wanted = AlphaBand is null ? 3 : 4;
            if (AlphaBand is int a && wanted == 4)
                bandMap = [.. _rgbBands, a];

            var buf = new byte[(long)dw * dh * wanted];

            // GDAL's default band spacing (0) means BAND-SEQUENTIAL output — three separate
            // planes — not an interleaved buffer. Ask for pixel-interleaved explicitly:
            // adjacent channels are 1 byte apart, pixels `wanted` apart, rows dw*wanted apart.
            _ds.ReadRaster(sx, sy, sw, sh, buf, dw, dh, wanted, bandMap,
                pixelSpace: wanted, lineSpace: dw * wanted, bandSpace: 1);

            if (wanted == 3)
            {
                Buffer.BlockCopy(buf, 0, outBuf, 0, outBuf.Length);
            }
            else
            {
                // Composite over white so transparent borders don't become black terrain.
                int px = dw * dh;
                for (int i = 0; i < px; i++)
                {
                    int o = i * 3, n = i * 4;
                    int al = buf[n + 3];
                    if (al == 255)
                    {
                        outBuf[o] = buf[n];
                        outBuf[o + 1] = buf[n + 1];
                        outBuf[o + 2] = buf[n + 2];
                    }
                    else
                    {
                        int inv = 255 - al;
                        outBuf[o] = (byte)((buf[n] * al + 255 * inv) / 255);
                        outBuf[o + 1] = (byte)((buf[n + 1] * al + 255 * inv) / 255);
                        outBuf[o + 2] = (byte)((buf[n + 2] * al + 255 * inv) / 255);
                    }
                }
            }

            return outBuf;
        }

        // Non-Byte source: read as double, then stretch per band from cached statistics.
        var planes = new double[3][];
        int[] srcBands = [_rgbBands[2], _rgbBands[1], _rgbBands[0]]; // R,G,B order for clarity
        for (int c = 0; c < 3; c++)
        {
            planes[c] = new double[dw * dh];
            Band band = _ds.GetRasterBand(srcBands[c]);
            band.ReadRaster(sx, sy, sw, sh, planes[c], dw, dh, 0, 0);
        }

        for (int c = 0; c < 3; c++)
        {
            double lo = _stretch![srcBands[c], 0];
            double hi = _stretch[srcBands[c], 1];
            double k = hi > lo ? 255.0 / (hi - lo) : 0.0;
            double[] plane = planes[c];
            int dstOffset = 2 - c; // buffer is B,G,R
            for (int i = 0; i < plane.Length; i++)
            {
                double v = (plane[i] - lo) * k;
                outBuf[i * 3 + dstOffset] = (byte)(v <= 0 ? 0 : v >= 255 ? 255 : v);
            }
        }

        return outBuf;
    }

    private void ReadPalette(int sx, int sy, int sw, int sh, int dw, int dh, byte[] outBuf)
    {
        var idx = new byte[dw * dh];
        _ds.GetRasterBand(1).ReadRaster(sx, sy, sw, sh, idx, dw, dh, 0, 0);
        var table = _ds.GetRasterBand(1).GetColorTable();
        if (table is null)
        {
            for (int i = 0; i < idx.Length; i++)
            {
                outBuf[i * 3] = idx[i];
                outBuf[i * 3 + 1] = idx[i];
                outBuf[i * 3 + 2] = idx[i];
            }

            return;
        }

        int n = table.GetCount();
        var lut = new byte[n * 3];
        for (int i = 0; i < n; i++)
        {
            ColorEntry e = table.GetColorEntry(i);
            lut[i * 3] = (byte)e.c4;     // B
            lut[i * 3 + 1] = (byte)e.c3; // G
            lut[i * 3 + 2] = (byte)e.c2; // R
        }

        for (int i = 0; i < idx.Length; i++)
        {
            int v = idx[i];
            if (v < n)
            {
                outBuf[i * 3] = lut[v * 3];
                outBuf[i * 3 + 1] = lut[v * 3 + 1];
                outBuf[i * 3 + 2] = lut[v * 3 + 2];
            }
        }
    }

    /// <summary>Reads a single band as raw bytes (used to read back class-id label rasters).</summary>
    public byte[] ReadBandBytes(int bandIndex, int x, int y, int w, int h)
    {
        var buf = new byte[(long)w * h];
        _ds.GetRasterBand(bandIndex).ReadRaster(x, y, w, h, buf, w, h, 0, 0);
        return buf;
    }

    // ---------------------------------------------------------------- overviews

    /// <summary>
    /// Builds internal overviews so zoomed-out reads stop re-scanning full-resolution blocks.
    /// Only valid for rasters whose format supports it (GTiff, COG...). Returns the factors built.
    /// </summary>
    public int[] BuildOverviews(string resampling = "AVERAGE", int[]? factors = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        factors ??= AutoFactors();
        if (factors.Length == 0)
            return [];

        int err = _ds.BuildOverviews(resampling, factors);
        if (err != 0)
            throw new InvalidOperationException($"BuildOverviews 失败 (err={err})");
        return factors;
    }

    private int[] AutoFactors()
    {
        var list = new List<int>();
        int f = 2;
        while (Math.Max(Width, Height) / f >= 512)
        {
            list.Add(f);
            f *= 2;
        }

        return list.Count > 0 ? [.. list] : [2];
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
