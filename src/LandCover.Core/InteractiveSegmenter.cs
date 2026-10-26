using System.Runtime.InteropServices;
using OpenCvSharp;

namespace LandCover.Core;

/// <summary>
/// One click from the interactive tool: a location the user says <em>is</em> the class being
/// outlined (positive) or <em>is not</em> (negative). Coordinates are raster pixels, relative
/// to the region being segmented.
/// </summary>
public readonly record struct SeedPoint(double X, double Y, bool Positive);

/// <summary>Result mask of an interactive segmentation: row-major, one byte per pixel, 0 or 255.</summary>
public sealed record InteractiveResult(
    byte[] Foreground,
    int Width,
    int Height,
    int ForegroundPixels,
    int Components,
    int GrownPixels = 0);

/// <summary>
/// Click-driven segmentation: given a region of imagery and a handful of positive/negative clicks,
/// work out which pixels belong to the object under the positive ones.
///
/// Two stages, and the first is what makes it work:
/// <list type="number">
/// <item>a colour-similarity flood fill grows from each positive click. On orthoimagery the things
/// worth click-selecting are usually flat-coloured (roofs, greenhouse sheeting, water, bare
/// fields), so this recovers the object's bulk in milliseconds.</item>
/// <item>GrabCut refines that estimate. Feeding it the grown region — rather than just the click —
/// is the difference between a usable outline and a blob the size of the cursor: GrabCut learns its
/// colour models from the seeded pixels, and a six-pixel disk teaches it nothing.</item>
/// </list>
///
/// No model, no GPU and no new dependency. A promptable segmentation network (SAM and friends)
/// would mean shipping another ONNX model and running an image encoder on CPU per region — and the
/// remote-sensing benchmarks put zero-shot SAM <em>behind</em> classical methods on exactly this
/// kind of imagery, so it would be cost without benefit.
///
/// The cost is bounded by <see cref="WorkingSize"/>, not by how much ground the user selects: a
/// larger region is segmented at a coarser working resolution and the mask is scaled back up. That
/// keeps every click around a second however far the 范围 control is turned up — which is the
/// whole point of an interactive tool.
///
/// Callers must run this off the UI thread.
/// </summary>
public static class InteractiveSegmenter
{
    /// <summary>Radius of the hard constraint painted at each click, in raster pixels.</summary>
    public const int DefaultSeedRadius = 6;

    /// <summary>Sensible default for "how many GrabCut sweeps"; more is slower, not always better.</summary>
    public const int DefaultIterations = 3;

    /// <summary>
    /// How far a grown pixel's channels may differ from the click it grew from, per channel.
    /// Around 32/255 separates a painted roof or a field from its surroundings without hopping
    /// across a shadow line.
    /// </summary>
    public const int DefaultColorTolerance = 32;

    /// <summary>
    /// Longest side the segmentation maths runs at. GrabCut is the expensive part and its cost is
    /// quadratic, so this is what keeps a big 范围 from turning into a multi-second freeze.
    /// Measured on a 12-core CPU: 512 costs about 0.8 s, and this cap holds the worst case near
    /// 2 s. Edges in the final mask are correspondingly coarser when a bigger region is used.
    /// </summary>
    public const int WorkingSize = 768;

    /// <summary>Regions bigger than this are refused; the mask would be uselessly coarse anyway.</summary>
    public const int MaxRegionPixels = 16_777_216; // 4096 x 4096

    /// <summary>
    /// Segments the foreground of <paramref name="bgr"/> (an 8UC3 region) from the given clicks.
    /// Returns a mask the same size as the input. Throws only on unusable input — a segmentation
    /// that comes back empty is a normal outcome the caller should report to the user.
    /// </summary>
    public static InteractiveResult Segment(
        Mat bgr,
        IReadOnlyList<SeedPoint> seeds,
        int colorTolerance = DefaultColorTolerance,
        int iterations = DefaultIterations,
        int seedRadius = DefaultSeedRadius)
    {
        ArgumentNullException.ThrowIfNull(bgr);
        ArgumentNullException.ThrowIfNull(seeds);

        if (bgr.Empty() || bgr.Channels() != 3)
            throw new ArgumentException($"交互分割需要 8UC3 的影像，收到 {bgr.Channels()} 通道", nameof(bgr));

        int w = bgr.Cols;
        int h = bgr.Rows;
        if (seeds.Count == 0)
            return new InteractiveResult(new byte[w * h], w, h, 0, 0);

        if ((long)w * h > MaxRegionPixels)
            throw new ArgumentException($"区域过大 ({w}x{h})，请把“范围”调小", nameof(bgr));

        double scale = Math.Min(1.0, WorkingSize / (double)Math.Max(w, h));
        int ww = Math.Max(8, (int)Math.Round(w * scale));
        int wh = Math.Max(8, (int)Math.Round(h * scale));

        using Mat? scaled = scale < 1.0
            ? bgr.Resize(new Size(ww, wh), 0, 0, InterpolationFlags.Area)
            : null;
        Mat work = scaled ?? bgr;

        var workSeeds = new SeedPoint[seeds.Count];
        for (int i = 0; i < seeds.Count; i++)
            workSeeds[i] = new SeedPoint(seeds[i].X * scale, seeds[i].Y * scale, seeds[i].Positive);

        int workRadius = Math.Max(2, (int)Math.Round(seedRadius * scale));
        (byte[] workMask, int grownAtWorkSize) = SegmentAtWorkingSize(
            work, workSeeds, Math.Clamp(colorTolerance, 1, 255), iterations, workRadius, ww, wh);

        // The negative click's disk and the "which blob did they actually click" test are far more
        // accurate at full resolution, so both are re-applied after the mask is scaled back up.
        byte[] foreground = ToRasterSize(workMask, ww, wh, w, h);

        // Clean the model's estimate *before* the user's hard constraints go on. Hole-filling after
        // the negative block would fill a negative click's disk straight back in, and a 3x3
        // majority could nibble away the very pixel the user pointed at — both clicks are
        // statements and have to win, so they are re-asserted below over whatever cleanup did.
        MaskPostProcessor.CleanBinary(foreground, w, h, bgr);
        ForcePositiveDisks(foreground, w, h, seeds, seedRadius);

        bool[] blocked = NegativeClickDisks(w, h, seeds, seedRadius);
        for (int i = 0; i < foreground.Length; i++)
            if (blocked[i])
                foreground[i] = 0;

        int components = KeepClicksComponents(foreground, w, h, seeds);

        int painted = 0;
        foreach (byte b in foreground)
            if (b != 0)
                painted++;

        int grown = (int)Math.Round(grownAtWorkSize * (double)w * h / ((double)ww * wh));
        return new InteractiveResult(foreground, w, h, painted, components, grown);
    }

    /// <summary>Region growing plus GrabCut, both at whatever resolution it is handed.</summary>
    private static (byte[] Mask, int Grown) SegmentAtWorkingSize(
        Mat bgr, IReadOnlyList<SeedPoint> seeds, int tolerance, int iterations, int seedRadius, int w, int h)
    {
        bool[] blocked = NegativeClickDisks(w, h, seeds, seedRadius);
        byte[] grown = RegionGrow(bgr, seeds, tolerance, blocked, w, h);

        int grownCount = 0;
        foreach (byte b in grown)
            if (b != 0)
                grownCount++;

        using Mat mask = BuildInitialMask(w, h, seeds, seedRadius, grown);
        using var bgdModel = new Mat();
        using var fgdModel = new Mat();
        // InitWithMask: our BGD/FGD pixels are fixed, PR_FGD/PR_BGD pixels are the algorithm's to decide.
        Cv2.GrabCut(bgr, mask, new Rect(0, 0, w, h), bgdModel, fgdModel,
            Math.Max(1, iterations), GrabCutModes.InitWithMask);

        return (ExtractForeground(mask, w, h, blocked), grownCount);
    }

    private static byte[] ToRasterSize(byte[] workMask, int ww, int wh, int w, int h)
    {
        if (ww == w && wh == h)
            return workMask;

        using var small = Mat.FromPixelData(wh, ww, MatType.CV_8UC1, workMask);
        using var full = new Mat();
        // Nearest, not smooth: the result is a class mask and interpolating it invents labels.
        Cv2.Resize(small, full, new Size(w, h), 0, 0, InterpolationFlags.Nearest);

        var bytes = new byte[(long)w * h];
        int step = (int)full.Step();
        unsafe
        {
            byte* p = (byte*)full.Data;
            for (int y = 0; y < h; y++)
                Marshal.Copy((IntPtr)(p + (long)y * step), bytes, y * w, w);
        }

        return bytes;
    }

    // ------------------------------------------------------------------ stage 1: region growing

    /// <summary>
    /// Flood fill outward from every positive click, accepting 4-connected neighbours whose BGR
    /// channels are all within <paramref name="tolerance"/> of the running average of the region
    /// grown so far.
    ///
    /// Two details are load-bearing on real orthoimagery, both learned the hard way on a
    /// terraced-greenhouse scene where a naive version grew two pixels and stopped:
    /// <list type="bullet">
    /// <item>the fill runs on a slightly blurred copy. At full resolution a single pixel of
    /// specular noise differs from its neighbour by more than any useful tolerance, so growing on
    /// raw pixels stops at the first speckle.</item>
    /// <item>the reference is the region's running mean, not the clicked pixel's colour, so a
    /// painted roof that shades from one edge to the other still grows. A click on a highlight or
    /// a shadow is far too specific a target otherwise. Growth is additionally capped at twice the
    /// tolerance away from the clicked colour, so the mean cannot drift off across a gradient and
    /// swallow the whole region.</item>
    /// </list>
    /// Each positive click grows from its own reference, so two clicks on differently-lit parts of
    /// one roof both contribute.
    /// </summary>
    private static byte[] RegionGrow(
        Mat bgr, IReadOnlyList<SeedPoint> seeds, int tolerance, bool[] blocked, int w, int h)
    {
        using Mat blurred = new();
        Cv2.GaussianBlur(bgr, blurred, new Size(5, 5), 0);
        byte[] pixels = BgrBytes(blurred, w, h);

        var fg = new byte[w * h];
        var queued = new bool[w * h];
        var queue = new Queue<int>();
        int maxDrift = tolerance * 2;

        foreach (SeedPoint s in seeds)
        {
            if (!s.Positive)
                continue;

            int sx = Math.Clamp((int)Math.Round(s.X), 0, w - 1);
            int sy = Math.Clamp((int)Math.Round(s.Y), 0, h - 1);
            int start = sy * w + sx;
            if (blocked[start])
                continue;

            int off = start * 3;
            int seedB = pixels[off], seedG = pixels[off + 1], seedR = pixels[off + 2];
            long sumB = seedB, sumG = seedG, sumR = seedR;
            int count = 1;

            queue.Clear();
            queue.Enqueue(start);
            queued[start] = true;

            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                if (fg[i] != 0 || blocked[i])
                    continue;

                int o = i * 3;
                int blue = pixels[o], green = pixels[o + 1], red = pixels[o + 2];
                int meanB = (int)(sumB / count), meanG = (int)(sumG / count), meanR = (int)(sumR / count);

                if (Math.Abs(blue - meanB) > tolerance
                    || Math.Abs(green - meanG) > tolerance
                    || Math.Abs(red - meanR) > tolerance
                    || Math.Abs(blue - seedB) > maxDrift
                    || Math.Abs(green - seedG) > maxDrift
                    || Math.Abs(red - seedR) > maxDrift)
                {
                    continue;
                }

                fg[i] = 255;
                sumB += blue;
                sumG += green;
                sumR += red;
                count++;

                int x = i % w;
                int y = i / w;
                if (x > 0) Enqueue(i - 1);
                if (x + 1 < w) Enqueue(i + 1);
                if (y > 0) Enqueue(i - w);
                if (y + 1 < h) Enqueue(i + w);
            }

            void Enqueue(int index)
            {
                if (queued[index])
                    return;
                queued[index] = true;
                queue.Enqueue(index);
            }
        }

        return fg;
    }

    /// <summary>Pixels inside a negative click's disk: never grown into, never foreground.
    /// Public because the SAM backend enforces the same convention on its own masks.</summary>
    public static bool[] NegativeClickDisks(int w, int h, IReadOnlyList<SeedPoint> seeds, int radius)
    {
        var blocked = new bool[w * h];
        foreach (SeedPoint s in seeds)
            MarkDisk(blocked, w, h, s, radius, !s.Positive);
        return blocked;
    }

    /// <summary>
    /// Pixels inside a positive click's disk become foreground, whatever the model said. The
    /// mirror of <see cref="NegativeClickDisks"/>: a click is a statement, so cleanup — a 3x3
    /// majority, say — must not be able to nibble away the pixel the user pointed at.
    /// </summary>
    public static void ForcePositiveDisks(byte[] foreground, int w, int h,
        IReadOnlyList<SeedPoint> seeds, int radius)
    {
        var mark = new bool[w * h];
        foreach (SeedPoint s in seeds)
            MarkDisk(mark, w, h, s, radius, s.Positive);
        for (int i = 0; i < foreground.Length; i++)
            if (mark[i])
                foreground[i] = 255;
    }

    private static void MarkDisk(bool[] target, int w, int h, SeedPoint s, int radius, bool value)
    {
        int cx = Math.Clamp((int)Math.Round(s.X), 0, w - 1);
        int cy = Math.Clamp((int)Math.Round(s.Y), 0, h - 1);
        int r2 = radius * radius;

        for (int y = Math.Max(0, cy - radius); y <= Math.Min(h - 1, cy + radius); y++)
        {
            for (int x = Math.Max(0, cx - radius); x <= Math.Min(w - 1, cx + radius); x++)
            {
                int dx = x - cx;
                int dy = y - cy;
                if (dx * dx + dy * dy <= r2)
                    target[y * w + x] = value;
            }
        }
    }

    private static byte[] BgrBytes(Mat bgr, int w, int h)
    {
        var bytes = new byte[(long)w * h * 3];
        int rowBytes = w * 3;
        int step = (int)bgr.Step();

        unsafe
        {
            byte* p = (byte*)bgr.Data;
            for (int y = 0; y < h; y++)
                Marshal.Copy((IntPtr)(p + (long)y * step), bytes, y * rowBytes, rowBytes);
        }

        return bytes;
    }

    // ------------------------------------------------------------------ stage 2: GrabCut

    /// <summary>
    /// Everything starts as "probably background"; the grown region becomes "probably
    /// foreground" and each click a hard constraint. GrabCut needs both a foreground and a
    /// background sample, so with no negative click the region border stands in for "not this
    /// object" — which is also why the object must not run off the edge of the region chosen.
    /// </summary>
    private static Mat BuildInitialMask(
        int w, int h, IReadOnlyList<SeedPoint> seeds, int seedRadius, byte[] grown)
    {
        var mask = new Mat(h, w, MatType.CV_8UC1, new Scalar((int)GrabCutClasses.PR_BGD));

        using (Mat grownMat = Mat.FromPixelData(h, w, MatType.CV_8UC1, grown))
            mask.SetTo(new Scalar((int)GrabCutClasses.PR_FGD), grownMat);

        bool hasNegative = false;
        foreach (SeedPoint s in seeds)
        {
            var centre = new Point(
                Math.Clamp((int)Math.Round(s.X), 0, w - 1),
                Math.Clamp((int)Math.Round(s.Y), 0, h - 1));

            Cv2.Circle(mask, centre, seedRadius,
                new Scalar((int)(s.Positive ? GrabCutClasses.FGD : GrabCutClasses.BGD)), -1);
            hasNegative |= !s.Positive;
        }

        if (!hasNegative)
            Cv2.Rectangle(mask, new Rect(0, 0, w, h), new Scalar((int)GrabCutClasses.BGD),
                Math.Max(2, seedRadius));

        return mask;
    }

    private static byte[] ExtractForeground(Mat mask, int w, int h, bool[] blocked)
    {
        var fg = new byte[w * h];
        byte bgd = (byte)GrabCutClasses.BGD;
        byte prBgd = (byte)GrabCutClasses.PR_BGD;

        unsafe
        {
            byte* p = (byte*)mask.Data;
            int step = (int)mask.Step();
            for (int y = 0; y < h; y++)
            {
                byte* row = p + (long)y * step;
                int o = y * w;
                for (int x = 0; x < w; x++)
                {
                    byte v = row[x];
                    // FGD and PR_FGD both count: PR_FGD is where GrabCut settled on "object"
                    // without being told, and dropping it leaves holes in textured objects.
                    if (v != bgd && v != prBgd)
                        fg[o + x] = 255;
                }
            }
        }

        // A negative click is a statement, not a hint: keep it out even if GrabCut overrules it.
        for (int i = 0; i < fg.Length; i++)
            if (blocked[i])
                fg[i] = 0;

        return fg;
    }

    /// <summary>
    /// Keeps only the blobs the user actually pointed at. GrabCut — and colour growing even more
    /// so — happily returns several disjoint blobs of similar colour; without this, clicking one
    /// roof would also paint the three identical roofs beside it. Returns the number of blobs kept.
    /// Public because the SAM backend enforces the same convention on its own masks.
    /// </summary>
    public static int KeepClicksComponents(byte[] foreground, int w, int h, IReadOnlyList<SeedPoint> seeds)
    {
        using var bin = Mat.FromPixelData(h, w, MatType.CV_8UC1, foreground);
        using var comp = new Mat();
        int count = Cv2.ConnectedComponents(bin, comp, PixelConnectivity.Connectivity8, MatType.CV_32S);
        if (count <= 1)
            return 0;

        var keep = new bool[count];

        foreach (SeedPoint s in seeds)
        {
            if (!s.Positive)
                continue;
            int label = LabelAt(comp, s, w, h);
            if (label > 0)
                keep[label] = true;
        }

        // A positive click can land on a pixel GrabCut decided was background (a shadow across
        // the roof, say). Rather than returning nothing and looking broken, keep the largest
        // blob — the click at least says "something here".
        if (!keep.Any(v => v))
        {
            int best = LargestComponent(comp, count, w, h);
            if (best > 0)
                keep[best] = true;
        }

        int kept = 0;
        for (int i = 1; i < count; i++)
            if (keep[i])
                kept++;

        unsafe
        {
            int* p = (int*)comp.Data;
            int step = (int)comp.Step() / sizeof(int);
            for (int y = 0; y < h; y++)
            {
                int* row = p + (long)y * step;
                int o = y * w;
                for (int x = 0; x < w; x++)
                {
                    int label = row[x];
                    if (label <= 0 || label >= count || !keep[label])
                        foreground[o + x] = 0;
                }
            }
        }

        return kept;
    }

    private static int LabelAt(Mat comp, SeedPoint s, int w, int h)
    {
        int x = Math.Clamp((int)Math.Round(s.X), 0, w - 1);
        int y = Math.Clamp((int)Math.Round(s.Y), 0, h - 1);
        return comp.At<int>(y, x);
    }

    private static int LargestComponent(Mat comp, int count, int w, int h)
    {
        var sizes = new int[count];
        unsafe
        {
            int* p = (int*)comp.Data;
            int step = (int)comp.Step() / sizeof(int);
            for (int y = 0; y < h; y++)
            {
                int* row = p + (long)y * step;
                for (int x = 0; x < w; x++)
                {
                    int label = row[x];
                    if (label > 0 && label < count)
                        sizes[label]++;
                }
            }
        }

        int best = 0;
        for (int i = 1; i < count; i++)
            if (sizes[i] > sizes[best])
                best = i;
        return best;
    }

}
