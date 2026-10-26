using OpenCvSharp;
using CvSize = OpenCvSharp.Size;

namespace LandCoverLabeler;

/// <summary>
/// Sparse store for the class-id label raster.
///
/// Labels are held as 512×512 byte tiles that are allocated only when painted and written
/// back as one PNG per tile (<c>labels/x{tx}_y{ty}.png</c>). That means annotating a corner
/// of a several-hundred-MP orthophoto costs a few hundred KB rather than the hundreds of MB a dense plane would
/// need, and each file maps 1:1 onto a training tile. Unpainted pixels are <see cref="Classes.Ignore"/>,
/// never class 0 — otherwise the un-annotated majority would teach the model "background".
/// </summary>
public sealed class LabelStore
{
    private readonly Dictionary<long, byte[]> _tiles = new();
    private readonly HashSet<long> _dirty = new();

    public int TileSize { get; }
    public int Width { get; }
    public int Height { get; }
    public string TilesDir { get; }

    public int TilesX { get; }
    public int TilesY { get; }

    public LabelStore(int width, int height, string tilesDir, int tileSize = 512)
    {
        Width = width;
        Height = height;
        TilesDir = tilesDir;
        TileSize = tileSize;
        TilesX = (width + tileSize - 1) / tileSize;
        TilesY = (height + tileSize - 1) / tileSize;
        Directory.CreateDirectory(tilesDir);
    }

    private static long Key(int tx, int ty) => ((long)tx << 32) | (uint)ty;

    public bool HasDirty => _dirty.Count > 0;

    /// <summary>Number of painted (non-ignore) pixels currently held in memory.</summary>
    public long PaintedPixelCount
    {
        get
        {
            long n = 0;
            foreach (var t in _tiles.Values)
                foreach (byte b in t)
                    if (b != Classes.Ignore)
                        n++;
            return n;
        }
    }

    public IEnumerable<long> LoadedTileKeys => _tiles.Keys;

    /// <summary>Tile grid coordinates present in memory (loaded or painted).</summary>
    public IEnumerable<(int Tx, int Ty)> LoadedTiles
    {
        get
        {
            foreach (long k in _tiles.Keys)
                yield return ((int)(k >> 32), (int)(uint)k);
        }
    }

    // ------------------------------------------------------------------ tiles

    /// <summary>True when this tile has any painted pixel (i.e. is worth exporting).</summary>
    public bool TileHasData(int tx, int ty)
    {
        if (!_tiles.TryGetValue(Key(tx, ty), out byte[]? data))
            return File.Exists(TilePath(tx, ty));

        foreach (byte b in data)
            if (b != Classes.Ignore)
                return true;
        return false;
    }

    private string TilePath(int tx, int ty) => Path.Combine(TilesDir, $"x{tx}_y{ty}.png");

    /// <summary>Returns the tile, allocating a fresh all-ignore one (or loading it from disk) if needed.</summary>
    public byte[] GetOrCreateTile(int tx, int ty)
    {
        long k = Key(tx, ty);

        // A pixel read may have recorded this tile as absent from disk; it is real now.
        _knownMissing.Remove(k);
        _cachedKey = long.MinValue;

        if (_tiles.TryGetValue(k, out byte[]? t))
            return t;

        int w = Math.Min(TileSize, Width - tx * TileSize);
        int h = Math.Min(TileSize, Height - ty * TileSize);
        var tile = new byte[TileSize * TileSize];
        Array.Fill(tile, Classes.Ignore);

        string path = TilePath(tx, ty);
        if (File.Exists(path))
        {
            try
            {
                using var m = Cv2.ImRead(path, ImreadModes.Grayscale);
                if (!m.Empty())
                {
                    // A saved tile is always TileSize² so that training tiles line up exactly.
                    using var resized = m.Rows == TileSize && m.Cols == TileSize
                        ? m.Clone()
                        : ResizeNearest(m, TileSize, TileSize);

                    unsafe
                    {
                        byte* src = (byte*)resized.Data;
                        int step = (int)resized.Step();
                        for (int y = 0; y < TileSize; y++)
                            System.Runtime.InteropServices.Marshal.Copy(
                                (IntPtr)(src + (long)y * step), tile, y * TileSize, TileSize);
                    }
                }
            }
            catch
            {
                // corrupt tile → treat as unlabelled rather than losing the session
            }
        }

        _tiles[k] = tile;

        // Mark the padding outside the raster as ignore so nothing downstream sees a stray class.
        if (w < TileSize || h < TileSize)
        {
            for (int y = 0; y < TileSize; y++)
            {
                if (y >= h)
                {
                    Array.Fill(tile, Classes.Ignore, y * TileSize, TileSize);
                    continue;
                }

                for (int x = w; x < TileSize; x++)
                    tile[y * TileSize + x] = Classes.Ignore;
            }
        }

        return tile;
    }

    private static Mat ResizeNearest(Mat src, int w, int h)
    {
        var dst = new Mat();
        Cv2.Resize(src, dst, new CvSize(w, h), 0, 0, InterpolationFlags.Nearest);
        return dst;
    }

    // ------------------------------------------------------------------ pixels

    // Rendering scans pixels in raster order, so a one-entry tile cache turns the
    // per-pixel dictionary probe into a single bounds check. The cache also remembers
    // *misses*: without that, every pixel of an un-annotated tile re-ran File.Exists,
    // which is a syscall per pixel — enough to make the whole UI unusable on a big image.
    private long _cachedKey = long.MinValue;
    private byte[]? _cachedTile;
    private readonly HashSet<long> _knownMissing = [];

    public byte GetPixel(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return Classes.Ignore;

        int tx = x / TileSize;
        int ty = y / TileSize;
        long k = Key(tx, ty);

        if (k == _cachedKey)
            return _cachedTile is null
                ? Classes.Ignore
                : _cachedTile[(y - ty * TileSize) * TileSize + (x - tx * TileSize)];

        if (_tiles.TryGetValue(k, out byte[]? t))
        {
            _cachedKey = k;
            _cachedTile = t;
        }
        else if (!_knownMissing.Contains(k) && File.Exists(TilePath(tx, ty)))
        {
            _cachedKey = k;
            _cachedTile = GetOrCreateTile(tx, ty);
        }
        else
        {
            _knownMissing.Add(k);
            _cachedKey = k;
            _cachedTile = null;
            return Classes.Ignore;
        }

        return _cachedTile![(y - ty * TileSize) * TileSize + (x - tx * TileSize)];
    }

    public void SetPixel(int x, int y, byte value)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return;

        int tx = x / TileSize;
        int ty = y / TileSize;
        long k = Key(tx, ty);
        byte[] t = GetOrCreateTile(tx, ty);

        // Snapshot a tile the first time this edit touches it, so one undo step costs a few
        // 256 KB tile copies rather than a copy of the whole affected screen area.
        if (_recording is not null && !_recording.ContainsKey(k))
            _recording[k] = (byte[])t.Clone();

        t[(y - ty * TileSize) * TileSize + (x - tx * TileSize)] = value;
        _dirty.Add(k);
    }

    /// <summary>Filled circle; the brush primitive. Coordinates are raster pixels.</summary>
    public void StampBrush(double cx, double cy, double radius, byte value)
    {
        int x0 = (int)Math.Floor(cx - radius);
        int x1 = (int)Math.Ceiling(cx + radius);
        int y0 = (int)Math.Floor(cy - radius);
        int y1 = (int)Math.Ceiling(cy + radius);
        double r2 = radius * radius;

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                double dx = x + 0.5 - cx;
                double dy = y + 0.5 - cy;
                if (dx * dx + dy * dy <= r2)
                    SetPixel(x, y, value);
            }
        }
    }

    /// <summary>Stamps along a segment so fast drags do not leave gaps.</summary>
    public void StrokeLine(double x0, double y0, double x1, double y1, double radius, byte value)
    {
        double dx = x1 - x0;
        double dy = y1 - y0;
        double len = Math.Sqrt(dx * dx + dy * dy);
        double step = Math.Max(1.0, radius * 0.4);
        int n = (int)Math.Ceiling(len / step);

        for (int i = 0; i <= n; i++)
        {
            double t = n == 0 ? 0 : (double)i / n;
            StampBrush(x0 + dx * t, y0 + dy * t, radius, value);
        }
    }

    public void FillRect(int x0, int y0, int x1, int y1, byte value)
    {
        if (x1 < x0) (x0, x1) = (x1, x0);
        if (y1 < y0) (y0, y1) = (y1, y0);
        x0 = Math.Max(0, x0);
        y0 = Math.Max(0, y0);
        x1 = Math.Min(Width - 1, x1);
        y1 = Math.Min(Height - 1, y1);

        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                SetPixel(x, y, value);
    }

    /// <summary>Scanline fill of a closed polygon (even-odd rule).</summary>
    public void FillPolygon(IReadOnlyList<(double X, double Y)> pts, byte value)
    {
        if (pts.Count < 3)
            return;

        double minY = pts.Min(p => p.Y);
        double maxY = pts.Max(p => p.Y);
        int y0 = Math.Max(0, (int)Math.Floor(minY));
        int y1 = Math.Min(Height - 1, (int)Math.Ceiling(maxY));

        var xs = new List<double>();
        for (int y = y0; y <= y1; y++)
        {
            xs.Clear();
            double sy = y + 0.5;
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                if ((a.Y <= sy && b.Y > sy) || (b.Y <= sy && a.Y > sy))
                    xs.Add(a.X + (sy - a.Y) / (b.Y - a.Y) * (b.X - a.X));
            }

            if (xs.Count < 2)
                continue;

            xs.Sort();
            for (int i = 0; i + 1 < xs.Count; i += 2)
            {
                int xa = Math.Max(0, (int)Math.Ceiling(xs[i] - 0.5));
                int xb = Math.Min(Width - 1, (int)Math.Floor(xs[i + 1] - 0.5));
                for (int x = xa; x <= xb; x++)
                    SetPixel(x, y, value);
            }
        }
    }

    /// <summary>
    /// 4-connected flood fill over pixels sharing the seed's label. Bounded by
    /// <paramref name="maxPixels"/> so a stray click on a large uniform region cannot hang the UI.
    /// </summary>
    public int FloodFill(int sx, int sy, byte value, int maxPixels = 8_000_000)
    {
        if ((uint)sx >= (uint)Width || (uint)sy >= (uint)Height)
            return 0;

        byte target = GetPixel(sx, sy);
        if (target == value)
            return 0;

        var stack = new Stack<(int X, int Y)>();
        stack.Push((sx, sy));
        int painted = 0;
        var seen = new HashSet<long>();

        while (stack.Count > 0 && painted < maxPixels)
        {
            var (x, y) = stack.Pop();
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
                continue;

            long k = ((long)y << 32) | (uint)x;
            if (!seen.Add(k))
                continue;
            if (GetPixel(x, y) != target)
                continue;

            SetPixel(x, y, value);
            painted++;

            stack.Push((x + 1, y));
            stack.Push((x - 1, y));
            stack.Push((x, y + 1));
            stack.Push((x, y - 1));
        }

        return painted;
    }

    /// <summary>
    /// Writes <paramref name="value"/> wherever <paramref name="mask"/> is non-zero, tile by tile.
    ///
    /// This exists because <see cref="SetPixel"/> is the wrong shape for filling a region: it does
    /// a tile lookup, an undo probe and a set insert per pixel, which for a 1024×1024 region costs
    /// more wall-clock than the segmentation that produced it. Here each touched tile is fetched,
    /// snapshotted into the undo record and written once.
    ///
    /// Existing labels in the region <em>are</em> overwritten — this is a deliberate, previewed,
    /// undoable edit by the user, not an automatic fill.
    /// </summary>
    public int ApplyMask(byte[] mask, int x, int y, int w, int h, byte value)
    {
        if (w <= 0 || h <= 0 || mask.Length < (long)w * h)
            return 0;

        int tx0 = Math.Max(0, x / TileSize);
        int ty0 = Math.Max(0, y / TileSize);
        int tx1 = Math.Min(TilesX - 1, (x + w - 1) / TileSize);
        int ty1 = Math.Min(TilesY - 1, (y + h - 1) / TileSize);
        int painted = 0;

        for (int ty = ty0; ty <= ty1; ty++)
        {
            for (int tx = tx0; tx <= tx1; tx++)
            {
                int rx0 = Math.Max(x, tx * TileSize);
                int ry0 = Math.Max(y, ty * TileSize);
                int rx1 = Math.Min(x + w, Math.Min(Width, (tx + 1) * TileSize));
                int ry1 = Math.Min(y + h, Math.Min(Height, (ty + 1) * TileSize));
                if (rx1 <= rx0 || ry1 <= ry0)
                    continue;

                if (!MaskHasSelection(mask, w, x, y, rx0, ry0, rx1, ry1))
                    continue;

                long k = Key(tx, ty);
                byte[] tile = GetOrCreateTile(tx, ty);

                if (_recording is not null && !_recording.ContainsKey(k))
                    _recording[k] = (byte[])tile.Clone();

                for (int gy = ry0; gy < ry1; gy++)
                {
                    int maskRow = (gy - y) * w - x;
                    int tileRow = (gy - ty * TileSize) * TileSize - tx * TileSize;
                    for (int gx = rx0; gx < rx1; gx++)
                    {
                        if (mask[maskRow + gx] == 0)
                            continue;

                        tile[tileRow + gx] = value;
                        painted++;
                    }
                }

                _dirty.Add(k);
            }
        }

        if (painted > 0)
        {
            // One reset for the whole region rather than per pixel; see the pixel-cache notes above.
            _cachedKey = long.MinValue;
            _cachedTile = null;
        }

        return painted;
    }

    private static bool MaskHasSelection(
        byte[] mask, int w, int x, int y, int rx0, int ry0, int rx1, int ry1)
    {
        for (int gy = ry0; gy < ry1; gy++)
        {
            int row = (gy - y) * w - x;
            for (int gx = rx0; gx < rx1; gx++)
                if (mask[row + gx] != 0)
                    return true;
        }

        return false;
    }

    // ------------------------------------------------------------------ undo support

    private Dictionary<long, byte[]>? _recording;

    /// <summary>Starts capturing the "before" image of every tile the next edits touch.</summary>
    public void BeginUndoRecord() => _recording = new Dictionary<long, byte[]>();

    /// <summary>
    /// Stops capturing and returns one entry per touched tile holding both the before and
    /// after images. Null when no recording was active.
    /// </summary>
    public List<(long Key, byte[] Before, byte[] After)>? EndUndoRecord()
    {
        Dictionary<long, byte[]>? rec = _recording;
        _recording = null;
        if (rec is null)
            return null;

        var list = new List<(long, byte[], byte[])>(rec.Count);
        foreach (var (k, before) in rec)
        {
            if (_tiles.TryGetValue(k, out byte[]? t))
                list.Add((k, before, (byte[])t.Clone()));
        }

        return list;
    }

    /// <summary>Replays an undo entry forwards (redo) or backwards (undo).</summary>
    public void ApplyUndoTiles(List<(long Key, byte[] Before, byte[] After)> tiles, bool forward)
    {
        foreach (var (k, before, after) in tiles)
        {
            byte[] src = forward ? after : before;
            if (_tiles.TryGetValue(k, out byte[]? cur) && cur.Length == src.Length)
                Array.Copy(src, cur, cur.Length);
            else
                _tiles[k] = (byte[])src.Clone();

            _dirty.Add(k);
        }

        _cachedKey = long.MinValue;
        _cachedTile = null;
    }

    /// <summary>Copies a raster rectangle out, for the undo stack. Clamped to the raster.</summary>
    public (byte[] Data, int W, int H, int X, int Y) SnapshotRect(int x, int y, int w, int h)
    {
        x = Math.Max(0, Math.Min(x, Width - 1));
        y = Math.Max(0, Math.Min(y, Height - 1));
        w = Math.Max(1, Math.Min(w, Width - x));
        h = Math.Max(1, Math.Min(h, Height - y));

        var data = new byte[w * h];
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
                data[j * w + i] = GetPixel(x + i, y + j);

        return (data, w, h, x, y);
    }

    public void RestoreRect(byte[] data, int w, int h, int x, int y)
    {
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
                SetPixel(x + i, y + j, data[j * w + i]);
    }

    // ------------------------------------------------------------------ persistence

    /// <summary>Writes every modified tile back to disk. Returns the tile count written.</summary>
    public int SaveDirty()
    {
        int n = 0;
        foreach (long k in _dirty.ToArray())
        {
            if (!_tiles.TryGetValue(k, out byte[]? data))
                continue;

            int tx = (int)(k >> 32);
            int ty = (int)(uint)k;
            WriteTile(tx, ty, data);
            n++;
        }

        _dirty.Clear();
        return n;
    }

    public void SaveAll()
    {
        foreach (var (k, data) in _tiles)
            WriteTile((int)(k >> 32), (int)(uint)k, data);
        _dirty.Clear();
    }

    /// <summary>
    /// Empties the label plane, on disk as well as in memory.
    ///
    /// Not undoable on purpose: a snapshot of the whole plane can be hundreds of megabytes, and the
    /// caller shows a confirmation before calling this. Used by the vector import, which replaces
    /// the annotations wholesale after they have been edited in a GIS.
    /// </summary>
    public void ClearAll()
    {
        _tiles.Clear();
        _dirty.Clear();

        // Rebuilding the pixel cache means the next pass re-checks the filesystem, which is what we
        // want now that the files are gone — otherwise stale "this tile exists" answers would linger.
        _knownMissing.Clear();
        _cachedKey = long.MinValue;
        _cachedTile = null;

        if (!Directory.Exists(TilesDir))
            return;

        foreach (string file in Directory.EnumerateFiles(TilesDir, "*.png"))
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // Locked or read-only: leaving it is survivable, the next save overwrites it.
            }
        }
    }

    private void WriteTile(int tx, int ty, byte[] data)
    {
        // Tiles are exported at exactly TileSize² (padding = ignore) so the mask grid
        // matches the image tiles the exporter writes alongside them.
        using var m = Mat.FromPixelData(TileSize, TileSize, MatType.CV_8UC1, data);
        Cv2.ImWrite(TilePath(tx, ty), m);
    }

    /// <summary>Renders the whole label plane, as one ignore-filled raster, for mask-stack viewing.</summary>
    public byte[]? RenderFull()
    {
        if ((long)Width * Height > 64_000_000)
            return null; // refuse to build a >64 MP buffer; use ExportFullGeoTiff instead

        var full = new byte[(long)Width * Height];
        Array.Fill(full, Classes.Ignore);
        foreach (var (tx, ty) in LoadedTiles)
        {
            byte[] t = GetOrCreateTile(tx, ty);
            for (int j = 0; j < TileSize; j++)
            {
                int gy = ty * TileSize + j;
                if (gy >= Height)
                    break;
                int gx = tx * TileSize;
                int n = Math.Min(TileSize, Width - gx);
                Array.Copy(t, j * TileSize, full, (long)gy * Width + gx, n);
            }
        }

        return full;
    }
}
