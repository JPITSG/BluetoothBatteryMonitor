using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BluetoothBatteryMonitor;

internal readonly record struct DownloadProgress(long Percent, long KilobytesPerSecond);

internal static class UpdateDownload
{
    internal static long CalculateSpeed(long receivedBytes, TimeSpan elapsed) => elapsed > TimeSpan.Zero
        ? (long)Math.Round(receivedBytes / 1024.0 / elapsed.TotalSeconds, MidpointRounding.AwayFromZero) : 0;

    internal static async Task<Version> StageAsync(HttpClient http, string url, string path,
        long maximumSize, Func<string, Version> validate, IProgress<DownloadProgress> progress, CancellationToken cancellation)
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
            using var reporting = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var reportTask = ReportAsync();
            try
            {
                int count;
                while ((count = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
                {
                    long received = Interlocked.Add(ref total, count);
                    if (received > expected || received > maximumSize) throw new InvalidDataException("Invalid update size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
                }
                if (total != expected) throw new InvalidDataException("The download is incomplete.");
            }
            finally
            {
                reporting.Cancel();
                await reportTask.ConfigureAwait(false);
            }

            async Task ReportAsync()
            {
                // Sample recent throughput on a monotonic clock, including stalls,
                // at most four times per second regardless of network chunk size.
                var watch = Stopwatch.StartNew();
                var previousTime = TimeSpan.Zero;
                long previousTotal = 0;
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
                try
                {
                    while (await timer.WaitForNextTickAsync(reporting.Token).ConfigureAwait(false))
                    {
                        var now = watch.Elapsed;
                        long received = Interlocked.Read(ref total);
                        progress.Report(new DownloadProgress(received * 100 / expected,
                            CalculateSpeed(received - previousTotal, now - previousTime)));
                        previousTime = now;
                        previousTotal = received;
                    }
                }
                catch (OperationCanceledException) when (reporting.IsCancellationRequested) { }
            }
        }
        cancellation.ThrowIfCancellationRequested();
        var version = validate(path);
        cancellation.ThrowIfCancellationRequested();
        return version;
    }
}
