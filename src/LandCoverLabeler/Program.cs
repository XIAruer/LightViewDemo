using LandCover.Core;

namespace LandCoverLabeler;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        try
        {
            GdalRuntime.EnsureInitialized();
            Classes.Load(Classes.LocateClassesFile());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"初始化失败:\n{ex.Message}", "LandCoverLabeler",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
            return SelfTest.Run(args);

        if (args.Contains("--traincheck", StringComparer.OrdinalIgnoreCase))
            return SelfTest.TrainCheck(args);

        if (args.Contains("--samcheck", StringComparer.OrdinalIgnoreCase))
            return SelfTest.SamCheck(args);

        string? initial = args.FirstOrDefault(a => !a.StartsWith('-'));

        ApplicationConfiguration.Initialize();

        if (args.Contains("--uitest", StringComparer.OrdinalIgnoreCase))
            return UiSmokeTest(initial);

        Application.Run(new MainForm(initial));
        return 0;
    }

    /// <summary>
    /// Builds and paints the real window for a few seconds, then exits. Exercises the paths a
    /// headless self-test cannot reach: form/toolstrip/palette construction, opening a raster,
    /// fitting the view, the async preview build and an actual OnPaint (GDAL read + overlay).
    /// </summary>
    private static int UiSmokeTest(string? raster)
    {
        if (raster is not null && !File.Exists(raster))
        {
            Console.WriteLine($"UI 冒烟测试: 找不到 {raster}");
            return 1;
        }

        MainForm form;
        try
        {
            form = new MainForm(raster);
        }
        catch (Exception ex)
        {
            Console.WriteLine("构造窗口失败: " + ex);
            return 1;
        }

        var failures = new List<string>();

        Application.ThreadException += (_, e) => failures.Add("UI 线程异常: " + e.Exception.Message);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => failures.Add("未处理异常: " + e.ExceptionObject);

        var timer = new System.Windows.Forms.Timer { Interval = 4000 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();

            // Force the render paths rather than relying on the compositor to ask for them.
            try
            {
                form.Refresh();
                Application.DoEvents();
            }
            catch (Exception ex)
            {
                failures.Add("重绘失败: " + ex.Message);
            }

            Screenshot.IfRequested(form, "labeler");

            form.Close();
        };
        timer.Start();

        Console.WriteLine($"UI 冒烟测试: raster={raster ?? "(未指定)"}");
        Application.Run(form);

        foreach (string f in failures)
            Console.WriteLine("  [FAIL] " + f);
        Console.WriteLine(failures.Count == 0 ? "=== UI 冒烟测试通过 ===" : $"=== UI 冒烟测试 {failures.Count} 项失败 ===");
        return failures.Count == 0 ? 0 : 1;
    }
}
