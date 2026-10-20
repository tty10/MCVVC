using System.Net.Http;
using System.Security.Cryptography;

namespace MclLauncher.Core;

public sealed class DownloadTask
{
    public string Url { get; init; } = "";
    public string Destination { get; init; } = "";
    public string? Sha1 { get; init; }
    public long Size { get; init; }
    public string Label { get; init; } = "";
    public Dictionary<string, string>? Headers { get; init; }
}

public sealed class DownloadProgress
{
    public int Completed { get; set; }
    public int Total { get; set; }
    public int Failed { get; set; }
    public long Bytes { get; set; }
    public string Current { get; set; } = "";
}

public static class Downloader
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MclLauncher/1.0");
        return client;
    }

    public static string? Sha1OfFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA1.Create();
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>已存在且 hash 对得上就直接跳过 —— 这就是天然的断点续传。</summary>
    public static async Task<bool> DownloadAsync(
        DownloadTask task,
        IProgress<DownloadProgress>? progress,
        DownloadProgress? shared,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(task.Sha1) && File.Exists(task.Destination))
        {
            if (Sha1OfFile(task.Destination) == task.Sha1) return true;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(task.Destination) ?? ".");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var temp = task.Destination + ".part";
            try
            {
            using var request = new HttpRequestMessage(HttpMethod.Get, task.Url);
            if (task.Headers != null)
            {
                foreach (var header in task.Headers)
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

                await using (var source = await response.Content.ReadAsStreamAsync(ct))
                await using (var target = File.Create(temp))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), ct);
                        if (shared != null) shared.Bytes += read;
                    }
                }

                if (!string.IsNullOrEmpty(task.Sha1) && Sha1OfFile(temp) != task.Sha1)
                {
                    File.Delete(temp);
                    continue;
                }

                File.Move(temp, task.Destination, overwrite: true);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                await Task.Delay(600 * (attempt + 1), ct);
            }
        }
        return false;
    }

    /// <summary>并发跑一批任务。MC 的 assets 动辄三四千个对象，串行会等到睡着。</summary>
    public static async Task<(int ok, int fail)> DownloadAllAsync(
        IReadOnlyList<DownloadTask> tasks,
        int parallelism,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var shared = new DownloadProgress { Total = tasks.Count };
        var ok = 0;
        var fail = 0;
        var gate = new SemaphoreSlim(Math.Max(1, parallelism));

        var running = tasks.Select(async task =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var success = await DownloadAsync(task, progress, shared, ct);
                if (success) Interlocked.Increment(ref ok);
                else Interlocked.Increment(ref fail);

                shared.Completed++;
                shared.Current = task.Label;
                progress?.Report(shared);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(running);
        return (ok, fail);
    }

    public static async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }
}
