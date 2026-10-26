using LandCover.Core;
using LandCoverLabeler;

namespace LandCoverShell;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        try
        {
            GdalRuntime.EnsureInitialized();

            // LandCoverLabeler's own entry point loads the palette before building its form;
            // the shell constructs that form directly, so it has to do the same.
            Classes.Load(Classes.LocateClassesFile());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"初始化失败:\n{ex.Message}", "LandCoverShell",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        ApplicationConfiguration.Initialize();

        (string view, string? raster) = ShellLauncher.ParseArgs(args);
        if (raster is not null && !File.Exists(raster))
        {
            MessageBox.Show($"找不到影像:\n{raster}", "LandCoverShell",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            raster = null;
        }

        if (args.Contains("--uitest", StringComparer.OrdinalIgnoreCase))
            return UiSmokeTest(view, raster);

        Application.Run(new ShellForm(view, raster));
        return 0;
    }

    /// <summary>
    /// Builds the real frame, switches between both views in both directions, paints each one and
    /// exits. Catches the failure that matters here: a menu entry that silently does nothing.
    /// </summary>
    private static int UiSmokeTest(string view, string? raster)
    {
        var failures = new List<string>();
        ShellForm form;
        try
        {
            form = new ShellForm(view, raster) { Width = 1400, Height = 900 };
        }
        catch (Exception ex)
        {
            Console.WriteLine("构造主菜单窗口失败: " + ex);
            return 1;
        }

        Application.ThreadException += (_, e) => failures.Add("UI 线程异常: " + e.Exception.Message);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => failures.Add("未处理异常: " + e.ExceptionObject);

        var timer = new System.Windows.Forms.Timer { Interval = 2500 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();

            foreach (string key in new[] { ViewKeys.Infer, ViewKeys.Labeler, ViewKeys.Infer })
            {
                try
                {
                    form.SwitchTo(key);
                    Application.DoEvents();
                    form.Refresh();
                    Application.DoEvents();

                    if (!form.VerifyViewOnScreen(key, out string why))
                        failures.Add(why);
                }
                catch (Exception ex)
                {
                    failures.Add($"切到 {ViewKeys.Display(key)} 失败: " + ex);
                }
            }

            form.Close();
        };
        timer.Start();

        Console.WriteLine($"UI 冒烟测试: 初始视图={ViewKeys.Display(view)} raster={raster ?? "(未指定)"}");
        Application.Run(form);

        foreach (string f in failures)
            Console.WriteLine("  [FAIL] " + f);
        Console.WriteLine(failures.Count == 0
            ? "=== 主菜单 UI 冒烟测试通过 ==="
            : $"=== 主菜单 UI 冒烟测试 {failures.Count} 项失败 ===");
        return failures.Count == 0 ? 0 : 1;
    }
}
