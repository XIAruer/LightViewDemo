using System.Net;
using System.Security.Cryptography;

namespace LandCover.Core;

public readonly record struct ModelDownloadProgress(string FileName, long Downloaded, long Total)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp(Downloaded / (double)Total, 0, 1);
}

/// <summary>
/// Fetches the catalog models into <see cref="RepoLayout.ModelsDir"/>. The repository ships no
/// weights, so a fresh clone has to be able to obtain them without a browser.
///
/// Downloads go to <c>&lt;file&gt;.part</c> and are moved into place only after the byte count and
/// SHA-256 match the catalog, so an interrupted transfer never leaves a half model that would
/// fail later inside ONNX Runtime with a confusing parse error.
/// </summary>
public static class ModelDownloader
{
    private const int BufferSize = 81920;

    private static readonly HttpClient Http = CreateClient();

    public static string PathFor(ModelEntry entry) => Path.Combine(RepoLayout.ModelsDir, entry.RelativePath);

    /// <summary>True when the file exists and has the expected size (the SHA is only checked on download).</summary>
    public static bool IsInstalled(ModelEntry entry)
    {
        string path = PathFor(entry);
        try
        {
            return File.Exists(path) && new FileInfo(path).Length == entry.SizeBytes;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<string> DownloadAsync(
        ModelEntry entry,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        string target = PathFor(entry);
        string part = target + ".part";

        string? dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        try
        {
            using var response = await Http.GetAsync(entry.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? entry.SizeBytes;
            if (total != entry.SizeBytes)
                throw new InvalidDataException(
                    $"服务器上的文件大小与目录不符（{total} vs {entry.SizeBytes} 字节），可能上游已更新。");

            long downloaded = 0;
            long lastReported = 0;
            var buffer = new byte[BufferSize];

            await using (Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var sink = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None,
                             BufferSize, useAsync: true))
            {
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await sink.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    downloaded += read;

                    // 300 MB at 80 KB per report would be ~4000 UI posts; 1% is plenty.
                    if (progress is not null && downloaded - lastReported >= Math.Max(BufferSize, total / 100))
                    {
                        lastReported = downloaded;
                        progress.Report(new ModelDownloadProgress(entry.FileName, downloaded, total));
                    }
                }
            }

            long actual = new FileInfo(part).Length;
            if (actual != entry.SizeBytes)
                throw new InvalidDataException($"下载不完整：期望 {entry.SizeBytes} 字节，实际 {actual} 字节。");

            string hash = await ComputeSha256Async(part, ct).ConfigureAwait(false);
            if (!hash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SHA-256 校验失败（得到 {hash}），文件已删除，请重试。");

            File.Move(part, target, overwrite: true);
            progress?.Report(new ModelDownloadProgress(entry.FileName, entry.SizeBytes, entry.SizeBytes));
            return target;
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // a leftover .part is harmless and will be overwritten next time
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,
        })
        {
            // Multi-hundred-megabyte transfers on a slow link are normal.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LightViewDemo/1.0 (+https://github.com/XIAruer/LightViewDemo)");
        return client;
    }
}
