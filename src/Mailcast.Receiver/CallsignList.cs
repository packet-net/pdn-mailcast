using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mailcast.Receiver;

/// <summary>
/// The base callsigns broadcast frames are accepted from (<see cref="ReceiverConfig.Sources"/>):
/// upper case, SSIDs dropped, no repeats; may be empty. Compared by its callsigns, so two configs
/// read from the same file are equal.
/// </summary>
[JsonConverter(typeof(Converter))]
public sealed class CallsignList : IReadOnlyList<string>, IEquatable<CallsignList>
{
    private readonly string[] _calls;

    private CallsignList(string[] calls) => _calls = calls;

    /// <summary>
    /// Reads callsigns as a person or a config file gives them: "gb7rdg", "M0LTE-1" and "M0LTE"
    /// give GB7RDG and M0LTE. Throws <see cref="ConfigException"/> for one that
    /// is not an AX.25 callsign (1 to 6 letters and digits, with an SSID from 0 to 15 if any).
    /// </summary>
    public static CallsignList Parse(IEnumerable<string?> entries)
    {
        var calls = new List<string>();
        foreach (string? entry in entries)
        {
            string call = BaseCall(entry);
            if (!calls.Contains(call, StringComparer.Ordinal))
            {
                calls.Add(call);
            }
        }
        return new CallsignList([.. calls]);
    }

    private static string BaseCall(string? entry)
    {
        string given = (entry ?? "").Trim().ToUpperInvariant();
        int dash = given.IndexOf('-', StringComparison.Ordinal);
        string call = dash < 0 ? given : given[..dash];
        bool ssidOk = dash < 0 || (int.TryParse(given.AsSpan(dash + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int ssid) && ssid <= 15);
        if (call.Length is < 1 or > 6 || !call.All(char.IsAsciiLetterOrDigit) || !ssidOk)
        {
            throw new ConfigException($"\"sources\": \"{(entry is null ? "null" : Ascii.Clean(entry))}\" is not a callsign: give 1 to 6 letters and digits, such as G4ABC; an SSID (-0 to -15) is allowed and ignored");
        }
        return call;
    }

    /// <summary>
    /// These callsigns followed by <paramref name="more"/> (SSIDs dropped, repeats and anything that is
    /// not a callsign skipped); this list itself when nothing is added.
    /// </summary>
    public CallsignList Plus(IEnumerable<string> more)
    {
        var calls = new List<string>(_calls);
        foreach (string entry in more)
        {
            try
            {
                string call = BaseCall(entry);
                if (!calls.Contains(call, StringComparer.Ordinal))
                {
                    calls.Add(call);
                }
            }
            catch (ConfigException)
            {
                // Not a callsign: nothing to accept.
            }
        }
        return calls.Count == _calls.Length ? this : new CallsignList([.. calls]);
    }

    /// <summary>Whether <paramref name="baseCall"/> (upper case, no SSID) is one of these.</summary>
    public bool Contains(string baseCall) => Array.IndexOf(_calls, baseCall) >= 0;

    /// <inheritdoc/>
    public int Count => _calls.Length;

    /// <inheritdoc/>
    public string this[int index] => _calls[index];

    /// <inheritdoc/>
    public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)_calls).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The callsigns for a person: "G4ABC, M0XYZ"; "none" when empty.</summary>
    public override string ToString() => _calls.Length == 0 ? "none" : string.Join(", ", _calls);

    /// <inheritdoc/>
    public bool Equals(CallsignList? other) => other is not null && _calls.AsSpan().SequenceEqual(other._calls);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as CallsignList);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (string call in _calls)
        {
            hash.Add(call, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }

    /// <summary>A JSON list of strings, read through <see cref="Parse"/>.</summary>
    private sealed class Converter : JsonConverter<CallsignList>
    {
        public override CallsignList Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            const string Shape = "\"sources\" must be a list of callsigns, such as [\"G4ABC\", \"M0XYZ\"]";
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new ConfigException(Shape);
            }
            var entries = new List<string?>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.String)
                {
                    throw new ConfigException(Shape);
                }
                entries.Add(reader.GetString());
            }
            return Parse(entries);
        }

        public override void Write(Utf8JsonWriter writer, CallsignList value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (string call in value)
            {
                writer.WriteStringValue(call);
            }
            writer.WriteEndArray();
        }
    }
}
