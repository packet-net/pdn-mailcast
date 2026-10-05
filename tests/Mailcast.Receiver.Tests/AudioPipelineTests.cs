using M0LTE.Radio.Audio;
using Microsoft.Extensions.Time.Testing;

namespace Mailcast.Receiver.Tests;

public class AudioPipelineTests
{
    /// <summary>A sound card whose read never returns until it is released, as a wedged driver.</summary>
    private sealed class StuckInput : IAudioInput, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();

        public int SampleRate => OnAir.SampleRate;

        public bool Disposed { get; private set; }

        public int Read(Span<float> destination)
        {
            _release.Wait();
            return 0;
        }

        public void Release() => _release.Set();

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task SilentSource_EndsThePipeline_AndAStuckReadIsLeftBehind()
    {
        var time = new FakeTimeProvider();
        var input = new StuckInput();
        var log = new List<string>();
        var pipeline = AudioPipeline.ForInput(input, line => { lock (log) { log.Add(line); } }, time);
        await pipeline.StartAsync(CancellationToken.None);

        // The watch looks every 5 s of the fake clock; move it on until the pipeline gives up.
        for (int i = 0; i < 100_000 && !pipeline.Finished.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        Assert.True(pipeline.Finished.IsCompleted);
        Assert.Contains("no audio", pipeline.EndReason, StringComparison.Ordinal);

        // The read is still stuck, so disposing waits StopWait and then leaves the device alone.
        var disposing = pipeline.DisposeAsync().AsTask();
        for (int i = 0; i < 100_000 && !disposing.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }
        await disposing;
        Assert.False(input.Disposed);
        lock (log)
        {
            Assert.Contains(log, l => l.Contains("did not return", StringComparison.Ordinal));
        }
        input.Release();
    }
}
