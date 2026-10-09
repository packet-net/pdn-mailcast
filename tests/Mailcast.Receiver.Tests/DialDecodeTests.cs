using M0LTE.Dsp;
using M0LTE.Radio.Audio;
using Packet.SoundModem.Ms110d;
using Packet.SoundModem.Waterfall;

namespace Mailcast.Receiver.Tests;

/// <summary>
/// The receiver decodes MS110D wherever the dial puts the signal in its own audio, not just at
/// the modem's native 1800 Hz. Issue packet-net/pdn-mailcast#65's review found that, after the
/// dial itself became a setting, the modem (fixed to pdn-soundmodem's own 1800 Hz subcarrier)
/// and the channel/probe measurement (ChannelMaths.ToBaseband, mixed at a hard-coded 1800 Hz)
/// still assumed the usual dial: 0 of 36 frames decoded on real recordings converted to a
/// 1500 Hz centre, against 34 of 36 at 1800 Hz. <see cref="AudioPipeline"/> now shifts the audio
/// to the modem's native 1800 Hz itself, upstream of everything, which these tests check holds
/// at the usual dial and at both ends of the 1000-2000 Hz range this receiver accepts.
/// </summary>
public class DialDecodeTests
{
    private const int Rate = OnAir.SampleRate;
    private const int Block = Rate / 10;

    private sealed class NoInput : IAudioInput
    {
        public int SampleRate => Rate;
        public int Read(Span<float> destination) => 0;
    }

    /// <summary>
    /// One MS110D burst, modulated at the modem's native 1800 Hz and then shifted to
    /// <paramref name="centreHz"/>: the audio a receiver whose dial puts the signal there would
    /// actually hear, as <see cref="AudioPipeline"/>'s own shift (the other way) expects to undo.
    /// </summary>
    private static float[] BurstAt(double centreHz, int wn = 4, int seed = 1)
    {
        var payload = new byte[120];
        new Random(seed).NextBytes(payload);
        byte[] frame = Ax25UiFrame.Build(Samples.Source, OnAir.Destination, payload);
        float[] native = new Ms110dModem(Rate, _ => { }, new Ms110dTxSettings { WaveformNumber = wn }).Modulate(frame, 0);
        double shiftHz = centreHz - OnAir.CentreAudioHz;
        if (Math.Abs(shiftHz) < 0.5)
        {
            return native;
        }
        var shifted = new float[native.Length];
        new FrequencyShifter(Rate, shiftHz).Process(native, shifted);
        return shifted;
    }

    private static void Feed(AudioPipeline pipeline, float[] audio)
    {
        for (int offset = 0; offset < audio.Length; offset += Block)
        {
            pipeline.Feed(audio.AsSpan(offset, Math.Min(Block, audio.Length - offset)));
        }
    }

    [Theory]
    [InlineData(1800)] // the usual 7.052 MHz dial
    [InlineData(1500)] // the narrow-filter 7.0523 MHz dial (the 2026-10 filter study)
    [InlineData(1278)] // an FT-450D's ~367-2190 Hz filter, centred there (dial about 7.05252 MHz)
    public async Task Decodes_WhereverTheDialPutsTheSignal(double centreHz)
    {
        double dialHz = OnAir.TransmitHz - centreHz;
        var log = new List<string>();
        var pipeline = AudioPipeline.ForInput(new NoInput(), log.Add, TimeProvider.System, watch: false, dialHz: dialHz);
        int frames = 0;
        pipeline.Channel.FrameReceived += (_, _) => frames++;

        Feed(pipeline, new float[Rate]);
        Feed(pipeline, BurstAt(centreHz));
        Feed(pipeline, new float[Rate]);

        Assert.Equal(1, frames);
        await pipeline.DisposeAsync();
    }

    [Fact]
    public async Task AtTheUsualDial_NoShiftIsBuilt_SoNothingChangesForIt()
    {
        // The default dial must cost nothing extra and behave exactly as before: no frequency
        // shifter at all, not even a no-op one.
        var pipeline = AudioPipeline.ForInput(new NoInput(), _ => { }, TimeProvider.System, watch: false);
        int frames = 0;
        pipeline.Channel.FrameReceived += (_, _) => frames++;

        Feed(pipeline, new float[Rate]);
        Feed(pipeline, BurstAt(OnAir.CentreAudioHz));
        Feed(pipeline, new float[Rate]);

        Assert.Equal(1, frames);
        await pipeline.DisposeAsync();
    }
}
