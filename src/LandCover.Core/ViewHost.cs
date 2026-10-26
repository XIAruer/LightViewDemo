using System.Diagnostics;

namespace LandCover.Core;

/// <summary>
/// The two top-level windows <c>LandCoverShell</c> can show. Kept as strings rather than an
/// enum so the shell can parse them straight off its command line.
/// </summary>
public static class ViewKeys
{
    public const string Infer = "infer";
    public const string Labeler = "labeler";

    public static string Display(string key) => Normalize(key) == Labeler ? "标注与训练" : "识别";

    /// <summary>Accepts a few spellings; anything unrecognised is the recognition view.</summary>
    public static string Normalize(string? key) => key?.Trim().ToLowerInvariant() switch
    {
        "labeler" or "label" or "train" or "annotation" or "标注" => Labeler,
        _ => Infer,
    };
}

/// <summary>
/// Implemented by the shell's main window. Both view windows read it when their 视图 menu fires:
/// non-null means they are embedded and the shell does the switching, null means they were
/// launched on their own and should fall back to <see cref="ShellLauncher"/>.
///
/// Lives in Core — not in a file linked into both GUI projects — because the views and the shell
/// have to agree on one type identity for the cast to work across assemblies.
/// </summary>
public interface IViewHost
{
    /// <summary>Brings the named view to the front, optionally opening <paramref name="rasterPath"/> in it.</summary>
    void SwitchTo(string viewKey, string? rasterPath = null);

    /// <summary>
    /// Closes the whole application. Embedded views cannot close themselves — their own
    /// <c>Close()</c> would tear down the child window and leave the shell empty.
    /// </summary>
    void RequestExit();
}

/// <summary>
/// Starts the shell on behalf of a view window that was opened standalone. The shell owns the
/// menu that switches views, so a lone window can still hand the user over to it — and carry the
/// image it currently has open.
/// </summary>
public static class ShellLauncher
{
    /// <summary>Command-line switch the shell reads to pick its initial view.</summary>
    public const string ViewOption = "--view";

    private static string ExeName => OperatingSystem.IsWindows() ? "LandCoverShell.exe" : "LandCoverShell";

    /// <summary>
    /// The shell next to this executable, or the one the solution build wrote under
    /// <c>src/LandCoverShell/bin</c>. Null when it has never been built.
    /// </summary>
    public static string? FindShellExecutable()
    {
        foreach (string candidate in Candidates())
        {
            try
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // malformed path entry
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, ExeName);

        foreach (string config in new[] { "Release", "Debug" })
            yield return Path.Combine(RepoLayout.Root, "src", "LandCoverShell", "bin", config,
                "net9.0-windows", ExeName);
    }

    /// <summary>
    /// Launches the shell on <paramref name="viewKey"/>. Returns null on success, otherwise a
    /// message to show the user explaining what to run instead.
    /// </summary>
    public static string? TryLaunch(string viewKey, string? rasterPath = null)
    {
        string? exe = FindShellExecutable();
        if (exe is null)
        {
            string image = string.IsNullOrWhiteSpace(rasterPath) ? "" : $"\"{rasterPath}\" ";
            return "还没构建过主菜单程序（LandCoverShell）。\n\n"
                + "先构建解决方案，或直接运行：\n\n"
                + $"    dotnet run --project src\\LandCoverShell -- {image}{ViewOption} {ViewKeys.Normalize(viewKey)}";
        }

        // ArgumentList instead of a hand-built command line: raster paths routinely contain
        // spaces and non-ASCII characters.
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = RepoLayout.Root,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(ViewOption);
        psi.ArgumentList.Add(ViewKeys.Normalize(viewKey));
        if (!string.IsNullOrWhiteSpace(rasterPath))
            psi.ArgumentList.Add(rasterPath);

        try
        {
            Process.Start(psi);
            return null;
        }
        catch (Exception ex)
        {
            return $"启动 {exe} 失败：{ex.Message}";
        }
    }

    /// <summary>
    /// Reads the shell's arguments. The first non-option argument is the raster, matching how
    /// the other three programs parse their command lines.
    /// </summary>
    public static (string View, string? Raster) ParseArgs(string[] args)
    {
        string view = ViewKeys.Infer;
        string? raster = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is ViewOption && i + 1 < args.Length)
            {
                view = ViewKeys.Normalize(args[++i]);
                continue;
            }

            if (!args[i].StartsWith('-') && raster is null)
                raster = args[i];
        }

        return (view, raster);
    }
}
