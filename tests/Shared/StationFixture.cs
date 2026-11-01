using Packet.Mailcast;

namespace PdnMailcast.TestSupport;

/// <summary>
/// The station the tests are about unless they say otherwise: nothing in the product knows any station,
/// so a test that needs one loads this stations file. It is called by the name the tests have always
/// used; the upgrade test (a receiver coming from before stations were data) has its own M0LTE file.
/// </summary>
public static class StationFixture
{
    /// <summary>The fixture station's callsign.</summary>
    public const string Callsign = "GB7RDG";

    /// <summary>Its signing key, in base64 (SubjectPublicKeyInfo).</summary>
    public const string KeyBase64 = "MCowBQYDK2VwAyEAe1ZU+I6EO97joFIDmTGCdGkpAi+CNlfiUe/nJJKlIGo=";

    /// <summary>Its signal centre, Hz.</summary>
    public const long SignalHz = 7_053_800;

    /// <summary>The USB dial that puts the signal at 1800 Hz in the audio, kHz.</summary>
    public const double DialKHz = 7052.0;

    /// <summary>Where it transmits from.</summary>
    public const string Locator = "IO91lk";

    /// <summary>The report address in the fixture file.</summary>
    public const string Report = "MCAST@GB7RDG.#42.GBR.EURO";

    /// <summary>The bulletin a report goes to.</summary>
    public const string ReportTo = "MCAST";

    /// <summary>The @ field of a report.</summary>
    public const string ReportAt = "GB7RDG.#42.GBR.EURO";

    /// <summary>Its key.</summary>
    public static TrustedKey Key { get; } = TrustedKey.Parse(Callsign, KeyBase64);

    /// <summary>A daylight rule: from 2 hours after sunrise to 30 minutes before sunset at the fixture's place.</summary>
    public static DaylightRule Daylight { get; } = new(Locator, 120, 30);

    /// <summary>The stations file as JSON; with <paramref name="daylight"/> false, a station whose every slot runs.</summary>
    public static string Json(bool daylight = true, string callsign = Callsign, string? formerly = null, int minute = 0, int every = 60) => $$"""
        {
          "report": "{{Report}}",
          "stations": [
            {
              "callsign": "{{callsign}}",
              "publicKey": "{{KeyBase64}}",
              "slots": { "minute": {{minute}}, "everyMinutes": {{every}}{{(daylight ? $$""", "daylight": { "locator": "{{Locator}}", "afterSunriseMinutes": 120, "beforeSunsetMinutes": 30 }""" : "")}} },
              "frequencyHz": {{SignalHz}},
              "sideband": "usb",
              "locator": "{{Locator}}",
              "formerly": [{{(formerly is null ? "" : $"\"{formerly}\"")}}]
            }
          ]
        }
        """;

    /// <summary>The fixture station, slots in daylight.</summary>
    public static StationsFile Stations { get; } = StationsFile.Parse(Json(), "fixture");

    /// <summary>The fixture station on other slots, every one running unless <paramref name="daylight"/>.</summary>
    public static StationsFile Custom(int minute = 0, int every = 60, bool daylight = false) => StationsFile.Parse(Json(daylight, minute: minute, every: every), "fixture");

    /// <summary>
    /// Several stations hourly with every slot running, each at its own minute and on the fixture's frequency, each with a
    /// key of its own: <c>("G8AAA", 0), ("G8BBB", 15)</c>.
    /// </summary>
    public static StationsFile Many(params (string Callsign, int Minute)[] stations) => StationsFile.Parse($$"""
        { "report": "{{Report}}", "stations": [ {{string.Join(", ", stations.Select(s => $$"""
          { "callsign": "{{s.Callsign}}", "publicKey": "{{DirectorySigner.FromPem(DirectorySigner.NewKeyPem()).PublicKeyBase64}}",
            "slots": { "minute": {{s.Minute}}, "everyMinutes": 60 }, "frequencyHz": {{SignalHz}}, "sideband": "usb", "locator": "{{Locator}}" }
        """))}} ] }
        """, "fixture");

    /// <summary>The fixture station, every slot running.</summary>
    public static StationsFile EverySlot { get; } = StationsFile.Parse(Json(daylight: false), "fixture");
}
