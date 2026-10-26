using LandCover.Core;

namespace LandCoverInfer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        try
        {
            GdalRuntime.EnsureInitialized();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"GDAL 初始化失败:\n{ex.Message}", "LandCoverInfer",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        // Must run before any control is constructed: HighDpiMode cannot be changed once a
        // window exists, and the smoke test builds the form before Application.Run.
        ApplicationConfiguration.Initialize();

        if (args.Contains("--uitest", StringComparer.OrdinalIgnoreCase))
            return UiSmokeTest(args.FirstOrDefault(a => !a.StartsWith('-')));

        string? initial = args.FirstOrDefault(a => !a.StartsWith('-'));

        Application.Run(new InferenceForm(initial));
        return 0;
    }

    /// <summary>
    /// Builds and paints the window for a few seconds, then exits — catches layout/init
    /// exceptions that a headless test cannot reach.
    /// </summary>
    private static int UiSmokeTest(string? raster)
    {
        if (raster is not null && !File.Exists(raster))
        {
            Console.WriteLine($"UI 冒烟测试: 找不到 {raster}");
            return 1;
        }

        var failures = new List<string>();
        InferenceForm form;
        try
        {
            form = new InferenceForm(raster);
            form.Width = 1200;
            form.Height = 800;
        }
        catch (Exception ex)
        {
            Console.WriteLine("构造窗口失败: " + ex);
            return 1;
        }

        Application.ThreadException += (_, e) => failures.Add("UI 线程异常: " + e.Exception.Message);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => failures.Add("未处理异常: " + e.ExceptionObject);

        bool runPipeline = Environment.GetEnvironmentVariable("LC_UITEST_RUN") == "1";
        string? model = Environment.GetEnvironmentVariable("LC_UITEST_MODEL");
        string? prefix = Environment.GetEnvironmentVariable("LC_UITEST_PREFIX");

        var timer = new System.Windows.Forms.Timer { Interval = 2500 };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();

            if (runPipeline)
            {
                try
                {
                    if (model is not null)
                        form.SelectModel(model);
                    if (prefix is not null)
                        form.SetOutputPrefix(prefix);
                    form.SetPreviewMode(1); // force previews so the result tab is exercised

                    string summary = await form.RunHeadlessAsync();
                    Console.WriteLine("  流水线: " + summary);
                }
                catch (Exception ex)
                {
                    failures.Add("流水线失败: " + ex.Message);
                }
            }

            try
            {
                form.Refresh();
                Application.DoEvents();
            }
            catch (Exception ex)
            {
                failures.Add("重绘失败: " + ex.Message);
            }

            Screenshot.IfRequested(form, "infer");
            form.Close();
        };
        timer.Start();

        Console.WriteLine($"UI 冒烟测试: raster={raster ?? "(未指定)"} 运行流水线={runPipeline}");
        Application.Run(form);

        foreach (string f in failures)
            Console.WriteLine("  [FAIL] " + f);
        Console.WriteLine(failures.Count == 0 ? "=== UI 冒烟测试通过 ===" : $"=== UI 冒烟测试 {failures.Count} 项失败 ===");
        return failures.Count == 0 ? 0 : 1;
    }
}
