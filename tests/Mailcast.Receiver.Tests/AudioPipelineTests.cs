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
        var watching = System.Threading.Channels.Channel.CreateUnbounded<bool>();
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.WatchWaiting += () => watching.Writer.TryWrite(true);
        pipeline.StopWaiting += () => stopping.TrySetResult();
        await pipeline.StartAsync(CancellationToken.None);

        // The watch looks every 5 s of the fake clock. Each time it has set its timer, move the
        // clock on to it; the sixth look is 30 s with no audio.
        for (int look = 1; look <= 6; look++)
        {
            Assert.False(pipeline.Finished.IsCompleted);
            await watching.Reader.ReadAsync();
            time.Advance(TimeSpan.FromSeconds(5));
        }
        await pipeline.Finished;
        Assert.Contains("no audio", pipeline.EndReason, StringComparison.Ordinal);

        // The read is still stuck, so disposing waits StopWait and then leaves the device alone.
        var disposing = pipeline.DisposeAsync().AsTask();
        await stopping.Task;
        time.Advance(AudioPipeline.StopWait);
        await disposing;
        Assert.True(pipeline.LeftStuck);
        Assert.False(input.Disposed);
        lock (log)
        {
            Assert.Contains(log, l => l.Contains("did not return", StringComparison.Ordinal));
        }
        input.Release();
    }
}
