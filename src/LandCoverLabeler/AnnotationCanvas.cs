using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using LandCover.Core;

namespace LandCoverLabeler;

/// <summary>
/// The drawing surface: source imagery underneath, a translucent class overlay on top,
/// with brush / rectangle / polygon / flood-fill tools and a magnifier loupe.
///
/// Rendering is region-cached. A "cache" is a bitmap pair (imagery + class overlay)
/// covering a raster rectangle a little larger than the viewport, drawn at exactly the
/// current zoom. Panning inside that rectangle costs two blits and no I/O at all; only
/// leaving it, or changing zoom, rebuilds — which is what keeps a 400 MP orthophoto
/// responsive. Brush strokes touch neither: they repaint just the affected overlay rect.
/// </summary>
public sealed class AnnotationCanvas : Control
{
    public enum ToolKind { Brush, Rectangle, Polygon, FloodFill, Picker, Segment }

    /// <summary>
    /// One interactive-segmentation job. <paramref name="JobId"/> rises with every click so a slow
    /// result that arrives after a newer click can be recognised and dropped.
    /// </summary>
    /// <param name="Box">
    /// The user-drawn bounding box in the request window's own pixel space (null when the window
    /// came from the 范围 slider). It is what a promptable model wants as its box prompt; the
    /// classical backend ignores it, since the window already clips the job.
    /// </param>
    public sealed record SegmentRequest(
        int JobId,
        int X,
        int Y,
        int Width,
        int Height,
        byte ClassId,
        int Tolerance,
        IReadOnlyList<SeedPoint> Seeds,
        Rectangle? Box = null);

    private RasterSource? _raster;
    private LabelStore? _labels;

    // --- view transform: screen = (raster - view) * scale ---
    private double _viewX;
    private double _viewY;
    private double _scale = 1.0;

    // --- whole-raster preview, used while zoomed out ---
    private Bitmap? _previewBmp;
    private double _previewPxPerRaster = 1.0;

    // --- region cache (imagery + overlay), sized to the viewport plus a margin ---
    private Bitmap? _cacheBase;
    private Bitmap? _cacheLabel;
    private double _cacheScale;
    private RectangleF _cacheRect;
    private bool _cacheValid;

    /// <summary>How many times the region cache has been rebuilt. Used by the self-test.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CacheRebuildCount { get; private set; }

    // --- magnifier ---
    private Bitmap? _magBmp;
    private int _magCenterX = int.MinValue;
    private int _magCenterY = int.MinValue;
    private bool _magHasCursor;
    private bool _magDirty = true;

    // --- tool state ---
    private bool _panning;
    private Point _panAnchor;
    private double _panViewX, _panViewY;
    private bool _painting;
    private PointF _lastRaster;
    private Point _rectStart = Point.Empty;
    private Point _rectNow = Point.Empty;
    private bool _rectActive;
    private readonly List<PointF> _polygon = [];
    private bool _undoOpen;
    private Rectangle _lastCursorRect = Rectangle.Empty;

    // --- interactive segmentation (ToolKind.Segment) ---
    private readonly List<SeedPoint> _seeds = [];
    private Rectangle _segmentRoi = Rectangle.Empty;

    /// <summary>User-drawn bounding box in raster pixels; null means "use the 范围 square".</summary>
    private Rectangle? _segmentBox;

    /// <summary>
    /// Order the prompts went in — true for a box, false for a point — so Z can step back through
    /// them. Points alone are not enough: a box can land between two clicks and "undo the last
    /// thing I did" has to mean the box then, not a point the user is still happy with.
    /// </summary>
    private readonly List<bool> _promptHistory = [];

    // Left-drag draws a box; a short drag is still a click. Both live in screen space until
    // MouseUp, because a raster rect cannot be built until the drag ends.
    private bool _boxDrag;
    private bool _boxDragMoved;
    private Point _boxDragStart;
    private Point _boxDragNow;

    private InteractiveResult? _pendingSegment;
    private Bitmap? _pendingSegmentBmp;

    // The class is captured with the preview so that changing the palette selection afterwards
    // cannot make 回车 write something other than what the user was looking at.
    private byte _pendingSegmentClass;

    public AnnotationCanvas()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
            true);
        BackColor = Color.FromArgb(32, 32, 32);
        TabStop = true;
    }

    // These are driven from code, never persisted by the WinForms designer.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ToolKind Tool
    {
        get => _tool;
        set
        {
            if (_tool == value)
                return;

            _tool = value;
            if (value != ToolKind.Segment)
                DiscardSegment();

            Invalidate();
        }
    }

    private ToolKind _tool = ToolKind.Brush;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public byte PaintValue { get; set; } = 1;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int BrushSize { get; set; } = 24;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowLabels { get; set; } = true;

    /// <summary>Loupe showing the imagery around the cursor at a fixed magnification.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowMagnifier { get; set; } = true;

    public RasterSource? Raster => _raster;

    /// <summary>Screen pixels per raster pixel. (Control already defines Scale(float).)</summary>
    public double ViewScale => _scale;

    /// <summary>Fired after any label mutation, so the host can mark the project dirty.</summary>
    public event Action? LabelsChanged;

    /// <summary>Fired with the raster coordinate under the cursor (or null when outside).</summary>
    public event Action<double, double, bool>? CursorMoved;

    /// <summary>Fired when the picker tool reads a class id back.</summary>
    public event Action<int>? Picked;

    private double BrushRadiusRaster => Math.Max(0.5, BrushSize / 2.0 / _scale);

    // ------------------------------------------------------------------ interactive segmentation

    /// <summary>Raised on every click of the segment tool, with the region and clicks so far.</summary>
    public event Action<SegmentRequest>? SegmentRequested;

    /// <summary>Side of the square region a click works in, in raster pixels. The 范围 control drives it.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SegmentRegionSize { get; set; } = 512;

    /// <summary>Per-channel colour tolerance for the growing stage. The 相似度 control drives it.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SegmentTolerance { get; set; } = InteractiveSegmenter.DefaultColorTolerance;

    /// <summary>Rises with every click; the host reports it back with the result.</summary>
    private int _segmentJob;

    public bool HasPendingSegment => _pendingSegment is not null;

    public IReadOnlyList<SeedPoint> SegmentSeeds => _seeds;

    /// <summary>The raster rectangle the current clicks are working in (empty before the first click).</summary>
    public Rectangle SegmentRoi => _segmentRoi;

    /// <summary>
    /// Installs a finished segmentation as a preview. The user still has to accept it, so nothing
    /// has been written to the label store yet.
    /// </summary>
    public void SetSegmentPreview(InteractiveResult result, int jobId)
    {
        // A newer click has already gone out; this result describes a region the user has moved on
        // from, so showing it would be showing them something they did not ask for.
        if (jobId < _segmentJob)
            return;

        ClearPendingSegment();
        _pendingSegment = result;
        _pendingSegmentClass = PaintValue;
        _pendingSegmentBmp = BuildSegmentPreviewBitmap(result);
        Invalidate();
    }

    /// <summary>Writes the preview into the label store as a single undo step. Returns pixels written.</summary>
    public int ApplySegment()
    {
        if (_pendingSegment is null || _labels is null || _segmentRoi.Width <= 0)
            return 0;

        InteractiveResult result = _pendingSegment;
        Rectangle roi = _segmentRoi;

        // Its own undo entry: one Ctrl+Z takes the whole region back.
        BeginUndo();
        int painted = _labels.ApplyMask(result.Foreground, roi.X, roi.Y, result.Width, result.Height,
            _pendingSegmentClass);
        EndUndo(roi);

        LabelsChanged?.Invoke();
        DiscardSegment();

        // Only the region is stale — rebuilding the whole view would throw away the render cache.
        InvalidateLabelsRect(roi.X, roi.Y, roi.Width, roi.Height);
        return painted;
    }

    /// <summary>Throws away the clicks, the box and the preview without writing anything.</summary>
    public void DiscardSegment()
    {
        ClearPendingSegment();
        _seeds.Clear();
        _segmentRoi = Rectangle.Empty;
        _segmentBox = null;
        _promptHistory.Clear();
        _boxDrag = false;
        _boxDragMoved = false;

        // Bumping the job here is what makes an in-flight result harmless: it was computed for a
        // region that no longer exists, so SetSegmentPreview must reject it. Without this, switching
        // tools (or pressing Esc) mid-computation would drop a preview back onto the canvas.
        _segmentJob++;

        Invalidate();
    }

    private void ClearPendingSegment()
    {
        _pendingSegment = null;
        _pendingSegmentBmp?.Dispose();
        _pendingSegmentBmp = null;
    }

    private void AddSeed(PointF raster, bool positive) => AddSegmentSeed(raster.X, raster.Y, positive);

    /// <summary>
    /// Adds a click at a raster coordinate and asks the host to segment. Normally called from the
    /// mouse handler; public so the headless self-test can drive the tool without synthesising
    /// mouse messages.
    /// </summary>
    public void AddSegmentSeed(double rasterX, double rasterY, bool positive)
    {
        if (_labels is null || PaintValue == Classes.Ignore)
            return;

        _seeds.Add(new SeedPoint(rasterX, rasterY, positive));
        _promptHistory.Add(false);
        RaiseSegmentRequest();
    }

    /// <summary>
    /// Installs a user-drawn bounding box (raster pixels). It replaces the 范围 square as the
    /// region to work in, and becomes the model's box prompt — which is the difference between
    /// "something in this 512 square" and "this object". Drawing a new one replaces the old.
    /// </summary>
    public void SetSegmentBox(Rectangle rasterRect)
    {
        if (_labels is null)
            return;

        Rectangle clamped = Rectangle.Intersect(rasterRect, new Rectangle(0, 0, _labels.Width, _labels.Height));
        if (clamped.Width < 4 || clamped.Height < 4)
            return;

        _segmentBox = clamped;
        _promptHistory.Add(true);
        RaiseSegmentRequest();
    }

    /// <summary>The user-drawn bounding box in raster pixels, or null when the 范围 square is in use.</summary>
    public Rectangle? SegmentBox => _segmentBox;

    /// <summary>
    /// Steps back one prompt — a point or the box, whichever went in last — and re-runs with what
    /// is left. Z drives this. The point is that a stray click costs one keystroke instead of
    /// starting the object over (Esc still throws everything away).
    /// </summary>
    public void UndoSegmentPrompt()
    {
        if (_promptHistory.Count == 0)
            return;

        bool wasBox = _promptHistory[^1];
        _promptHistory.RemoveAt(_promptHistory.Count - 1);
        if (wasBox)
            _segmentBox = null;
        else if (_seeds.Count > 0)
            _seeds.RemoveAt(_seeds.Count - 1);

        RaiseSegmentRequest();
    }

    /// <summary>How many prompts are pending, for the host to decide what Z should mean.</summary>
    public int PendingPromptCount => _promptHistory.Count;

    /// <summary>
    /// Works out the region to segment and asks the host to do it. Three ways to get it, in order:
    /// the user's box (padded a little so the object is not glued to the window edge), else the
    /// previous window if all clicks still fall inside it, else the square of
    /// <see cref="SegmentRegionSize"/> centred on the first positive click.
    ///
    /// Holding the window still is load-bearing for the SAM backend: the window's pixels are the
    /// cache key for its image features, so a window that moved on every refinement click would
    /// re-encode the image on every refinement click. It is also just calmer — the box stops
    /// sliding out from under the object as points are added. Clicks outside it are not part of
    /// the job, which is what the drawn box is telling you.
    /// </summary>
    private void RaiseSegmentRequest()
    {
        ClearPendingSegment();

        if (_labels is null)
            return;

        var positives = _seeds.Where(s => s.Positive).ToList();
        if (positives.Count == 0 && _segmentBox is null)
        {
            _segmentRoi = Rectangle.Empty;
            Invalidate();
            return;
        }

        int x, y, w, h;
        if (_segmentBox is { } box)
        {
            // Pad the user's box: an object that touches the window edge confuses both backends,
            // and the box prompt only means anything when there is image around it.
            int pad = Math.Max(24, Math.Min(box.Width, box.Height) / 5);
            x = Math.Max(0, box.X - pad);
            y = Math.Max(0, box.Y - pad);
            w = Math.Min(_labels.Width - x, box.Width + 2 * pad);
            h = Math.Min(_labels.Height - y, box.Height + 2 * pad);
        }
        else if (_segmentRoi.Width > 0
            && positives.All(s => _segmentRoi.Contains((int)s.X, (int)s.Y)))
        {
            x = _segmentRoi.X;
            y = _segmentRoi.Y;
            w = _segmentRoi.Width;
            h = _segmentRoi.Height;
        }
        else
        {
            int side = Math.Clamp(SegmentRegionSize, 64, 4096);
            x = Math.Clamp((int)Math.Round(positives.Average(s => s.X) - side / 2.0), 0,
                Math.Max(0, _labels.Width - side));
            y = Math.Clamp((int)Math.Round(positives.Average(s => s.Y) - side / 2.0), 0,
                Math.Max(0, _labels.Height - side));
            w = Math.Min(side, _labels.Width - x);
            h = Math.Min(side, _labels.Height - y);
        }

        if (w <= 0 || h <= 0)
            return;

        _segmentRoi = new Rectangle(x, y, w, h);

        var local = _seeds
            .Where(s => s.X >= x && s.X < x + w && s.Y >= y && s.Y < y + h)
            .Select(s => new SeedPoint(s.X - x, s.Y - y, s.Positive))
            .ToList();
        if (!local.Any(s => s.Positive))
        {
            // A box with no click in it is still a request the host can act on only if there is
            // something to point at; without a positive point there is no object to ask for.
            Invalidate();
            return;
        }

        Rectangle? boxLocal = _segmentBox is { } b
            ? new Rectangle(b.X - x, b.Y - y, b.Width, b.Height)
            : null;

        _segmentJob++;
        SegmentRequested?.Invoke(new SegmentRequest(
            _segmentJob, x, y, w, h, PaintValue, Math.Clamp(SegmentTolerance, 1, 255), local,
            boxLocal));
        Invalidate();
    }

    /// <summary>
    /// Renders the preview mask for display. Capped at 1024 on the long side: this is a translucent
    /// overlay the user is about to accept or throw away, and a full-resolution 4096² bitmap would
    /// be 64 MB for no visible gain.
    /// </summary>
    private Bitmap BuildSegmentPreviewBitmap(InteractiveResult result)
    {
        const int maxDim = 1024;
        double step = Math.Max(1.0, (double)Math.Max(result.Width, result.Height) / maxDim);
        int bw = Math.Max(1, (int)Math.Round(result.Width / step));
        int bh = Math.Max(1, (int)Math.Round(result.Height / step));

        ClassDef cls = Classes.Get(PaintValue);
        int argb = (SegmentPreviewAlpha << 24) | (cls.R << 16) | (cls.G << 8) | cls.B;

        var bmp = new Bitmap(bw, bh, PixelFormat.Format32bppArgb);
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, bw, bh), ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            var row = new int[bw];
            for (int j = 0; j < bh; j++)
            {
                int sy = Math.Min(result.Height - 1, (int)((j + 0.5) * step));
                int baseIdx = sy * result.Width;
                for (int i = 0; i < bw; i++)
                {
                    int sx = Math.Min(result.Width - 1, (int)((i + 0.5) * step));
                    row[i] = result.Foreground[baseIdx + sx] != 0 ? argb : 0;
                }

                Marshal.Copy(row, 0, bd.Scan0 + j * bd.Stride, bw);
            }
        }
        finally
        {
            bmp.UnlockBits(bd);
        }

        return bmp;
    }

    private const int SegmentPreviewAlpha = 110;

    public void SetSource(RasterSource raster, LabelStore labels)
    {
        _raster = raster;
        _labels = labels;
        _previewBmp?.Dispose();
        _previewBmp = null;
        DiscardSegment();
        DiscardCache();
        DiscardMagnifier();
    }

    /// <summary>
    /// Decodes a whole-raster thumbnail for the zoomed-out path. Blocking — call it from a
    /// worker thread and hand the result back to the UI thread.
    /// </summary>
    public static Bitmap BuildPreview(RasterSource raster, int maxDim = 6144, int maxPixels = 24_000_000)
    {
        // Cap total pixels so a very wide panorama does not blow up the preview buffer.
        // A larger preview than before is deliberate: it keeps the cheap "scale a bitmap"
        // path in use for more of the zoom range, so fewer zoom steps hit the disk.
        double factor = Math.Max(raster.Width, raster.Height) / (double)maxDim;
        while ((raster.Width / factor) * (raster.Height / factor) > maxPixels)
            factor *= 1.25;

        int targetW = Math.Max(1, (int)Math.Round(raster.Width / factor));
        using var preview = raster.ReadBgr(0, 0, raster.Width, raster.Height, targetW,
            Math.Max(1, (int)Math.Round(raster.Height / factor)));
        return BitmapFromBgr(preview);
    }

    /// <summary>Installs a preview built by <see cref="BuildPreview"/> (UI thread).</summary>
    public void SetPreview(Bitmap bmp)
    {
        _previewBmp?.Dispose();
        _previewBmp = bmp;
        if (_raster is not null)
            _previewPxPerRaster = (double)bmp.Width / _raster.Width;
        DiscardCache();
        Invalidate();
    }

    // ------------------------------------------------------------------ coordinates

    public PointF RasterFromScreen(double sx, double sy) =>
        new((float)(_viewX + sx / _scale), (float)(_viewY + sy / _scale));

    public PointF ScreenFromRaster(double rx, double ry) =>
        new((float)((rx - _viewX) * _scale), (float)((ry - _viewY) * _scale));

    /// <summary>Raster rectangle currently visible on the canvas.</summary>
    private RectangleF VisibleRasterRect()
    {
        double w = Width / _scale;
        double h = Height / _scale;
        return new RectangleF((float)_viewX, (float)_viewY, (float)w, (float)h);
    }

    public void FitToWindow()
    {
        if (_raster is null || Width <= 0 || Height <= 0)
            return;

        double sx = (double)Width / _raster.Width;
        double sy = (double)Height / _raster.Height;
        // Never magnify past 1:1 on open — a blown-up blurry view is disorienting.
        _scale = Math.Min(Math.Min(sx, sy) * 0.98, 1.0);
        _viewX = (_raster.Width - Width / _scale) / 2;
        _viewY = (_raster.Height - Height / _scale) / 2;
        InvalidateView();
    }

    /// <summary>Scrolls the view by a screen-space delta (positive = content moves right/down).</summary>
    public void PanByScreen(double dx, double dy)
    {
        _viewX -= dx / _scale;
        _viewY -= dy / _scale;
        ClampView();
        Invalidate();
        InvalidateMagnifier();
    }

    /// <summary>Renders one frame into an arbitrary Graphics. Lets the self-test exercise painting.</summary>
    public void RenderFrame(Graphics g) => OnPaint(new PaintEventArgs(g, ClientRectangle));

    public void SetZoom100AtCenter()
    {
        if (_raster is null)
            return;
        ZoomAt(Width / 2.0, Height / 2.0, 1.0 / _scale);
    }

    public void ZoomToRasterPoint(double rx, double ry, double scale)
    {
        _scale = Math.Clamp(scale, MinScale(), 64);
        _viewX = rx - Width / 2.0 / _scale;
        _viewY = ry - Height / 2.0 / _scale;
        ClampView();
        InvalidateView();
    }

    /// <summary>Centres the view on a raster rectangle, zoomed to fill the canvas.</summary>
    public void ZoomToRect(double x, double y, double w, double h)
    {
        if (_raster is null || w <= 0 || h <= 0)
            return;
        _scale = Math.Clamp(Math.Min(Width / w, Height / h), MinScale(), 64);
        _viewX = x + w / 2 - Width / 2.0 / _scale;
        _viewY = y + h / 2 - Height / 2.0 / _scale;
        ClampView();
        InvalidateView();
    }

    private double MinScale()
    {
        if (_raster is null)
            return 0.01;
        return Math.Min((double)Width / _raster.Width, (double)Height / _raster.Height) * 0.1;
    }

    private void ZoomAt(double sx, double sy, double factor)
    {
        if (_raster is null)
            return;

        PointF before = RasterFromScreen(sx, sy);
        _scale = Math.Clamp(_scale * factor, MinScale(), 64);
        // keep the raster point under the cursor pinned
        _viewX = before.X - sx / _scale;
        _viewY = before.Y - sy / _scale;
        ClampView();
        InvalidateView();
    }

    private void ClampView()
    {
        if (_raster is null)
            return;

        double visW = Width / _scale;
        double visH = Height / _scale;

        // Allow a margin of half a screen so the image edge can be pulled inward.
        _viewX = Math.Clamp(_viewX, -visW * 0.5, _raster.Width - visW * 0.5);
        _viewY = Math.Clamp(_viewY, -visH * 0.5, _raster.Height - visH * 0.5);
    }

    // ------------------------------------------------------------------ region cache

    private void InvalidateView()
    {
        DiscardCache();
        DiscardMagnifier();
        Invalidate();
    }

    private void DiscardCache()
    {
        _cacheValid = false;
    }

    private void DiscardMagnifier()
    {
        _magCenterX = int.MinValue;
        _magCenterY = int.MinValue;
        _magDirty = true;
    }

    /// <summary>
    /// Makes sure the cache covers the viewport (plus a margin, so panning stays free)
    /// at the current zoom. Rebuilds only on a cache miss.
    /// </summary>
    private void EnsureCache()
    {
        RasterSource? raster = _raster;
        if (raster is null || _labels is null || Width <= 0 || Height <= 0)
            return;

        RectangleF vis = VisibleRasterRect();

        // Only the part of the viewport that actually overlaps the raster can be cached —
        // when the image is smaller than the window the viewport runs off the edge, and
        // comparing the raw viewport against the cache would rebuild on every single frame.
        var rasterBounds = new RectangleF(0, 0, raster.Width, raster.Height);
        RectangleF need = RectangleF.Intersect(vis, rasterBounds);

        if (_cacheValid
            && _cacheScale == _scale
            && (need.Width <= 0 || need.Height <= 0
                || (need.Left >= _cacheRect.Left && need.Top >= _cacheRect.Top
                    && need.Right <= _cacheRect.Right && need.Bottom <= _cacheRect.Bottom)))
        {
            return;
        }

        // Read a margin around the viewport. Bigger margin = fewer rebuilds when panning,
        // at the cost of a proportionally larger read; 35% each side is a good trade.
        double mx = vis.Width * 0.35;
        double my = vis.Height * 0.35;
        double x0 = Math.Max(0, vis.Left - mx);
        double y0 = Math.Max(0, vis.Top - my);
        double x1 = Math.Min(raster.Width, vis.Right + mx);
        double y1 = Math.Min(raster.Height, vis.Bottom + my);
        if (x1 <= x0 || y1 <= y0)
            return;

        _cacheRect = RectangleF.FromLTRB((float)x0, (float)y0, (float)x1, (float)y1);
        _cacheScale = _scale;

        int cw = Math.Max(1, (int)Math.Round((x1 - x0) * _scale));
        int ch = Math.Max(1, (int)Math.Round((y1 - y0) * _scale));

        if (_cacheBase is null || _cacheBase.Width != cw || _cacheBase.Height != ch)
        {
            _cacheBase?.Dispose();
            _cacheLabel?.Dispose();
            _cacheBase = new Bitmap(cw, ch, PixelFormat.Format32bppArgb);
            _cacheLabel = new Bitmap(cw, ch, PixelFormat.Format32bppArgb);
        }

        RenderCacheImagery(raster, cw, ch);
        RenderLabelRegion(new Rectangle(0, 0, cw, ch));

        _cacheValid = true;
        CacheRebuildCount++;
    }

    private void RenderCacheImagery(RasterSource raster, int cw, int ch)
    {
        using var g = Graphics.FromImage(_cacheBase!);
        g.Clear(Color.FromArgb(24, 24, 24));

        bool usePreview = _previewBmp is not null && _scale <= _previewPxPerRaster;

        try
        {
            if (usePreview)
            {
                g.InterpolationMode = _previewPxPerRaster / _scale > 2
                    ? System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic
                    : System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(_previewBmp!, new RectangleF(
                    (float)((0 - _cacheRect.X) * _scale), (float)((0 - _cacheRect.Y) * _scale),
                    (float)(raster.Width * _scale), (float)(raster.Height * _scale)));
            }
            else
            {
                int rx = Math.Max(0, (int)Math.Floor(_cacheRect.X));
                int ry = Math.Max(0, (int)Math.Floor(_cacheRect.Y));
                int rw = Math.Min(raster.Width - rx, (int)Math.Ceiling(_cacheRect.Width) + 2);
                int rh = Math.Min(raster.Height - ry, (int)Math.Ceiling(_cacheRect.Height) + 2);
                if (rw <= 0 || rh <= 0)
                    return;

                using var tile = raster.ReadBgr(rx, ry, rw, rh, cw, ch);
                using var bmp = BitmapFromBgr(tile);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImageUnscaled(bmp, (int)Math.Round((rx - _cacheRect.X) * _scale),
                    (int)Math.Round((ry - _cacheRect.Y) * _scale));
            }
        }
        catch (Exception ex)
        {
            g.DrawString($"读取影像失败: {ex.Message}", Font, Brushes.OrangeRed, 8, 8);
        }
    }

    /// <summary>
    /// Paints the class overlay for a rectangle of the cache, sampling the label store once
    /// per cached pixel. Cost is bounded by the cache size rather than by zoom, so it stays
    /// cheap whether the view shows 1 000 or 100 000 raster pixels.
    /// </summary>
    private void RenderLabelRegion(Rectangle rect)
    {
        LabelStore? labels = _labels;
        if (labels is null || _cacheLabel is null)
            return;

        rect.Intersect(new Rectangle(0, 0, _cacheLabel.Width, _cacheLabel.Height));
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        if (!ShowLabels)
        {
            ClearBitmapRegion(_cacheLabel, rect);
            return;
        }

        BitmapData bd = _cacheLabel.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            // bd.Stride is the *bitmap's* row stride even when a sub-rectangle is locked: Scan0
            // points into the middle of a row and that row continues past the rect. So copy one
            // row at a time and stop at rect.Width. A single contiguous Marshal.Copy of
            // stride*Height ints smears zeros into the pixels right of the rect and — when the
            // rect sits on the bottom edge of the cache — runs off the end of the buffer and
            // corrupts the heap, which is what the System.ExecutionEngineException on Enter was.
            var row = new int[rect.Width];
            int[] lut = BuildOverlayLut();

            double invScale = 1.0 / _cacheScale;
            for (int y = 0; y < rect.Height; y++)
            {
                int cy = rect.Top + y;
                int iy = (int)(_cacheRect.Y + (cy + 0.5) * invScale);
                Array.Clear(row);

                if ((uint)iy < (uint)labels.Height)
                {
                    for (int x = 0; x < rect.Width; x++)
                    {
                        int cx = rect.Left + x;
                        int ix = (int)(_cacheRect.X + (cx + 0.5) * invScale);
                        if ((uint)ix >= (uint)labels.Width)
                            continue;

                        byte v = labels.GetPixel(ix, iy);
                        row[x] = v < lut.Length ? lut[v] : 0;
                    }
                }

                Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, rect.Width);
            }
        }
        finally
        {
            _cacheLabel.UnlockBits(bd);
        }
    }

    /// <summary>Repaints only the overlay inside a changed raster rectangle (used while painting).</summary>
    private void InvalidateLabelsRect(int rx, int ry, int rw, int rh)
    {
        if (!_cacheValid || _cacheLabel is null)
        {
            Invalidate();
            return;
        }

        var cacheRect = new Rectangle(
            (int)Math.Floor((rx - _cacheRect.X) * _cacheScale) - 1,
            (int)Math.Floor((ry - _cacheRect.Y) * _cacheScale) - 1,
            (int)Math.Ceiling(rw * _cacheScale) + 2,
            (int)Math.Ceiling(rh * _cacheScale) + 2);

        Rectangle clipped = Rectangle.Intersect(cacheRect, new Rectangle(0, 0, _cacheLabel.Width, _cacheLabel.Height));
        if (clipped.Width > 0 && clipped.Height > 0)
            RenderLabelRegion(clipped);

        var screen = ScreenRectOfCacheRect(cacheRect);
        screen.Inflate(2, 2);
        Invalidate(screen);

        if (_magHasCursor)
        {
            _magDirty = true;
            InvalidateMagnifier();
        }
    }

    /// <summary>Maps a rectangle of cache pixels to the screen.</summary>
    private Rectangle ScreenRectOfCacheRect(Rectangle cacheRect)
    {
        float ox = (float)((_cacheRect.X - _viewX) * _scale);
        float oy = (float)((_cacheRect.Y - _viewY) * _scale);
        return new Rectangle(
            (int)Math.Floor(cacheRect.Left + ox), (int)Math.Floor(cacheRect.Top + oy),
            cacheRect.Width + 1, cacheRect.Height + 1);
    }

    /// <summary>Zeroes a rectangle of an ARGB bitmap, respecting the stride.</summary>
    private static void ClearBitmapRegion(Bitmap bmp, Rectangle rect)
    {
        BitmapData bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var zeros = new int[rect.Width];
            for (int y = 0; y < rect.Height; y++)
                Marshal.Copy(zeros, 0, bd.Scan0 + y * bd.Stride, rect.Width);
        }
        finally
        {
            bmp.UnlockBits(bd);
        }
    }

    private const int OverlayAlpha = 150;

    private static int[] BuildOverlayLut()
    {
        var lut = new int[256];
        for (int i = 0; i < Classes.All.Length; i++)
        {
            ClassDef c = Classes.All[i];
            lut[c.Id] = (OverlayAlpha << 24) | (c.R << 16) | (c.G << 8) | c.B;
        }

        lut[Classes.Ignore] = 0; // unpainted shows the imagery through
        return lut;
    }

    private static Bitmap BitmapFromBgr(OpenCvSharp.Mat bgr)
    {
        OpenCvSharp.Cv2.ImEncode(".bmp", bgr, out byte[] bytes);
        using var ms = new MemoryStream(bytes);
        return new Bitmap(ms);
    }

    // ------------------------------------------------------------------ painting

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_raster is null || _labels is null)
        {
            base.OnPaint(e);
            return;
        }

        EnsureCache();

        if (_cacheBase is not null && _cacheLabel is not null)
        {
            // The cache is drawn at exactly the view zoom, so this is a 1:1 blit of the
            // sub-rectangle that corresponds to the viewport. Rounding the origin keeps it
            // crisp; both layers use the same offset so they stay aligned.
            int srcX = (int)Math.Round((_viewX - _cacheRect.X) * _scale);
            int srcY = (int)Math.Round((_viewY - _cacheRect.Y) * _scale);

            // The view may begin before the raster — the image is smaller than the window, or it
            // is FitToWindow's 2% margin — and then the cache's first pixel is *inside* the
            // viewport. Carry that offset into the destination instead of bailing out: bailing
            // paints nothing at all, which is a black canvas until the user happens to zoom in.
            int dstX = Math.Max(0, -srcX);
            int dstY = Math.Max(0, -srcY);
            int srcLeft = Math.Max(0, srcX);
            int srcTop = Math.Max(0, srcY);
            int w = Math.Min(Width - dstX, _cacheBase.Width - srcLeft);
            int h = Math.Min(Height - dstY, _cacheBase.Height - srcTop);

            if (w > 0 && h > 0)
            {
                var src = new Rectangle(srcLeft, srcTop, w, h);
                var dst = new Rectangle(dstX, dstY, w, h);
                e.Graphics.DrawImage(_cacheBase, dst, src, GraphicsUnit.Pixel);
                if (ShowLabels)
                    e.Graphics.DrawImage(_cacheLabel, dst, src, GraphicsUnit.Pixel);
            }
        }

        DrawSegmentPreview(e.Graphics);
        DrawToolPreview(e.Graphics);
        DrawMagnifier(e.Graphics);
        base.OnPaint(e);
    }

    /// <summary>
    /// Draws the pending segmentation over the committed labels. It deliberately sits outside the
    /// region cache: it comes and goes with clicks, and touching the cache for it would re-read the
    /// raster on every preview — the exact regression the cache exists to prevent.
    /// </summary>
    private void DrawSegmentPreview(Graphics g)
    {
        if (_pendingSegmentBmp is null || _segmentRoi.Width <= 0 || Tool != ToolKind.Segment)
            return;

        PointF topLeft = ScreenFromRaster(_segmentRoi.X, _segmentRoi.Y);
        PointF bottomRight = ScreenFromRaster(_segmentRoi.Right, _segmentRoi.Bottom);
        var dst = Rectangle.FromLTRB(
            (int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y),
            (int)Math.Round(bottomRight.X), (int)Math.Round(bottomRight.Y));

        Rectangle visible = Rectangle.Intersect(dst, ClientRectangle);
        if (visible.Width <= 0 || visible.Height <= 0)
            return;

        var state = g.Save();
        g.SetClip(ClientRectangle);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        g.DrawImage(_pendingSegmentBmp, dst);
        g.Restore(state);
    }

    private void DrawToolPreview(Graphics g)
    {
        DrawSegmentMarkers(g);

        if (_rectActive)
        {
            var p0 = ScreenFromRaster(_rectStart.X, _rectStart.Y);
            var p1 = ScreenFromRaster(_rectNow.X, _rectNow.Y);
            var r = Rectangle.FromLTRB(
                (int)Math.Min(p0.X, p1.X), (int)Math.Min(p0.Y, p1.Y),
                (int)Math.Max(p0.X, p1.X), (int)Math.Max(p0.Y, p1.Y));

            using var pen = new Pen(Color.Yellow, 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            using var fill = new SolidBrush(Color.FromArgb(70, Classes.Get(PaintValue).WinColor));
            g.FillRectangle(fill, r);
            g.DrawRectangle(pen, r);
        }

        if (_polygon.Count > 0)
        {
            using var pen = new Pen(Color.Yellow, 1.5f);
            var pts = _polygon.Select(p => ScreenFromRaster(p.X, p.Y)).ToArray();
            if (pts.Length > 1)
                g.DrawLines(pen, pts);

            foreach (var p in pts)
                g.DrawRectangle(pen, p.X - 2, p.Y - 2, 4, 4);

            Point cursor = PointToClient(Cursor.Position);
            using var pen2 = new Pen(Color.FromArgb(140, Color.Yellow), 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            g.DrawLine(pen2, pts[^1], cursor);
        }

        Rectangle c = CursorRect();
        if (c.Width > 0 && !_panning)
        {
            using var pen = new Pen(Color.White, 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            g.DrawEllipse(pen, c);
        }
    }

    /// <summary>
    /// The region box and the clicks. Showing the box matters: it is the only way to see how far
    /// the current 范围 reaches, and why a click outside it has no effect.
    /// </summary>
    private void DrawSegmentMarkers(Graphics g)
    {
        if (Tool != ToolKind.Segment || _raster is null)
            return;

        // Two nested rectangles and it matters which is which: the orange one is the window that
        // gets encoded (范围 square, or the user's box padded out), the yellow one is the user's
        // own bounding box — the "this object" hint. Drawing only one made the box look arbitrary.
        if (_segmentRoi.Width > 0)
        {
            PointF a = ScreenFromRaster(_segmentRoi.X, _segmentRoi.Y);
            PointF b = ScreenFromRaster(_segmentRoi.Right, _segmentRoi.Bottom);
            var box = Rectangle.FromLTRB((int)a.X, (int)a.Y, (int)b.X, (int)b.Y);

            using var pen = new Pen(Color.FromArgb(220, Color.Orange), 1)
            {
                DashStyle = System.Drawing.Drawing2D.DashStyle.Dash,
            };
            g.DrawRectangle(pen, box);
        }

        if (_segmentBox is { } userBox)
        {
            PointF a = ScreenFromRaster(userBox.X, userBox.Y);
            PointF b = ScreenFromRaster(userBox.Right, userBox.Bottom);
            var box = Rectangle.FromLTRB((int)a.X, (int)a.Y, (int)b.X, (int)b.Y);

            using var pen = new Pen(Color.FromArgb(240, Color.Yellow), 2)
            {
                DashStyle = System.Drawing.Drawing2D.DashStyle.Dash,
            };
            g.DrawRectangle(pen, box);
        }

        if (_boxDrag && _boxDragMoved)
        {
            var r = Rectangle.FromLTRB(
                Math.Min(_boxDragStart.X, _boxDragNow.X), Math.Min(_boxDragStart.Y, _boxDragNow.Y),
                Math.Max(_boxDragStart.X, _boxDragNow.X), Math.Max(_boxDragStart.Y, _boxDragNow.Y));
            using var pen = new Pen(Color.Yellow, 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            g.DrawRectangle(pen, r);
        }

        foreach (SeedPoint seed in _seeds)
        {
            PointF p = ScreenFromRaster(seed.X, seed.Y);
            var dot = new Rectangle((int)p.X - 4, (int)p.Y - 4, 8, 8);
            Color fill = seed.Positive ? Color.Lime : Color.Red;

            using var brush = new SolidBrush(fill);
            using var outline = new Pen(Color.Black, 1);
            g.FillEllipse(brush, dot);
            g.DrawEllipse(outline, dot);

            // A short stroke makes "plus"/"minus" readable at a glance at any zoom.
            using var mark = new Pen(Color.Black, 1.5f);
            g.DrawLine(mark, p.X - 2, p.Y, p.X + 2, p.Y);
            if (seed.Positive)
                g.DrawLine(mark, p.X, p.Y - 2, p.X, p.Y + 2);
        }

        if (HasPendingSegment)
        {
            using var pen = new Pen(Color.FromArgb(230, Color.DeepSkyBlue), 2);
            PointF a = ScreenFromRaster(_segmentRoi.X, _segmentRoi.Y);
            PointF b = ScreenFromRaster(_segmentRoi.Right, _segmentRoi.Bottom);
            g.DrawRectangle(pen, Rectangle.FromLTRB((int)a.X, (int)a.Y, (int)b.X, (int)b.Y));
        }
    }

    /// <summary>Screen rect of the brush footprint under the cursor (empty when not applicable).</summary>
    private Rectangle CursorRect()
    {
        if (Tool is not (ToolKind.Brush or ToolKind.Rectangle or ToolKind.FloodFill))
            return Rectangle.Empty;

        Point c = PointToClient(Cursor.Position);
        if (!ClientRectangle.Contains(c))
            return Rectangle.Empty;

        int r = Math.Max(2, (int)(BrushSize / 2.0));
        return new Rectangle(c.X - r, c.Y - r, r * 2, r * 2);
    }

    /// <summary>Redraws only the small region the brush cursor occupies, old and new.</summary>
    private void InvalidateCursorTrail()
    {
        Rectangle c = CursorRect();
        Rectangle region = c.Width > 0 ? c : ClientRectangle;
        if (_lastCursorRect.Width > 0)
            region = Rectangle.Union(region, _lastCursorRect);
        region.Inflate(2, 2);
        _lastCursorRect = c;

        if (region.Width > ClientRectangle.Width || region.Height > ClientRectangle.Height)
        {
            Invalidate();
            return;
        }

        Invalidate(region);
    }

    // ------------------------------------------------------------------ magnifier

    /// <summary>Magnification of the loupe relative to the current view zoom.</summary>
    private double MagnifierScale()
    {
        // When zoomed out, 1:1 is already a big magnification; when zoomed in, go further.
        // Capped at 6x: past that you are only blowing up individual pixels.
        return Math.Clamp(_scale * 4.0, 1.0, 6.0);
    }

    private void InvalidateMagnifier()
    {
        if (!ShowMagnifier || !_magHasCursor)
            return;
        Invalidate(MagnifierRect());
    }

    /// <summary>Redraws the loupe after the tool or brush size changed.</summary>
    public void RefreshMagnifier()
    {
        _magDirty = true;
        InvalidateMagnifier();
    }

    /// <summary>The loupe parks in whichever corner is furthest from the cursor.</summary>
    private Rectangle MagnifierRect()
    {
        const int size = 200;
        const int margin = 14;
        Point c = PointToClient(Cursor.Position);

        bool right = c.X < Width / 2;
        bool bottom = c.Y < Height / 2;

        int x = right ? Width - size - margin : margin;
        int y = bottom ? Height - size - margin : margin;
        return new Rectangle(x, y, size, size);
    }

    private void DrawMagnifier(Graphics g)
    {
        if (!ShowMagnifier || !_magHasCursor || _raster is null || _labels is null)
            return;

        Rectangle box = MagnifierRect();
        if (box.Width < 40 || box.Height < 40 || !ClientRectangle.Contains(box))
            return;

        if (_magBmp is null || _magBmp.Width != box.Width || _magBmp.Height != box.Height)
        {
            _magBmp?.Dispose();
            _magBmp = new Bitmap(box.Width, box.Height, PixelFormat.Format32bppArgb);
            _magDirty = true;
        }

        if (_magDirty)
        {
            RebuildMagnifier(box);
            _magDirty = false;
        }

        using var border = new Pen(Color.FromArgb(200, 255, 255, 255), 1);
        g.DrawImageUnscaled(_magBmp!, box.Location);
        g.DrawRectangle(border, box);

        using var font = new Font("Microsoft YaHei UI", 7.5f);
        string caption = $"放大镜 {MagnifierScale():0.#}x";
        SizeF sz = g.MeasureString(caption, font);
        var capRect = new RectangleF(box.X + 4, box.Bottom - sz.Height - 2, sz.Width + 6, sz.Height);
        using var bg = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
        g.FillRectangle(bg, capRect);
        g.DrawString(caption, font, Brushes.White, capRect.X + 3, capRect.Y + 1);
    }

    /// <summary>
    /// Re-reads the small raster window under the cursor plus its labels. Runs only when the
    /// cursor actually moved a raster pixel or the labels changed, so an idle repaint is free.
    /// </summary>
    private void RebuildMagnifier(Rectangle box)
    {
        RasterSource? raster = _raster;
        LabelStore? labels = _labels;
        if (raster is null || labels is null || _magBmp is null)
            return;

        double mag = MagnifierScale();
        int span = Math.Max(4, (int)Math.Round(box.Width / mag));
        int x0 = _magCenterX - span / 2;
        int y0 = _magCenterY - span / 2;

        try
        {
            using var mg = Graphics.FromImage(_magBmp);
            mg.Clear(Color.FromArgb(20, 20, 20));
            mg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            mg.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

            using (var tile = raster.ReadBgr(x0, y0, span, span, box.Width, box.Height))
            using (var bmp = BitmapFromBgr(tile))
                mg.DrawImageUnscaled(bmp, 0, 0);

            if (ShowLabels)
            {
                using Bitmap labelLayer = BuildMagnifierLabelBitmap(labels, x0, y0, span);
                mg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                mg.DrawImage(labelLayer, new Rectangle(0, 0, box.Width, box.Height));
            }

            DrawMagnifierCrosshair(mg, box.Width, box.Height);
        }
        catch
        {
            // a failed loupe read must never break painting
        }
    }

    /// <summary>Renders the class overlay for the loupe window at 1:1, then it gets scaled up.</summary>
    private static Bitmap BuildMagnifierLabelBitmap(LabelStore labels, int x0, int y0, int span)
    {
        var bmp = new Bitmap(span, span, PixelFormat.Format32bppArgb);
        int[] lut = BuildOverlayLut();
        int[] row = new int[span];

        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, span, span), ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            for (int j = 0; j < span; j++)
            {
                int iy = y0 + j;
                if ((uint)iy >= (uint)labels.Height)
                {
                    Array.Clear(row);
                }
                else
                {
                    for (int i = 0; i < span; i++)
                    {
                        int ix = x0 + i;
                        if ((uint)ix >= (uint)labels.Width)
                        {
                            row[i] = 0;
                            continue;
                        }

                        byte v = labels.GetPixel(ix, iy);
                        row[i] = v < lut.Length ? lut[v] : 0;
                    }
                }

                Marshal.Copy(row, 0, bd.Scan0 + j * bd.Stride, span);
            }
        }
        finally
        {
            bmp.UnlockBits(bd);
        }

        return bmp;
    }

    private void DrawMagnifierCrosshair(Graphics g, int w, int h)
    {
        float sx = w / 2f;
        float sy = h / 2f;

        using var pen = new Pen(Color.FromArgb(220, 255, 230, 0), 1);
        g.DrawLine(pen, sx, 0, sx, h);
        g.DrawLine(pen, 0, sy, w, sy);

        double brushRaster = BrushRadiusRaster;
        float br = (float)(brushRaster * MagnifierScale());
        if (br > 1 && br < w / 2f)
            g.DrawEllipse(pen, sx - br, sy - br, br * 2, br * 2);
    }

    // ------------------------------------------------------------------ input

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        // Zooming mid-stroke would move the canvas out from under the brush.
        if (_painting)
            return;

        ZoomAt(e.X, e.Y, e.Delta > 0 ? 1.2 : 1 / 1.2);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        if (_raster is null || _labels is null)
            return;

        bool wantPan = e.Button == MouseButtons.Middle
                       || (e.Button == MouseButtons.Left && ModifierKeys.HasFlag(Keys.Space));

        if (wantPan)
        {
            _panning = true;
            _panAnchor = e.Location;
            _panViewX = _viewX;
            _panViewY = _viewY;
            Cursor = Cursors.SizeAll;
            return;
        }

        PointF rp = RasterFromScreen(e.X, e.Y);

        if (e.Button == MouseButtons.Right)
        {
            // On the segment tool the right button means "not this"; anywhere else it cancels the
            // polygon being drawn.
            if (Tool == ToolKind.Segment)
            {
                AddSeed(rp, positive: false);
                return;
            }

            if (_polygon.Count > 0)
            {
                _polygon.Clear();
                Invalidate();
            }

            return;
        }

        if (e.Button != MouseButtons.Left)
            return;

        switch (Tool)
        {
            case ToolKind.Picker:
            {
                int id = _labels.GetPixel((int)rp.X, (int)rp.Y);
                if (id != Classes.Ignore)
                    Picked?.Invoke(id);
                break;
            }

            case ToolKind.Brush:
                BeginUndo();
                _painting = true;
                _lastRaster = rp;
                StampAt(rp, rp);
                break;

            case ToolKind.Rectangle:
                _rectActive = true;
                _rectStart = e.Location;
                _rectNow = e.Location;
                Invalidate();
                break;

            case ToolKind.Polygon:
                _polygon.Add(rp);
                Invalidate();
                break;

            case ToolKind.FloodFill:
            {
                BeginUndo();
                int n = _labels.FloodFill((int)rp.X, (int)rp.Y, PaintValue);
                EndUndo(new Rectangle((int)rp.X - 1, (int)rp.Y - 1,
                    Math.Max(1, _labels.Width), Math.Max(1, _labels.Height)));
                if (n > 0)
                {
                    LabelsChanged?.Invoke();
                    InvalidateView();
                }

                break;
            }

            case ToolKind.Segment:
                // Not a seed yet: a left-drag is a bounding box, and only MouseUp knows which this
                // was. Adding the seed here would mean undoing it again when the drag turns out to
                // be a box — and a box drag already costs the user a deliberate gesture.
                _boxDrag = true;
                _boxDragMoved = false;
                _boxDragStart = e.Location;
                _boxDragNow = e.Location;
                break;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_panning)
        {
            _viewX = _panViewX - (e.X - _panAnchor.X) / _scale;
            _viewY = _panViewY - (e.Y - _panAnchor.Y) / _scale;
            ClampView();
            // No DiscardCache here: panning inside the cached margin is just a blit.
            Invalidate();
            InvalidateMagnifier();
            return;
        }

        if (_raster is null || _labels is null)
            return;

        PointF rp = RasterFromScreen(e.X, e.Y);
        CursorMoved?.Invoke(rp.X, rp.Y, true);

        int ncx = (int)rp.X;
        int ncy = (int)rp.Y;
        if (ncx != _magCenterX || ncy != _magCenterY)
        {
            _magCenterX = ncx;
            _magCenterY = ncy;
            _magHasCursor = true;
            _magDirty = true;
            InvalidateMagnifier();
        }

        if (_boxDrag)
        {
            // Left button is down in the segment tool: either a click (a positive point) or a box.
            // A few pixels of slop, so a slightly shaky click still counts as a click.
            _boxDragNow = e.Location;
            if (!_boxDragMoved
                && (Math.Abs(e.X - _boxDragStart.X) > 5 || Math.Abs(e.Y - _boxDragStart.Y) > 5))
                _boxDragMoved = true;
            if (_boxDragMoved)
                Invalidate();
            return;
        }

        if (_painting)
        {
            StampAt(_lastRaster, rp);
            _lastRaster = rp;
            InvalidateCursorTrail();
            return;
        }

        if (_rectActive)
        {
            _rectNow = e.Location;
            Invalidate();
            return;
        }

        if (_polygon.Count > 0)
        {
            Invalidate();
            return;
        }

        InvalidateCursorTrail();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (_panning)
        {
            _panning = false;
            Cursor = Cursors.Default;
            return;
        }

        if (_boxDrag)
        {
            _boxDrag = false;
            if (_boxDragMoved)
            {
                _boxDragMoved = false;
                PointF a = RasterFromScreen(_boxDragStart.X, _boxDragStart.Y);
                PointF b = RasterFromScreen(e.X, e.Y);
                int x0 = (int)Math.Floor(Math.Min(a.X, b.X));
                int y0 = (int)Math.Floor(Math.Min(a.Y, b.Y));
                int x1 = (int)Math.Ceiling(Math.Max(a.X, b.X));
                int y1 = (int)Math.Ceiling(Math.Max(a.Y, b.Y));
                SetSegmentBox(new Rectangle(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0)));
            }
            else
            {
                AddSeed(RasterFromScreen(e.X, e.Y), positive: true);
            }

            return;
        }

        if (_painting)
        {
            _painting = false;
            EndUndo(RectAroundStroke());
            LabelsChanged?.Invoke();
        }

        if (_rectActive)
        {
            _rectActive = false;
            PointF a = RasterFromScreen(_rectStart.X, _rectStart.Y);
            PointF b = RasterFromScreen(e.X, e.Y);

            using var _ = new WaitCursor();
            BeginUndo();
            int x0 = (int)Math.Floor(Math.Min(a.X, b.X));
            int y0 = (int)Math.Floor(Math.Min(a.Y, b.Y));
            int x1 = (int)Math.Ceiling(Math.Max(a.X, b.X));
            int y1 = (int)Math.Ceiling(Math.Max(a.Y, b.Y));
            _labels!.FillRect(x0, y0, x1, y1, PaintValue);
            EndUndo(new Rectangle(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0)));
            LabelsChanged?.Invoke();
            InvalidateLabelsRect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        CursorMoved?.Invoke(0, 0, false);
        _magHasCursor = false;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (Tool == ToolKind.Segment && (_seeds.Count > 0 || HasPendingSegment))
        {
            if (e.KeyCode == Keys.Enter && HasPendingSegment)
            {
                ApplySegment();
                e.Handled = true;
                return;
            }

            if (e.KeyCode == Keys.Escape)
            {
                DiscardSegment();
                e.Handled = true;
                return;
            }
        }

        if (e.KeyCode == Keys.Enter && _polygon.Count >= 3)
        {
            CommitPolygon();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Escape && _polygon.Count > 0)
        {
            _polygon.Clear();
            Invalidate();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Back && !_painting)
        {
            UndoPerformed?.Invoke();
            e.Handled = true;
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (Tool == ToolKind.Polygon && _polygon.Count >= 3)
            CommitPolygon();
    }

    public void CommitPolygon()
    {
        if (_labels is null || _polygon.Count < 3)
            return;

        using var _ = new WaitCursor();
        BeginUndo();
        _labels.FillPolygon(_polygon.Select(p => ((double)p.X, (double)p.Y)).ToList(), PaintValue);

        float minX = _polygon.Min(p => p.X), maxX = _polygon.Max(p => p.X);
        float minY = _polygon.Min(p => p.Y), maxY = _polygon.Max(p => p.Y);
        _polygon.Clear();
        var rect = new Rectangle((int)minX, (int)minY,
            Math.Max(1, (int)(maxX - minX) + 1), Math.Max(1, (int)(maxY - minY) + 1));
        EndUndo(rect);
        LabelsChanged?.Invoke();
        InvalidateLabelsRect(rect.X, rect.Y, rect.Width, rect.Height);
        Invalidate();
    }

    public bool HasPendingPolygon => _polygon.Count > 0;

    private Rectangle _strokeBounds = Rectangle.Empty;

    private void StampAt(PointF from, PointF to)
    {
        if (_labels is null)
            return;

        double r = BrushRadiusRaster;
        _labels.StrokeLine(from.X, from.Y, to.X, to.Y, r, PaintValue);

        var box = new Rectangle(
            (int)Math.Floor(Math.Min(from.X, to.X) - r - 1),
            (int)Math.Floor(Math.Min(from.Y, to.Y) - r - 1),
            (int)Math.Ceiling(Math.Abs(to.X - from.X) + 2 * r + 2),
            (int)Math.Ceiling(Math.Abs(to.Y - from.Y) + 2 * r + 2));

        _strokeBounds = _strokeBounds.IsEmpty ? box : Rectangle.Union(_strokeBounds, box);
        InvalidateLabelsRect(box.X, box.Y, box.Width, box.Height);
    }

    private Rectangle RectAroundStroke() => _strokeBounds.IsEmpty
        ? new Rectangle(0, 0, 1, 1)
        : _strokeBounds;

    // ------------------------------------------------------------------ undo

    public event Action? UndoPerformed;

    private readonly List<UndoEntry> _undo = [];
    private readonly List<UndoEntry> _redo = [];
    private const int MaxUndo = 40;

    private sealed class UndoEntry
    {
        public required List<(long Key, byte[] Before, byte[] After)> Tiles { get; init; }
        public required Rectangle RasterRect { get; init; }
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    private void BeginUndo()
    {
        _labels?.BeginUndoRecord();
        _undoOpen = true;
        _strokeBounds = Rectangle.Empty;
    }

    private void EndUndo(Rectangle rasterRect)
    {
        if (!_undoOpen || _labels is null)
            return;

        _undoOpen = false;
        var tiles = _labels.EndUndoRecord();
        if (tiles is null || tiles.Count == 0)
            return;

        _undo.Add(new UndoEntry { Tiles = tiles, RasterRect = rasterRect });
        if (_undo.Count > MaxUndo)
            _undo.RemoveAt(0);
        _redo.Clear();
    }

    public void Undo()
    {
        if (_labels is null || _undo.Count == 0)
            return;

        UndoEntry e = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _labels.ApplyUndoTiles(e.Tiles, forward: false);
        _redo.Add(e);
        LabelsChanged?.Invoke();
        InvalidateView();
    }

    public void Redo()
    {
        if (_labels is null || _redo.Count == 0)
            return;

        UndoEntry e = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _labels.ApplyUndoTiles(e.Tiles, forward: true);
        _undo.Add(e);
        LabelsChanged?.Invoke();
        InvalidateView();
    }

    private sealed class WaitCursor : IDisposable
    {
        private readonly Cursor? _prev = Cursor.Current;
        public WaitCursor() => Cursor.Current = Cursors.WaitCursor;
        public void Dispose() => Cursor.Current = _prev;
    }

    // ------------------------------------------------------------------ bulk edits

    /// <summary>
    /// Runs an edit of the label store as a single undo entry. The undo stack is private to the
    /// canvas, so a host that writes labels in bulk — the shapefile import, which touches hundreds
    /// of tiles at once — cannot push an entry of its own.
    /// </summary>
    public int RunAsUndoStep(Func<LabelStore, int> edit, Rectangle affected)
    {
        if (_labels is null)
            return 0;

        BeginUndo();
        int result;
        try
        {
            result = edit(_labels);
        }
        catch
        {
            // Do not leave a recording open: the next stroke would silently merge into it.
            _labels.EndUndoRecord();
            _undoOpen = false;
            throw;
        }

        EndUndo(affected);
        LabelsChanged?.Invoke();
        InvalidateLabelsRect(affected.X, affected.Y, affected.Width, affected.Height);
        return result;
    }

    /// <summary>
    /// Forgets the undo/redo history. Used after the label store is emptied wholesale (import with
    /// "clear first"): the recorded tile snapshots describe a store that no longer exists, and
    /// replaying them would resurrect the old annotations.
    /// </summary>
    public void ResetHistory()
    {
        _undo.Clear();
        _redo.Clear();
        _undoOpen = false;
    }
}
