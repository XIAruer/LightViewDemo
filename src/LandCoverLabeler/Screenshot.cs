using System.Drawing.Imaging;

namespace LandCoverLabeler;

/// <summary>
/// Saves a PNG of a window when <c>LC_UITEST_SHOT</c> names an output directory. Scaffolding for the
/// UI smoke test: it is the only way to tell a layout refactor that preserved the window from one
/// that quietly rearranged it, since a headless test can only prove the form did not throw.
/// </summary>
internal static class Screenshot
{
    public static void IfRequested(Form form, string label)
    {
        string? dir = Environment.GetEnvironmentVariable("LC_UITEST_SHOT");
        if (string.IsNullOrWhiteSpace(dir))
            return;

        try
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, label + ".png");

            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(path, ImageFormat.Png);

            Console.WriteLine($"  截图: {path} ({form.Width}x{form.Height})");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  截图失败: " + ex.Message);
        }
    }
}
