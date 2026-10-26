namespace LandCover.Core;

/// <summary>
/// Locates the repository's directories from wherever the executable happens to sit, so
/// <c>dotnet run</c> works from any working directory. Shared by the CLI and both GUI apps.
/// </summary>
public static class RepoLayout
{
    public static string Root { get; } = FindRoot();

    public static string TrainDir => Path.Combine(Root, "train");
    public static string TrainScript => Path.Combine(TrainDir, "train.py");
    public static string ExportScript => Path.Combine(TrainDir, "export_onnx.py");
    public static string DataDir => Path.Combine(TrainDir, "data");
    public static string RunsDir => Path.Combine(TrainDir, "runs");

    /// <summary>Transcript of the last training/export run. The GUI is a WinExe with no console,
    /// so this file is the only record that outlives the window.</summary>
    public static string TrainLogPath => Path.Combine(RunsDir, "train.log");
    public static string ModelsDir => Path.Combine(Root, "models");
    public static string AnnotationsDir => Path.Combine(Root, "annotations");
    public static string OutputDir => Path.Combine(Root, "output");

    /// <summary>Default model, if it exists.</summary>
    public static string DefaultModel => Path.Combine(ModelsDir, "deeplabv3_landcover_4c.onnx");

    /// <summary>The recommended out-of-the-box model (OpenEarthMap 8-class, MIT).</summary>
    public static string RecommendedModel => Path.Combine(ModelsDir, "oem_sparsemask_8c.onnx");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LightViewDemo.sln")))
                return dir.FullName;
            if (Directory.Exists(Path.Combine(dir.FullName, "models"))
                && Directory.Exists(Path.Combine(dir.FullName, "train")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// The interpreter to run training with. Prefers the project venv (which has torch,
    /// rasterio and segmentation-models-pytorch installed) over whatever is on PATH.
    /// </summary>
    public static string? FindPython()
    {
        foreach (string candidate in new[]
                 {
                     Path.Combine(Root, ".venv", "Scripts", "python.exe"),
                     Path.Combine(Root, ".venv", "bin", "python"),
                     Path.Combine(TrainDir, ".venv", "Scripts", "python.exe"),
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return WhichOnPath("python") ?? WhichOnPath("python3");
    }

    public static string? FindVenvPython() =>
        new[]
        {
            Path.Combine(Root, ".venv", "Scripts", "python.exe"),
            Path.Combine(Root, ".venv", "bin", "python"),
        }.FirstOrDefault(File.Exists);

    public static string? WhichOnPath(string exe)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null)
            return null;

        foreach (string dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            try
            {
                string full = Path.Combine(dir, exe + ".exe");
                if (File.Exists(full))
                    return full;
            }
            catch
            {
                // malformed PATH entry
            }
        }

        return null;
    }

    /// <summary>Every model directory entry with a .onnx extension, sorted.</summary>
    public static IEnumerable<string> ListModels()
    {
        try
        {
            return Directory.Exists(ModelsDir)
                ? Directory.EnumerateFiles(ModelsDir, "*.onnx").Order()
                : [];
        }
        catch
        {
            return [];
        }
    }
}
