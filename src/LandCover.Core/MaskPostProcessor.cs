using OpenCvSharp;

namespace LandCover.Core;

/// <summary>
/// Per-class morphology + majority smooth + small-speckle removal, and the binary-mask cleanup
/// the interactive tool uses. Both exist because the same three defects show up in every mask
/// this project touches, whichever model produced it: salt-and-pepper noise, holes inside a solid
/// region, and a boundary that does not sit on the object's edge.
/// </summary>
public static class MaskPostProcessor
{
    public sealed record Options(
        bool Enabled = true,
        int MorphKernel = 3,
        bool Majority3 = true,
        int MinAreaPx = 64,
        bool RoadClose = true,
        bool BuildingClose = true,
        bool WoodlandOpen = true,
        bool WaterOpen = true);

    /// <summary>
    /// Cleanup for a single-class (0/255) mask.
    ///
    /// <see cref="FillHoles"/> and <see cref="SnapEdges"/> are the two that people actually
    /// notice: "a big region with pixel-sized blanks in it" is an enclosed hole, and "the edge is
    /// diffuse" is an upsampled low-resolution mask that has never seen the image's edges.
    /// </summary>
    public sealed record BinaryOptions(
        bool Majority3 = true,
        bool FillHoles = true,
        bool SnapEdges = true,
        int GuideRadius = 3,
        double EdgeThreshold = 0.3);

    public static Mat Process(Mat classMap, Options opt)
    {
        if (!opt.Enabled || classMap.Empty())
            return classMap.Clone();

        var result = classMap.Clone();
        int k = Math.Max(3, opt.MorphKernel | 1);

        MorphClass(result, 2, open: false, close: opt.RoadClose, k);
        MorphClass(result, 1, open: false, close: opt.BuildingClose, k);
        MorphClass(result, 3, open: false, close: true, k);
        MorphClass(result, 4, open: opt.WoodlandOpen, close: false, k);
        MorphClass(result, 5, open: opt.WaterOpen, close: false, k);

        if (opt.Majority3)
        {
            var smoothed = Majority3x3(result);
            result.Dispose();
            result = smoothed;
        }

        if (opt.MinAreaPx > 1)
            RemoveSmallBlobs(result, opt.MinAreaPx);

        return result;
    }

    private static void MorphClass(Mat classMap, int classId, bool open, bool close, int k)
    {
        if (!open && !close)
            return;

        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(k, k));
        using var binary = new Mat();
        Cv2.InRange(classMap, new Scalar(classId), new Scalar(classId), binary);
        if (Cv2.CountNonZero(binary) == 0)
            return;

        using var morphed = new Mat();
        if (open && close)
            Cv2.MorphologyEx(binary, morphed, MorphTypes.Open, kernel, anchor: null, iterations: 1, borderType: BorderTypes.Reflect101);
        else if (open)
            Cv2.MorphologyEx(binary, morphed, MorphTypes.Open, kernel);
        else
            Cv2.MorphologyEx(binary, morphed, MorphTypes.Close, kernel);

        // Second pass close for roads after open is skipped; do close when requested
        if (open && close)
        {
            using var closed = new Mat();
            Cv2.MorphologyEx(morphed, closed, MorphTypes.Close, kernel);
            closed.CopyTo(morphed);
        }

        unsafe
        {
            byte* cls = (byte*)classMap.Data;
            byte* mor = (byte*)morphed.Data;
            int stepC = (int)classMap.Step();
            int stepM = (int)morphed.Step();
            int h = classMap.Rows, w = classMap.Cols;
            for (int y = 0; y < h; y++)
            {
                byte* rc = cls + y * stepC;
                byte* rm = mor + y * stepM;
                for (int x = 0; x < w; x++)
                {
                    if (rm[x] != 0)
                        rc[x] = (byte)classId;
                }
            }
        }
    }

    private static Mat Majority3x3(Mat src)
    {
        int h = src.Rows, w = src.Cols;
        var dst = new Mat(h, w, MatType.CV_8UC1);

        unsafe
        {
            byte* s = (byte*)src.Data;
            byte* d = (byte*)dst.Data;
            int step = (int)src.Step();
            int stepD = (int)dst.Step();

            // Allocate once — stackalloc inside the pixel loop overflows the stack on large images.
            int* hist = stackalloc int[32];

            for (int y = 0; y < h; y++)
            {
                byte* row = d + y * stepD;
                for (int x = 0; x < w; x++)
                {
                    for (int i = 0; i < 32; i++)
                        hist[i] = 0;

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = Math.Clamp(y + dy, 0, h - 1);
                        byte* sr = s + yy * step;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = Math.Clamp(x + dx, 0, w - 1);
                            byte c = sr[xx];
                            if (c < 32)
                                hist[c]++;
                        }
                    }

                    byte best = *(s + y * step + x);
                    if (best >= 32)
                    {
                        // 255 is "ignore" in a class map: smoothing must not invent a class for it.
                        row[x] = best;
                        continue;
                    }

                    int bestN = hist[best];
                    // From 0, not 1: starting at 1 meant a pixel could never *become* class 0, so
                    // a lone foreground speck in a background field survived every pass — which is
                    // the exact noise this filter is here to remove.
                    for (int c = 0; c < 32; c++)
                    {
                        if (hist[c] > bestN)
                        {
                            bestN = hist[c];
                            best = (byte)c;
                        }
                    }

                    row[x] = best;
                }
            }
        }

        return dst;
    }

    private static void RemoveSmallBlobs(Mat classMap, int minArea)
    {
        for (int cls = 1; cls < 16; cls++)
        {
            using var binary = new Mat();
            Cv2.InRange(classMap, new Scalar(cls), new Scalar(cls), binary);
            if (Cv2.CountNonZero(binary) == 0)
                continue;

            Cv2.FindContours(binary, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            using var kill = new Mat(classMap.Size(), MatType.CV_8UC1, Scalar.All(0));
            bool any = false;
            foreach (var c in contours)
            {
                if (Math.Abs(Cv2.ContourArea(c)) < minArea)
                {
                    Cv2.DrawContours(kill, new[] { c }, -1, Scalar.White, thickness: -1);
                    any = true;
                }
            }

            if (!any)
                continue;

            unsafe
            {
                byte* p = (byte*)classMap.Data;
                byte* k = (byte*)kill.Data;
                int step = (int)classMap.Step();
                int stepK = (int)kill.Step();
                int h = classMap.Rows, w = classMap.Cols;
                for (int y = 0; y < h; y++)
                {
                    byte* rc = p + y * step;
                    byte* rk = k + y * stepK;
                    for (int x = 0; x < w; x++)
                    {
                        if (rk[x] != 0 && rc[x] == cls)
                            rc[x] = 0;
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------ binary mask cleanup

    /// <summary>
    /// Cleans a single-class 0/255 mask in place: majority smooth, fill interior holes, snap the
    /// boundary onto the image's edges. <paramref name="guideBgr"/> is the imagery the mask was
    /// cut from; without it the edge step is skipped.
    ///
    /// Only the model's opinion is cleaned here. The hard constraints that belong to the user —
    /// a click is a statement — go on <em>afterwards</em>, by the caller: hole-filling would
    /// otherwise fill a negative click's disk straight back in.
    /// </summary>
    public static void CleanBinary(byte[] mask, int w, int h, Mat? guideBgr = null, BinaryOptions? opt = null)
    {
        opt ??= new BinaryOptions();
        if (mask.Length != w * h || w < 3 || h < 3)
            return;

        // 0/1 internally: Majority3x3 histograms class ids and only looks at ids below 32, so a
        // 0/255 mask would come back all zeros.
        using var bin = Mat.FromPixelData(h, w, MatType.CV_8UC1, mask);
        unsafe
        {
            byte* p = (byte*)bin.Data;
            int step = (int)bin.Step();
            for (int y = 0; y < h; y++)
            {
                byte* row = p + (long)y * step;
                for (int x = 0; x < w; x++)
                    row[x] = (byte)(row[x] != 0 ? 1 : 0);
            }
        }

        if (opt.Majority3)
        {
            using var smoothed = Majority3x3(bin);
            smoothed.CopyTo(bin);
        }

        if (opt.FillHoles)
            FillHoles(bin);

        if (opt.SnapEdges && guideBgr is not null && !guideBgr.Empty()
            && guideBgr.Cols == w && guideBgr.Rows == h)
        {
            SnapEdges(bin, guideBgr, Math.Max(1, opt.GuideRadius), opt.EdgeThreshold);
        }

        // FromPixelData *copies*, so the result has to be written back — and 0/1 becomes 0/255 on
        // the way out, which is what InteractiveResult and the label store expect.
        unsafe
        {
            byte* p = (byte*)bin.Data;
            int step = (int)bin.Step();
            for (int y = 0; y < h; y++)
            {
                byte* row = p + (long)y * step;
                int o = y * w;
                for (int x = 0; x < w; x++)
                    mask[o + x] = (byte)(row[x] != 0 ? 255 : 0);
            }
        }
    }

    /// <summary>
    /// Sets every enclosed background pixel to foreground: anything the border flood cannot reach.
    ///
    /// A morphological close only plugs pinholes a couple of pixels across, which is why "a big
    /// region with scattered blanks in it" survived it. Reaching the hole from the outside is the
    /// definition of a hole, so one flood from a padded border frame answers it for holes of any
    /// size, and never touches the background that really is outside the object.
    /// </summary>
    private static void FillHoles(Mat bin /* 0/1 */)
    {
        int h = bin.Rows, w = bin.Cols;

        // bg = 1 where the mask is background.
        using var bg = new Mat(h, w, MatType.CV_8UC1);
        unsafe
        {
            byte* s = (byte*)bin.Data;
            byte* d = (byte*)bg.Data;
            int stepS = (int)bin.Step();
            int stepD = (int)bg.Step();
            for (int y = 0; y < h; y++)
            {
                byte* rs = s + (long)y * stepS;
                byte* rd = d + (long)y * stepD;
                for (int x = 0; x < w; x++)
                    rd[x] = (byte)(rs[x] == 0 ? 1 : 0);
            }
        }

        // The frame has to carry the *background's* value (1) or the flood stops at the image
        // border and every hole looks enclosed. Seed the frame itself so one call reaches every
        // border-touching background region.
        using var padded = new Mat();
        Cv2.CopyMakeBorder(bg, padded, 1, 1, 1, 1, BorderTypes.Constant, new Scalar(1));
        Cv2.FloodFill(padded, new Point(0, 0), new Scalar(2), out _, new Scalar(0), new Scalar(0));

        // Whatever is still 1 is an enclosed hole.
        unsafe
        {
            byte* s = (byte*)bin.Data;
            byte* d = (byte*)padded.Data;
            int stepS = (int)bin.Step();
            int stepD = (int)padded.Step();
            for (int y = 0; y < h; y++)
            {
                byte* rs = s + (long)y * stepS;
                byte* rd = d + (long)(y + 1) * stepD + 1;
                for (int x = 0; x < w; x++)
                {
                    if (rd[x] == 1)
                        rs[x] = 1;
                }
            }
        }
    }

    /// <summary>
    /// Pulls the boundary onto the image's edges with a guided filter — but only in a thin band
    /// around the current boundary, only where the imagery actually has an edge to snap to, and
    /// never at the cost of the object's core.
    ///
    /// A promptable model hands back a mask upsampled from a much coarser grid, so its edge is a
    /// smooth curve that stops wherever the coarse cell happened to stop, rather than where the
    /// roof stops — "弥散". Running the mask through a guided filter with the imagery as the guide
    /// turns the transition into a local linear function of the image, so re-thresholding at 0.5
    /// lands on the strongest nearby edge. This is the only step here that can do anything about
    /// edges; morphology can only move the blur around.
    ///
    /// The three guards are what make it safe to run on every mask. A guided filter is an
    /// edge-preserving <em>smoothing</em>: on a thin feature with no matching edge in the guide it
    /// will average the feature away. So (1) the eroded core is forced back on, (2) only pixels in
    /// the boundary band may change, and (3) only where the guide's local edge strength passes
    /// <paramref name="edgeThreshold"/> — in a flat field there is nothing to snap to and the
    /// boundary must not move at all.
    /// </summary>
    private static void SnapEdges(Mat bin /* 0/1 */, Mat guideBgr, int radius, double edgeThreshold)
    {
        // Where is there an edge to snap to? Blurred gradient magnitude: a single step survives
        // the blur at ~gain/(2r+1), texture noise does not.
        using var gray8 = new Mat();
        Cv2.CvtColor(guideBgr, gray8, ColorConversionCodes.BGR2GRAY);
        using var gray = new Mat();
        gray8.ConvertTo(gray, MatType.CV_32FC1, 1.0 / 255.0);
        using var gx = new Mat();
        using var gy = new Mat();
        Cv2.Sobel(gray, gx, MatType.CV_32FC1, 1, 0, 3);
        Cv2.Sobel(gray, gy, MatType.CV_32FC1, 0, 1, 3);
        using var mag = new Mat();
        Cv2.Magnitude(gx, gy, mag);
        using var prox = new Mat();
        Cv2.BoxFilter(mag, prox, MatType.CV_32FC1,
            new Size(2 * radius + 1, 2 * radius + 1));
        using var nearEdge = new Mat();
        Cv2.Threshold(prox, nearEdge, edgeThreshold, 1.0, ThresholdTypes.Binary);

        using var soft = new Mat();
        bin.ConvertTo(soft, MatType.CV_32FC1);
        using var guide = new Mat();
        guideBgr.ConvertTo(guide, MatType.CV_32FC3, 1.0 / 255.0);
        using var gf = OpenCvSharp.XImgProc.GuidedFilter.Create(guide, radius, 0.01);
        using var q = new Mat();
        gf.Filter(soft, q, -1);
        using var snappedF = new Mat();
        Cv2.Threshold(q, snappedF, 0.5, 1.0, ThresholdTypes.Binary);
        using var snapped = new Mat();
        snappedF.ConvertTo(snapped, MatType.CV_8UC1);

        // The band is as thick as the boundary is allowed to travel: a 1px ring would make the
        // whole exercise a no-op for an edge that is three pixels off.
        int k = 2 * Math.Max(1, radius) + 1;
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(k, k));
        using var core = new Mat();
        Cv2.Erode(bin, core, kernel);
        using var dilated = new Mat();
        Cv2.Dilate(bin, dilated, kernel);
        using var band = new Mat();
        Cv2.Subtract(dilated, core, band);

        using var nearEdge8 = new Mat();
        nearEdge.ConvertTo(nearEdge8, MatType.CV_8UC1);
        using var movable = new Mat();
        Cv2.BitwiseAnd(band, nearEdge8, movable);

        using var keep = new Mat();
        using var outside = new Mat();
        Cv2.BitwiseNot(movable, outside);
        Cv2.BitwiseAnd(bin, outside, keep);
        using var moved = new Mat();
        Cv2.BitwiseAnd(snapped, movable, moved);
        Cv2.BitwiseOr(keep, moved, bin);
        Cv2.BitwiseOr(bin, core, bin);
    }
}
