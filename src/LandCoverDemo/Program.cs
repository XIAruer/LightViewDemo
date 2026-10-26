using LandCover.Core;

namespace LandCoverDemo;

/// <summary>
/// Console front-end. The actual work lives in <see cref="InferencePipeline"/> so that this
/// and the GUI (LandCoverInfer) cannot drift apart.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        string root = FindRepoRoot();
        string modelPath = Path.Combine(root, "models", "oem_sparsemask_8c.onnx");

        string? defaultInput = FindDefaultSample(root);
        string? positional = FirstPositional(args);
        string inputPath = positional ?? defaultInput ?? "";
        string modelArg = GetOption(args, "--model") ?? modelPath;
        string outputDir = Path.Combine(root, "output");

        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        // Download-only runs are valid without an input image.
        string? downloadSpec = GetOption(args, "--download-model");
        if (downloadSpec is not null && !DownloadModels(downloadSpec))
            return 1;

        if (downloadSpec is not null && positional is null)
            return 0;

        var options = new InferenceOptions(
            InputPath: inputPath,
            ModelPath: modelArg,
            TileSize: int.TryParse(GetOption(args, "--tile"), out var t) ? t : 512,
            Overlap: int.TryParse(GetOption(args, "--overlap"), out var ov) ? ov : 64,
            Threads: int.TryParse(GetOption(args, "--threads"), out var th) && th > 0 ? th : 0,
            CenterWeightedBlend: !HasFlag(args, "--no-center-blend"),
            PostProcess: !HasFlag(args, "--no-post"),
            MorphKernel: int.TryParse(GetOption(args, "--morph"), out var mk) && mk >= 3 ? mk : 3,
            PostMinAreaPx: int.TryParse(GetOption(args, "--post-min-area"), out var pma) ? pma : 64,
            Preview: HasFlag(args, "--preview") ? PreviewMode.Always
                : HasFlag(args, "--no-preview") ? PreviewMode.Never
                : PreviewMode.Auto,
            MinAreaPx: double.TryParse(GetOption(args, "--min-area"), out var ma) ? ma : 64,
            SimplifyPx: double.TryParse(GetOption(args, "--simplify"), out var sp) ? sp : 1.5,
            OutputPrefix: GetOption(args, "--out") ?? Path.Combine(outputDir, Path.GetFileNameWithoutExtension(inputPath)),
            WriteShapefile: HasFlag(args, "--shp"),
            WriteGeoPackage: HasFlag(args, "--gpkg"),
            BuildOverviews: HasFlag(args, "--build-overviews"),
            ForceInMemory: HasFlag(args, "--no-stream"),
            GeorefPath: GetOption(args, "--georef"));

        if (string.IsNullOrEmpty(inputPath))
        {
            Console.Error.WriteLine("没有输入影像。仓库不携带样例数据，请指定一个影像文件。");
            Console.Error.WriteLine(Usage);
            return 1;
        }

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"找不到输入影像: {inputPath}");
            return 1;
        }

        if (!File.Exists(modelArg))
        {
            Console.Error.WriteLine($"找不到 ONNX 模型: {modelArg}");
            ModelEntry? entry = ModelCatalog.ForModelPath(modelArg);
            Console.Error.WriteLine(entry is null
                ? "仓库不携带模型权重。可用 --download-model 下载: oem / sam-tiny / sam-base-plus / all"
                : $"下载它: dotnet run --project src\\LandCoverDemo -c Release -- --download-model {entry.Id}");
            return 1;
        }

        Console.WriteLine("=== 轻量化正射地物分类 Demo (C# + ONNX Runtime + GDAL) ===");
        Console.WriteLine($"GDAL: {(GdalRuntime.IsAvailable ? GdalRuntime.Version : $"不可用 ({GdalRuntime.FailureReason})")}");

        int lastReport = -1;
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            int pct = p.Total == 0 ? 100 : p.Done * 100 / p.Total;
            if (pct != lastReport && pct % 5 == 0)
            {
                lastReport = pct;
                Console.Write($"\r推理进度: {p.Done}/{p.Total} ({pct}%)");
            }
        });

        try
        {
            InferenceResult r = InferencePipeline.Run(options, Console.WriteLine, progress);
            Console.WriteLine();

            var byClass = r.ClassStats.Where(s => s.Pixels > 0)
                .Select(s => $"{s.Name}={s.Pixels} ({s.Percent:F2}%)");
            Console.WriteLine($"矢量: {r.FeatureCount} 个面，耗时 {r.VectorSeconds:F1}s");
            Console.WriteLine($"  分类统计: {string.Join(", ", byClass)}");
            Console.WriteLine();
            Console.WriteLine("输出文件:");
            foreach (string f in r.WrittenFiles)
                Console.WriteLine($"  {f}");
            foreach (string w in r.Warnings)
                Console.WriteLine($"  警告: {w}");

            Console.WriteLine();
            Console.WriteLine("类别图例:");
            for (int i = 0; i < LandCoverPalette.ClassNames.Length; i++)
            {
                var (bb, gg, rr) = LandCoverPalette.ColorsBgr[i];
                Console.WriteLine($"  {i}: {LandCoverPalette.ClassNames[i]}  RGB({rr},{gg},{bb})");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"失败: {ex.Message}");
            return 2;
        }
    }

    private const string Usage =
        "用法: LandCoverDemo [input] [--tile 512] [--overlap 64] [--threads N] [--preview|--no-preview] "
        + "[--no-center-blend] [--no-post] [--morph 3] [--post-min-area 64] [--georef original.tif] "
        + "[--min-area 64] [--simplify 1.5] [--out prefix] [--model model.onnx] "
        + "[--shp] [--gpkg] [--no-stream] [--build-overviews]\n"
        + "下载模型（仓库不携带权重）: --download-model oem|sam-tiny|sam-base-plus|all\n"
        + "图形界面请运行 LandCoverInfer。";

    /// <summary>Options that take a value, so their argument is not mistaken for the input path.</summary>
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--tile", "--overlap", "--threads", "--out", "--georef", "--min-area",
        "--simplify", "--model", "--morph", "--post-min-area", "--download-model",
    };

    /// <summary>Any image left in samples/, if the user still has one. The repo ships none.</summary>
    private static string? FindDefaultSample(string root)
    {
        string dir = Path.Combine(root, "samples");
        if (!Directory.Exists(dir))
            return null;

        foreach (string pattern in new[] { "*.png", "*.tif", "*.tiff", "*.jpg" })
        {
            try
            {
                string? hit = Directory.EnumerateFiles(dir, pattern).Order().FirstOrDefault();
                if (hit is not null)
                    return hit;
            }
            catch
            {
                // unreadable directory: fall through to "no default"
            }
        }

        return null;
    }

    private static bool DownloadModels(string spec)
    {
        List<ModelEntry> wanted;
        if (string.Equals(spec, "all", StringComparison.OrdinalIgnoreCase))
        {
            wanted = ModelCatalog.All.ToList();
        }
        else if (ModelCatalog.ById(spec) is { } one)
        {
            wanted = [one];
        }
        else
        {
            Console.Error.WriteLine($"未知模型: {spec}");
            Console.Error.WriteLine("可选: " + string.Join(", ", ModelCatalog.All.Select(e => e.Id)) + ", all");
            return false;
        }

        foreach (ModelEntry entry in wanted)
        {
            string target = ModelDownloader.PathFor(entry);
            if (ModelDownloader.IsInstalled(entry))
            {
                Console.WriteLine($"[跳过] {entry.DisplayName} 已存在: {target}");
                continue;
            }

            Console.WriteLine($"[下载] {entry.DisplayName} ({entry.SizeText})");
            try
            {
                string path = ModelDownloader
                    .DownloadAsync(entry, new ConsoleDownloadProgress(entry.FileName))
                    .GetAwaiter().GetResult();
                Console.WriteLine();
                Console.WriteLine($"[完成] {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.Error.WriteLine($"[失败] {entry.DisplayName}: {ex.Message}");
                return false;
            }
        }

        Console.WriteLine("模型已就绪。");
        return true;
    }

    /// <summary>Console has no synchronization context, so <see cref="Progress{T}"/> would post
    /// every update to the thread pool and interleave with the next line. Report inline instead.</summary>
    private sealed class ConsoleDownloadProgress(string fileName) : IProgress<ModelDownloadProgress>
    {
        private int _lastPercent = -1;

        public void Report(ModelDownloadProgress p)
        {
            int percent = (int)(p.Fraction * 100);
            if (percent == _lastPercent)
                return;

            _lastPercent = percent;
            Console.Write($"\r  {fileName}  {percent,3}%  "
                + $"{p.Downloaded / 1048576.0:F1}/{p.Total / 1048576.0:F1} MB");
        }
    }

    private static string? FirstPositional(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith('-'))
            {
                if (ValueOptions.Contains(args[i]))
                    i++; // skip this option's value
                continue;
            }

            return args[i];
        }

        return null;
    }

    private static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    private static bool HasFlag(string[] args, string name)
        => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    internal static string FindRepoRoot()
    {
        // Prefer the project root that contains models/ next to src/
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "models")))
                return dir.FullName;
            if (File.Exists(Path.Combine(dir.FullName, "LightViewDemo.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
