using System.Net;
using System.Collections.Concurrent;
using System.Diagnostics;
using BluetoothBatteryMonitor;

internal static class UpdateDownloadTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        check(UpdateDownload.CalculateSpeed(25600, TimeSpan.FromMilliseconds(250)) == 100,
            "Subsecond speed uses actual elapsed time, without a one-second floor.");
        check(UpdateDownload.CalculateSpeed(25728, TimeSpan.FromMilliseconds(250)) == 101,
            "Half-kilobyte speeds round to the nearest whole number, upwards at midpoint.");
        check(UpdateDownload.CalculateSpeed(25727, TimeSpan.FromMilliseconds(250)) == 100,
            "Speeds below the midpoint round down without integer truncation of bytes.");
        check(UpdateDownload.CalculateSpeed(0, TimeSpan.FromMilliseconds(250)) == 0 &&
            UpdateDownload.CalculateSpeed(1024, TimeSpan.Zero) == 0, "Zero bytes and zero elapsed time are safe.");
        var folder = Path.Combine(Path.GetTempPath(), "battery-download-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var bytes = Enumerable.Range(0, 180000).Select(i => (byte)i).ToArray();
            var expectedVersion = new Version(1, 0, 9, 0);
            var progress = new Progress<DownloadProgress>();
            async Task Stage(HttpContent content, long maximum, Func<string, Version> validate, CancellationToken token = default)
            {
                using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
                var result = await UpdateDownload.StageAsync(http, "https://test.invalid/update", Path.Combine(folder, Guid.NewGuid() + ".exe"),
                    maximum, validate, progress, token);
                check(result == expectedVersion, "Return the validated version.");
            }
            async Task Reject(Func<Task> action, Type exceptionType, string message)
            {
                Exception? error = null;
                try { await action(); }
                catch (Exception ex) { error = ex; }
                check(error != null && exceptionType.IsInstanceOfType(error), message);
            }
            Version Validate(string path)
            {
                // Validation must see the complete file with the writer closed.
                check(File.ReadAllBytes(path).SequenceEqual(bytes), "Validate the complete, closed staged file.");
                return expectedVersion;
            }
            await Stage(new ByteArrayContent(bytes), bytes.Length, Validate);
            // Exercise the actual streaming reporter while a response pauses, then
            // finishes. No real network, executable installation, or process launch.
            using (var stream = new PausedStream(bytes))
            using (var http = new HttpClient(new Handler((_, _) =>
            {
                var content = new StreamContent(stream);
                content.Headers.ContentLength = bytes.Length;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            })))
            {
                var samples = new ConcurrentQueue<(DownloadProgress Progress, TimeSpan Time)>();
                var positive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var watch = Stopwatch.StartNew();
                var reports = new InlineProgress(sample =>
                {
                    samples.Enqueue((sample, watch.Elapsed));
                    if (sample.KilobytesPerSecond > 0) positive.TrySetResult();
                    else if (positive.Task.IsCompleted) stalled.TrySetResult();
                });
                var download = UpdateDownload.StageAsync(http, "https://test.invalid/update", Path.Combine(folder, "paused.exe"),
                    bytes.Length, Validate, reports, CancellationToken.None);
                try
                {
                    await positive.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await stalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    check(samples.First().Progress.Percent == 25600L * 100 / bytes.Length, "Progress reflects actual received bytes.");
                    check(samples.Last().Progress.KilobytesPerSecond == 0, "Live throughput falls to zero during a stalled read.");
                    check(samples.Count <= watch.Elapsed.TotalMilliseconds / 250 + 1, "Reports are throttled to the 250 ms sampling cadence.");
                }
                finally { stream.Resume.TrySetResult(); }
                check(await download == expectedVersion, "A paused transfer still validates and completes normally.");
                int completedReports = samples.Count;
                await Task.Delay(350);
                check(samples.Count == completedReports, "No progress is emitted after staging completes.");
            }
            await Reject(() => Stage(new ByteArrayContent(Array.Empty<byte>()), bytes.Length, Validate), typeof(InvalidDataException), "Reject empty downloads.");
            await Reject(() => Stage(new ByteArrayContent(bytes), bytes.Length - 1, Validate), typeof(InvalidDataException), "Reject oversized content lengths.");
            foreach (var length in new[] { bytes.Length - 1, bytes.Length + 1 })
            {
                var content = new ByteArrayContent(bytes);
                content.Headers.ContentLength = length;
                await Reject(() => Stage(content, bytes.Length + 1, Validate), typeof(InvalidDataException), "Reject truncated or overlong response bodies.");
            }
            await Reject(() => Stage(new UnknownLengthContent(), bytes.Length, Validate), typeof(InvalidDataException), "Reject a missing content length.");
            await Reject(() => Stage(new ByteArrayContent(bytes), bytes.Length, _ => throw new InvalidDataException("Invalid executable")),
                typeof(InvalidDataException), "Propagate executable validation failures.");
            using (var cancellation = new CancellationTokenSource())
            {
                await Reject(() => Stage(new ByteArrayContent(bytes), bytes.Length, path =>
                {
                    Validate(path);
                    cancellation.Cancel();
                    return expectedVersion;
                }, cancellation.Token), typeof(OperationCanceledException), "Cancellation during validation must not publish a staged update.");
            }
            using (var cancellation = new CancellationTokenSource())
            {
                var stream = new WaitingStream();
                var content = new StreamContent(stream);
                content.Headers.ContentLength = bytes.Length;
                var download = Stage(content, bytes.Length, Validate, cancellation.Token);
                await stream.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                await Reject(() => download.WaitAsync(TimeSpan.FromSeconds(5)), typeof(OperationCanceledException), "Cancel an in-flight body read promptly.");
            }
            using (var cancellation = new CancellationTokenSource())
            {
                var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var http = new HttpClient(new Handler(async (_, token) =>
                {
                    requested.SetResult();
                    await Task.Delay(Timeout.Infinite, token);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }));
                var download = UpdateDownload.StageAsync(http, "https://test.invalid/update", Path.Combine(folder, "pending.exe"),
                    bytes.Length, Validate, progress, cancellation.Token);
                await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                await Reject(() => download.WaitAsync(TimeSpan.FromSeconds(5)), typeof(OperationCanceledException), "Cancel while waiting for response headers.");
            }
            using (var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)))))
                await Reject(() => UpdateDownload.StageAsync(http, "https://test.invalid/update", Path.Combine(folder, "missing.exe"), bytes.Length,
                    Validate, progress, CancellationToken.None), typeof(HttpRequestException), "Reject HTTP errors before staging.");
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class InlineProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
    private sealed class PausedStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == 0) return await base.ReadAsync(buffer[..25600], cancellationToken);
            await Resume.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
    private sealed class UnknownLengthContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
    }
    private sealed class WaitingStream : MemoryStream
    {
        internal TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reading.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
