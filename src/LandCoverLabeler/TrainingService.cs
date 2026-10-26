using System.Diagnostics;
using System.Text;
using LandCover.Core;

namespace LandCoverLabeler;

public sealed record ProcessResult(int ExitCode, string Output, string? LogPath = null);

/// <summary>Runs the Python training scripts and streams their output back to the UI.</summary>
public sealed class TrainingService
{
    private Process? _current;

    public bool IsRunning => _current is { HasExited: false };

    public void Cancel()
    {
        try
        {
            if (_current is { HasExited: false })
                _current.Kill(entireProcessTree: true);
        }
        catch
        {
            // already gone
        }
    }

    /// <summary>
    /// Runs <paramref name="script"/> with <paramref name="args"/>, forwarding stdout and stderr
    /// line by line. <paramref name="onLine"/> is called on a background thread.
    ///
    /// When <paramref name="logPath"/> is given the same transcript is also written there with
    /// timestamps. That matters more than it sounds: the app is a WinExe with no console, so
    /// without a file the only record of a run is a scrollback that dies with the window — and a
    /// training run that seems to hang is exactly when you want to look at one afterwards.
    /// </summary>
    public async Task<ProcessResult> RunScriptAsync(
        string python,
        string script,
        IReadOnlyList<string> args,
        Action<string> onLine,
        string? logPath = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(python))
            throw new FileNotFoundException($"找不到 Python 解释器: {python}");
        if (!File.Exists(script))
            throw new FileNotFoundException($"找不到脚本: {script}");

        var psi = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = Path.GetDirectoryName(script)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // torch's ONNX exporter prints emoji; a GBK console would crash it.
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";

        psi.ArgumentList.Add("-u"); // unbuffered, so progress lines appear live
        psi.ArgumentList.Add(script);
        foreach (string a in args)
            psi.ArgumentList.Add(a);

        var sb = new StringBuilder();
        var gate = new object();
        StreamWriter? transcript = null;
        if (logPath is not null)
        {
            try
            {
                string? dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                transcript = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
            }
            catch
            {
                transcript = null; // an unwritable log must never stop the run
            }
        }

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _current = proc;

        void Emit(string line)
        {
            lock (gate)
            {
                sb.AppendLine(line);
                transcript?.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
            }

            onLine(line);
        }

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                Emit(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                Emit(e.Data);
        };

        var command = $"$ {Quote(python)} -u {Path.GetFileName(script)} {string.Join(' ', args.Select(Quote))}";
        Emit(command);
        Emit($"  (工作目录 {psi.WorkingDirectory})");

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try
            {
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Cancel();
                Emit("*** 已取消 ***");
            }

            // WaitForExitAsync returns as soon as the process ends; the asynchronous readers can
            // still be a line or two behind. The blocking overload waits for them to finish, so
            // the last "done. best mIoU=..." line is never lost.
            if (proc.HasExited)
                proc.WaitForExit();
        }
        finally
        {
            _current = null;
            lock (gate)
            {
                transcript?.Flush();
                transcript?.Dispose();
            }
        }

        string output;
        lock (gate) output = sb.ToString();
        return new ProcessResult(proc.ExitCode, output, logPath);
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    /// <summary>Runs a quick probe to confirm the interpreter has the training dependencies.</summary>
    public async Task<(bool Ok, string Message)> CheckEnvironmentAsync(string python)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = python,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUTF8"] = "1";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(
                "import torch, torchvision, numpy, PIL;"
                + "print('torch', torch.__version__, '| cuda', torch.cuda.is_available());"
                + "import segmentation_models_pytorch as smp; print('smp', smp.__version__)");

            using var p = Process.Start(psi)!;

            // Both pipes must be drained concurrently. Reading stdout to the end first would
            // deadlock the moment the child fills the 4 KB stderr buffer — and import warnings
            // are exactly the kind of thing that produces a few KB of stderr.
            Task<string> stdoutTask = p.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync().ConfigureAwait(false);
            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);

            return p.ExitCode == 0
                ? (true, stdout.Trim())
                : (false, $"缺少依赖:\n{stderr.Trim()}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
