using Packet.Mailcast;
using Mailcast.HeadEnd.Intake;
using Mailcast.HeadEnd.Offline;
using Mailcast.HeadEnd.Planning;
using Mailcast.HeadEnd.Slot;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Modems;

namespace Mailcast.HeadEnd.Tests;

public class OfflineTests
{
    [Fact]
    public void Wav_DecodesThroughPdnSoundmodemBackToEveryBulletin()
    {
        var day = new DateOnly(2026, 10, 5);
        using var state = new TempDirectory();
        using var rx = new TempDirectory();
        using var output = new TempDirectory();
        var journal = new MemoryJournal();
        var options = new ScheduleOptions();
        var store = new RotationStore(state.Path, Compression.Default, options, journal);
        var bulletins = new[] { Bulletins.Make(31, 1200), Bulletins.Make(32, 2500) };
        foreach (var b in bulletins)
        {
            store.Offer(b, day);
        }
        var plan = new StoreSlotPlanner(store, Compression.Default, options).Plan(new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc)));

        string wav = Path.Combine(output.Path, "slot.wav");
        var settings = new SlotSettings { ToneLength = TimeSpan.FromSeconds(3), PauseAfterTone = TimeSpan.FromSeconds(4) };
        var summary = new WavRenderer(settings, "ms110d-wn4", 48000).Render(plan.Frames, wav, new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc)));
        Assert.True(summary.Tone);
        Assert.True(summary.Probe);
        Assert.Equal(plan.Frames.Count, summary.Frames);
        Assert.True(summary.Idents >= 1);

        // Decoded by the same package the receiver embeds, then rebuilt by the core's receiver store.
        var heard = new List<byte[]>();
        IModem modem = ModemCatalog.Create("ms110d-wn4", 48000, heard.Add);
        var (audio, rate) = WavFile.ReadMono(wav, 0);
        Assert.Equal(48000, rate);
        for (int i = 0; i < audio.Length; i += 4800)
        {
            modem.Process(audio.AsSpan(i, Math.Min(4800, audio.Length - i)));
        }
        modem.Process(new float[48000]);
        Assert.Equal(plan.Frames.Count, heard.Count);

        var receiver = new ReceiverStore(rx.Path, Compression.Default);
        var rebuilt = new List<Bulletin>();
        foreach (byte[] ax25 in heard)
        {
            Assert.Equal(Station.Ax25Ui.Encode("MCAST", "GB7RDG", []).AsSpan(0, 16).ToArray(), ax25[..16]);
            var result = receiver.Accept(ax25.AsSpan(16));
            if (result.Bulletin is not null)
            {
                rebuilt.Add(result.Bulletin);
            }
        }
        Assert.Equal(bulletins.OrderBy(b => b.Bid), rebuilt.OrderBy(b => b.Bid));
    }
}
