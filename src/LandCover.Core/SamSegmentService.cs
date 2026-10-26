using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.Json;
using OpenCvSharp;

namespace LandCover.Core;

/// <summary>
/// Talks to <c>train/sam_service.py</c>, the resident process that holds SAM in memory.
///
/// Why a separate process at all: the checkpoints are PyTorch state dicts, which C# cannot load,
/// and loading one costs seconds — far too much to pay per click. So the model lives for the whole
/// session and the GUI only ever sends a prompt and gets a mask back. Everything expensive
/// (image encoding) happens once per window and is cached over there; everything per click
/// (the prompt query) is milliseconds.
///
/// The transport is one JSON object per line on stdin/stdout. stderr is drained on its own
/// thread from the very first byte: a child that fills its stderr pipe blocks, and a parent that
/// is still waiting for stdout never notices. That deadlock is documented in this repo's notes
/// on <c>TrainingService</c>.
/// </summary>
public sealed class SamSegmentService : IDisposable
{
    private readonly Process _proc;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly List<string> _diagnostics = [];
    private readonly object _gate = new();
    private bool _disposed;

    public string Device { get; private set; } = "";
    public string ModelName { get; private set; } = "";
    public string Family { get; private set; } = "";

    /// <summary>Everything the service said on stderr, most recent last. Diagnostic aid only.</summary>
    public IReadOnlyList<string> Diagnostics
    {
        get { lock (_diagnostics) return _diagnostics.ToList(); }
    }

    private SamSegmentService(Process proc, StreamWriter stdin, StreamReader stdout)
    {
        _proc = proc;
        _stdin = stdin;
        _stdout = stdout;
    }

    /// <summary>
    /// Spawns the service and loads a model. Blocking and slow (seconds) — call it from a
    /// worker thread; the GUI starts it at startup so it is warm long before the first click.
    /// A missing interpreter or checkpoint is a normal state, not a crash: the caller falls back
    /// to the built-in segmenter, so this reports the reason instead of throwing.
    /// </summary>
    public static (SamSegmentService? Service, string? Error) Start(
        string? model = null,
        string? device = null,
        int loadTimeoutMs = 180_000,
        Action<string>? log = null)
    {
        string? python = RepoLayout.FindPython();
        if (python is null)
            return (null, "找不到 Python 解释器");

        string script = Path.Combine(RepoLayout.TrainDir, "sam_service.py");
        if (!File.Exists(script))
            return (null, $"找不到 {script}");

        // Encoding.UTF8 emits a byte-order mark at the head of the stream, and the service
        // reads the first line as JSON — the BOM makes it "Unexpected UTF-8 BOM". No identifier.
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var psi = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = RepoLayout.TrainDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(script);

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            proc.Dispose();
            return (null, $"启动 SAM 服务失败: {ex.Message}");
        }

        var svc = new SamSegmentService(proc, proc.StandardInput, proc.StandardOutput);

        // Drain stderr from the first moment, not after the model has loaded: torch is chatty
        // enough during import to fill a 4 KB pipe.
        var stderr = proc.StandardError;
        var diagThread = new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = stderr.ReadLine()) is not null)
                {
                    lock (svc._diagnostics)
                    {
                        svc._diagnostics.Add(line);
                        if (svc._diagnostics.Count > 200)
                            svc._diagnostics.RemoveAt(0);
                    }

                    log?.Invoke($"[SAM] {line}");
                }
            }
            catch
            {
                // process gone
            }
        })
        { IsBackground = true, Name = "sam-service-stderr" };
        diagThread.Start();

        var (reply, err) = svc.Request(new Dictionary<string, object?>
        {
            ["cmd"] = "load",
            ["model"] = model ?? "",
            ["device"] = device ?? "auto",
        }, loadTimeoutMs);
        if (reply is null)
        {
            svc.Dispose();
            return (null, err ?? "SAM 服务加载超时");
        }

        using (reply)
        {
            var root = reply.RootElement;
            if (!root.GetProperty("ok").GetBoolean())
            {
                string why = root.TryGetProperty("error", out var e) ? e.GetString() ?? "加载失败" : "加载失败";
                svc.Dispose();
                return (null, why);
            }

            svc.Device = root.GetProperty("device").GetString() ?? "";
            svc.ModelName = root.GetProperty("model").GetString() ?? "";
            svc.Family = root.GetProperty("family").GetString() ?? "";
        }

        log?.Invoke($"[SAM] 就绪: {svc.ModelName} ({svc.Family}) on {svc.Device}");
        return (svc, null);
    }

    /// <summary>Async wrapper so the GUI can warm it up at startup without blocking the form.</summary>
    public static Task<(SamSegmentService? Service, string? Error)> StartAsync(
        string? model = null,
        string? device = null,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        return Task.Run(() => Start(model, device, log: log), ct);
    }

    /// <summary>
    /// Segments the object under <paramref name="seeds"/> inside a BGR window.
    ///
    /// <paramref name="box"/> is an optional bounding box in the window's own pixel space — the
    /// thing SAM actually wants when the user drags one. It is the difference between "something
    /// in here" and "this object", so it is worth the extra prompt when there is one.
    ///
    /// The window's pixels are the cache key: adding a refinement click on the same window hits
    /// the service's feature cache and answers in milliseconds, which is the entire reason this
    /// is usable interactively. Changing the window re-encodes.
    /// </summary>
    public (InteractiveResult? Result, string? Error) Segment(
        Mat bgr,
        IReadOnlyList<SeedPoint> seeds,
        Rectangle? box = null,
        int timeoutMs = 120_000)
    {
        if (_disposed)
            return (null, "服务已退出");
        if (seeds.Count == 0)
            return (null, "没有提示点");

        int w = bgr.Cols, h = bgr.Rows;
        string key = CacheKey(bgr);

        // The service wants RGB row-major, which is what SAM was trained on.
        using var rgb = new Mat();
        Cv2.CvtColor(bgr, rgb, ColorConversionCodes.BGR2RGB);

        var (encode, encodeErr) = Request(new Dictionary<string, object?>
        {
            ["cmd"] = "encode",
            ["key"] = key,
            ["w"] = w,
            ["h"] = h,
            ["rgb"] = Convert.ToBase64String(RgbBytes(rgb)),
        }, timeoutMs);
        if (encode is null)
            return (null, encodeErr);

        using (encode)
        {
            if (!encode.RootElement.GetProperty("ok").GetBoolean())
                return (null, encode.RootElement.TryGetProperty("error", out var e1)
                    ? e1.GetString() : "编码失败");
        }

        var points = new List<object[]>(seeds.Count);
        foreach (SeedPoint s in seeds)
            points.Add([s.X, s.Y, s.Positive]);

        var (reply, replyErr) = Request(new Dictionary<string, object?>
        {
            ["cmd"] = "prompt",
            ["key"] = key,
            ["points"] = points,
            // XYXY in the window's pixel space, which is exactly what the SAM API wants.
            ["box"] = box is { } r ? new object[] { r.Left, r.Top, r.Right, r.Bottom } : null,
        }, timeoutMs);
        if (reply is null)
            return (null, replyErr);

        using (reply)
        {
            var root = reply.RootElement;
            if (!root.GetProperty("ok").GetBoolean())
                return (null, root.TryGetProperty("error", out var e2) ? e2.GetString() : "分割失败");

            int rw = root.GetProperty("w").GetInt32();
            int rh = root.GetProperty("h").GetInt32();
            byte[] raw = Convert.FromBase64String(root.GetProperty("mask").GetString() ?? "");
            if (raw.Length != rw * rh)
                return (null, $"掩码字节数不符: {raw.Length} != {rw * rh}");

            // The model's opinion, then this app's conventions — the same steps the classical
            // segmenter applies, in the same order, so both backends obey one set of rules. Clean
            // first (holes, speckle, a diffuse edge), then re-assert the clicks: a negative click
            // must not be filled back in by hole-filling, and only the blob the user pointed at is
            // kept (SAM happily returns every similar object in the window).
            MaskPostProcessor.CleanBinary(raw, rw, rh, bgr);
            InteractiveSegmenter.ForcePositiveDisks(raw, rw, rh, seeds,
                InteractiveSegmenter.DefaultSeedRadius);

            bool[] blocked = InteractiveSegmenter.NegativeClickDisks(rw, rh, seeds,
                InteractiveSegmenter.DefaultSeedRadius);
            for (int i = 0; i < raw.Length; i++)
                if (blocked[i])
                    raw[i] = 0;

            int components = InteractiveSegmenter.KeepClicksComponents(raw, rw, rh, seeds);
            int painted = 0;
            foreach (byte b in raw)
                if (b != 0)
                    painted++;

            return (new InteractiveResult(raw, rw, rh, painted, components), null);
        }
    }

    /// <summary>Drops every cached window (the next call re-encodes).</summary>
    public void Reset()
    {
        if (_disposed)
            return;
        Request(new Dictionary<string, object?> { ["cmd"] = "reset" }, 30_000);
    }

    private static byte[] RgbBytes(Mat rgb)
    {
        int w = rgb.Cols, h = rgb.Rows;
        var bytes = new byte[w * h * 3];
        if (rgb.IsContinuous())
        {
            System.Runtime.InteropServices.Marshal.Copy(rgb.Data, bytes, 0, bytes.Length);
            return bytes;
        }

        for (int y = 0; y < h; y++)
            System.Runtime.InteropServices.Marshal.Copy(rgb.Row(y).Data, bytes, y * w * 3, w * 3);
        return bytes;
    }

    /// <summary>
    /// Identity of a window's pixels. Content-addressed so a caller that re-centres its window
    /// cannot accidentally keep a cache hit for a different image — and so a window that really
    /// is unchanged (the common refinement case) always hits.
    /// </summary>
    private static string CacheKey(Mat bgr)
    {
        byte[] pixels = RgbBytes(bgr);

        // FNV-1a 64: enough to identify a window, and far cheaper than a crypto hash over the
        // ~1 MB this runs on every click.
        ulong hash = 14695981039346656037UL;
        foreach (byte b in pixels)
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }

        return $"{bgr.Cols}x{bgr.Rows}:{hash:x16}";
    }

    private (JsonDocument? Reply, string? Error) Request(Dictionary<string, object?> payload, int timeoutMs)
    {
        lock (_gate)
        {
            if (_disposed)
                return (null, "SAM 服务已退出");

            try
            {
                _stdin.WriteLine(JsonSerializer.Serialize(payload));
                _stdin.Flush();

                var read = Task.Run(() => _stdout.ReadLine());
                if (!read.Wait(timeoutMs))
                    return (null, $"SAM 服务 {timeoutMs / 1000}s 无响应");

                string? line = read.Result;
                if (line is null)
                    return (null, "SAM 服务已关闭输出（进程可能已退出）");

                return (JsonDocument.Parse(line), null);
            }
            catch (Exception ex)
            {
                return (null, $"SAM 通信失败: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        try
        {
            _stdin.WriteLine("{\"cmd\":\"quit\"}");
            _stdin.Flush();
        }
        catch
        {
            // already gone
        }

        try
        {
            if (!_proc.WaitForExit(2000))
                _proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // already gone
        }

        _stdin.Dispose();
        _stdout.Dispose();
        _proc.Dispose();
    }
}
