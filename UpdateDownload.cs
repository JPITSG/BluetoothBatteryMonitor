using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BluetoothBatteryMonitor;

internal static class UpdateDownload
{
    internal static async Task<Version> StageAsync(HttpClient http, string url, string path,
        long maximumSize, Func<string, Version> validate, IProgress<string> progress, CancellationToken cancellation)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long expected = response.Content.Headers.ContentLength ?? throw new InvalidDataException("The server did not report a file size.");
        if (expected <= 0 || expected > maximumSize) throw new InvalidDataException("Invalid update size.");
        await using (var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false))
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
        {
            byte[] buffer = new byte[81920];
            long total = 0;
            var watch = Stopwatch.StartNew();
            long lastReport = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
            {
                total += count;
                if (total > expected || total > maximumSize) throw new InvalidDataException("Invalid update size.");
                await output.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
                if (watch.ElapsedMilliseconds - lastReport >= 250)
                {
                    progress.Report($"Downloading… {total * 100 / expected}% ({total / 1024 / Math.Max(1, watch.Elapsed.TotalSeconds):0} KB/s)");
                    lastReport = watch.ElapsedMilliseconds;
                }
            }
            if (total != expected) throw new InvalidDataException("The download is incomplete.");
        }
        cancellation.ThrowIfCancellationRequested();
        var version = validate(path);
        cancellation.ThrowIfCancellationRequested();
        return version;
    }
}
