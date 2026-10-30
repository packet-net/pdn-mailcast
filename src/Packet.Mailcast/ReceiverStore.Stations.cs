using System.Globalization;

namespace Packet.Mailcast;

/// <summary>A station's key as this receiver holds it (issue #178).</summary>
/// <param name="Station">The station's callsign.</param>
/// <param name="Key">Its directory key.</param>
/// <param name="PinnedAt">When it was learnt from the station's first signed directory; null for one built in or in <c>trustedKeys</c>.</param>
/// <param name="FromTrustedKeys">Whether it was pinned because it is in <c>trustedKeys</c> (its label not a callsign): the pin goes when the key does.</param>
/// <param name="Added">Whether it was added by hand on the status page (issue #146), not learnt on the air.</param>
public sealed record PinnedStation(string Station, TrustedKey Key, DateTimeOffset? PinnedAt, bool FromTrustedKeys = false, bool Added = false);

/// <summary>
/// A signed directory that named a station whose key is already pinned, under another key: it
/// is refused (issue #178), and the status page shows it.
/// </summary>
/// <param name="Station">The callsign the directory named.</param>
/// <param name="PinnedKeyId">The key pinned for it.</param>
/// <param name="HeardKeyId">The key the directory was signed with.</param>
/// <param name="HeardAt">When the last such directory was heard.</param>
public sealed record KeyConflict(string Station, ulong PinnedKeyId, ulong HeardKeyId, DateTimeOffset HeardAt);

/// <summary>The newest directory heard from one station (issue #144).</summary>
/// <param name="Station">The station's callsign, as its pinned key names it.</param>
/// <param name="Directory">Its newest directory still remembered: the latest date, and of those the one heard last.</param>
/// <param name="Check">What the directory's signature check found.</param>
/// <param name="HeardAt">When it was last heard; <see cref="DateTimeOffset.MinValue"/> for one kept only as the newest across a restart.</param>
public sealed record StationDirectory(string Station, BroadcastDirectory Directory, DirectoryCheck Check, DateTimeOffset HeardAt);

/// <summary>Stations, their pinned keys, and their directories (issues #178 and #144).</summary>
/// <remarks>
/// <para>
/// Each station signs its own directories, and may have more than one key. GB7RDG's key is
/// built in. Every key in the receiver's <c>trustedKeys</c> is trusted, and pinned to its label
/// when that is a callsign, or else to the callsign its first signed directory names; the label
/// is only what the page shows. Any other station's key is pinned the first
/// time its signed directory is heard: the directory names the station (<c>station=</c>) and
/// gives its key (<c>pubkey=</c>), and if the signature checks against that key and the callsign
/// is not pinned yet, the callsign is pinned to the key in <see cref="PinsFile"/> and the
/// directory is used at once, so the new station's bulletins are delivered straight away. A
/// directory naming a pinned callsign under another key is refused like any bad signature, and
/// kept in <see cref="KeyConflicts"/> for the page. A key already pinned keeps its name: a
/// directory signed by it belongs to that station whatever callsign it names.
/// </para>
/// <para>
/// A trusted signed directory belongs to the station its key is pinned to. An unsigned one
/// belongs to GB7RDG when it names no station or names GB7RDG, exactly as before stations were
/// named; one naming another station belongs to none, since anyone can send an unsigned
/// directory. A directory of no station still names and delivers bulletins as any usable
/// directory does, but gives no station's bulletins up and sets no station's timetable.
/// </para>
/// </remarks>
public sealed partial class ReceiverStore
{
    /// <summary>The station whose key is built in, and that unsigned directories belong to: GB7RDG.</summary>
    public const string BuiltInStation = "GB7RDG";

    /// <summary>Where keys learnt on the air are kept, under the store's folder: one <c>CALL TAB base64 TAB pinned-at</c> line each.</summary>
    public const string PinsFile = "pins.txt";

    // A station may hold several keys: GB7RDG's built-in one and a newer one in trustedKeys, say.
    private const string FromTrustedKeysMark = "trusted";
    private const string AddedMark = "added";

    private readonly List<PinnedStation> _pins = [];
    private readonly Dictionary<string, KeyConflict> _conflicts = new(StringComparer.Ordinal);
    private bool _newPin;

    /// <summary>
    /// Every station's pinned keys: built in, from <c>trustedKeys</c> (pinned to its label when
    /// that is a callsign, otherwise to the callsign its first signed directory names), and
    /// learnt on the air. GB7RDG first, then by callsign.
    /// </summary>
    public IReadOnlyList<PinnedStation> Pins => [.. _pins.OrderBy(p => p.Station != BuiltInStation).ThenBy(p => p.Station, StringComparer.Ordinal)];

    /// <summary>Signed directories refused because they named a station pinned to another key, newest per station.</summary>
    public IReadOnlyList<KeyConflict> KeyConflicts => [.. _conflicts.Values.OrderBy(c => c.Station, StringComparer.Ordinal)];

    /// <summary>The keys directories are checked against: every key in <c>trustedKeys</c> and the built-in one, and every pinned key.</summary>
    public IReadOnlyList<TrustedKey> DirectoryKeys { get; private set; } = [];

    private void LoadPins()
    {
        foreach (var key in _options.TrustedKeys)
        {
            string label = key.Label.ToUpperInvariant();
            if (StationAdvert.IsCallsign(label) && !_pins.Any(p => p.Key.KeyId == key.KeyId))
            {
                _pins.Add(new PinnedStation(label, key, null));
            }
        }
        string file = Path.Combine(_root, PinsFile);
        bool dropped = false;
        if (File.Exists(file))
        {
            foreach (string line in Bulletin.TextEncoding.GetString(File.ReadAllBytes(file)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Split('\t');
                TrustedKey? key = null;
                try
                {
                    key = p.Length >= 3 && StationAdvert.IsCallsign(p[0]) ? TrustedKey.Parse(p[0], p[1]) : null;
                }
                catch (FormatException)
                {
                    key = null;
                }
                if (key is null || !DateTimeOffset.TryParse(p[2], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                {
                    Log($"{PinsFile}: skipped an unreadable line");
                    continue;
                }
                if (p.Length >= 4 && p[3] == FromTrustedKeysMark && !_options.TrustedKeys.Any(k => k.KeyId == key.KeyId))
                {
                    // Pinned because it was in trustedKeys, and it is not any more: the pin goes with it.
                    Log($"{PinsFile}: {p[0]}'s key {KeyId.Format(key.KeyId)} is no longer in trustedKeys, so its pin is removed");
                    dropped = true;
                    continue;
                }
                if (!_pins.Any(x => x.Key.KeyId == key.KeyId))
                {
                    _pins.Add(new PinnedStation(p[0], key, at, p.Length >= 4 && p[3] == FromTrustedKeysMark, p.Length >= 4 && p[3] == AddedMark));
                }
            }
        }
        UpdateKeys();
        if (dropped)
        {
            WritePins("a removed trustedKeys key");
        }
    }

    private void UpdateKeys() => DirectoryKeys = [.. _options.TrustedKeys.Concat(_pins.Select(x => x.Key)).DistinctBy(k => k.KeyId)];

    private void Pin(string station, TrustedKey key, string how, bool fromTrustedKeys = false)
    {
        var now = _options.Time.GetUtcNow();
        _pins.Add(new PinnedStation(station, key, now, fromTrustedKeys));
        UpdateKeys();
        Log($"pinned {station}'s key {KeyId.Format(key.KeyId)}, {how}");
        _newPin = true;
        WritePins(station + "'s key");
    }

    private void WritePins(string what)
    {
        var lines = _pins.Where(x => x.PinnedAt is not null)
            .Select(x => $"{x.Station}\t{Convert.ToBase64String(x.Key.PublicKey)}\t{x.PinnedAt!.Value.ToString("o", CultureInfo.InvariantCulture)}{(x.FromTrustedKeys ? "\t" + FromTrustedKeysMark : x.Added ? "\t" + AddedMark : "")}\n");
        try
        {
            DurableFile.WriteAtomically(Path.Combine(_root, PinsFile), Bulletin.TextEncoding.GetBytes(string.Concat(lines)), flush: _options.FlushToDisk);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"cannot write {PinsFile}, so {what} is kept only until the receiver restarts: {e.Message}");
        }
    }

    /// <summary>
    /// Checks a directory's signature against the pinned keys, pinning a new station's key when
    /// the directory names a callsign not yet pinned and checks against the key it gives.
    /// </summary>
    internal DirectoryCheck CheckDirectory(BroadcastDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var check = CheckSignature(directory);
        if (check.State == SignatureState.Trusted && directory.SigningKeyId is { } trustedId && !_pins.Any(p => p.Key.KeyId == trustedId)
            && directory.Station is { } named && DirectoryKeys.FirstOrDefault(k => k.KeyId == trustedId) is { } configured)
        {
            // A key in trustedKeys whose label is not a callsign: the station is the callsign its directory names,
            // unless that callsign is pinned to another key already (issue #187).
            if (KeyConflictOf(directory, check) is { } conflict)
            {
                return conflict;
            }
            Pin(named, configured, "from trustedKeys, as its first signed directory names it", fromTrustedKeys: true);
            return check;
        }
        if (check.State != SignatureState.Rejected || directory.SigningKeyId is not { } id || DirectoryKeys.Any(k => k.KeyId == id) || directory.Station is not { } station)
        {
            return check; // fine, unsigned, a trusted key's bad signature, or names no station
        }
        if (_pins.FirstOrDefault(p => p.Station == station) is { } pinned)
        {
            _conflicts[station] = new KeyConflict(station, pinned.Key.KeyId, id, _options.Time.GetUtcNow());
            return new DirectoryCheck(SignatureState.Rejected, Reason: $"it names {station}, whose key is pinned as {KeyId.Format(pinned.Key.KeyId)}, but is signed by key {KeyId.Format(id)}");
        }
        if (directory.PublicKey is not { } publicKey || KeyId.Of(publicKey) != id)
        {
            return check;
        }
        var key = TrustedKey.Parse(station, Convert.ToBase64String(publicKey));
        var own = directory.Check([key]);
        if (own.State == SignatureState.Trusted)
        {
            Pin(station, key, "learnt from its first signed directory; its bulletins are delivered from now on");
        }
        return own;
    }

    /// <summary>
    /// The directory's signature against <see cref="DirectoryKeys"/>; an unsigned directory is
    /// refused, not used for anything (issue #146), unless a test says otherwise.
    /// </summary>
    private DirectoryCheck CheckSignature(BroadcastDirectory directory)
    {
        var check = directory.Check(DirectoryKeys);
        return check.State == SignatureState.None && !_options.UnsignedDirectoriesAreUsed ? DirectoryCheck.UnsignedNotUsed : check;
    }

    /// <summary>
    /// The refusal for a directory signed by a trustedKeys key not pinned yet that names a callsign
    /// pinned to another key (issue #187), recorded as a key conflict; null when it is not so.
    /// </summary>
    private DirectoryCheck? KeyConflictOf(BroadcastDirectory directory, DirectoryCheck check)
    {
        if (check.State != SignatureState.Trusted || directory.SigningKeyId is not { } id || directory.Station is not { } named
            || _pins.Any(p => p.Key.KeyId == id) || _pins.FirstOrDefault(p => p.Station == named) is not { } held)
        {
            return null;
        }
        _conflicts[named] = new KeyConflict(named, held.Key.KeyId, id, _options.Time.GetUtcNow());
        return new DirectoryCheck(SignatureState.Rejected, Reason: $"it names {named}, whose key is pinned as {KeyId.Format(held.Key.KeyId)}, but is signed by key {KeyId.Format(id)}");
    }

    /// <summary>
    /// After a key was pinned: directories remembered as refused that it now makes good are used
    /// from now on, and bulletins held because of them are released. Null when nothing was pinned.
    /// </summary>
    private List<Bulletin>? AfterNewPin()
    {
        if (!_newPin)
        {
            return null;
        }
        _newPin = false;
        var released = new List<Bulletin>();
        foreach (var (id, (directory, heardAt, _)) in _rejectedDirectories.ToList())
        {
            var check = CheckSignature(directory);
            if (check.Usable && KeyConflictOf(directory, check) is null)
            {
                _rejectedDirectories.Remove(id);
                _knownDirectories[id] = (directory, heardAt, check);
                NoteAdverts(directory, check);
                released.AddRange(ReleaseWithheld(directory) ?? []);
            }
        }
        return released;
    }

    /// <summary>Which station a directory belongs to, or null for none: see the remarks.</summary>
    public string? StationOf(BroadcastDirectory directory, DirectoryCheck check)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(check);
        return check.State switch
        {
            // A key in trustedKeys not pinned to a callsign, from a head end too old to name itself:
            // its label stands in, as before stations were named.
            SignatureState.Trusted => directory.SigningKeyId is not { } id ? null
                : _pins.FirstOrDefault(p => p.Key.KeyId == id)?.Station
                ?? (directory.Station is null ? DirectoryKeys.FirstOrDefault(k => k.KeyId == id)?.Label : null),
            SignatureState.None => directory.Station is null or BuiltInStation ? BuiltInStation : null,
            _ => null,
        };
    }

    /// <summary>
    /// The newest directory remembered from each station (issue #144): the latest date, and of
    /// those the one heard last. GB7RDG first, then by callsign.
    /// </summary>
    public IReadOnlyList<StationDirectory> StationDirectories()
    {
        var newest = new Dictionary<string, StationDirectory>(StringComparer.Ordinal);
        var all = _knownDirectories.Values.Select(k => (k.Directory, k.HeardAt, k.Check));
        if (Directory is { } held && NewestCheck is { } heldCheck)
        {
            all = all.Append((held, DateTimeOffset.MinValue, heldCheck));
        }
        foreach (var (directory, heardAt, check) in all)
        {
            if (StationOf(directory, check) is not { } station)
            {
                continue;
            }
            if (!newest.TryGetValue(station, out var had) || directory.Date > had.Directory.Date || (directory.Date == had.Directory.Date && heardAt > had.HeardAt))
            {
                newest[station] = new StationDirectory(station, directory, check, heardAt);
            }
        }
        return [.. newest.Values.OrderBy(d => d.Station != BuiltInStation).ThenBy(d => d.Station, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The timetable <paramref name="station"/>'s own directories give: for GB7RDG,
    /// <see cref="HeardSchedule"/>, kept across restarts; for another, that of its newest
    /// remembered directory that gives one, always one signed by its own pinned key. Null when
    /// none does.
    /// </summary>
    public SlotTimetable? HeardTimetable(string station)
    {
        ArgumentNullException.ThrowIfNull(station);
        if (station == BuiltInStation)
        {
            return HeardSchedule;
        }
        return _knownDirectories.Values
            .Where(k => k.Directory.Schedule is not null && k.Check.State == SignatureState.Trusted && StationOf(k.Directory, k.Check) == station)
            .OrderBy(k => k.Directory.Date).ThenBy(k => k.HeardAt)
            .LastOrDefault().Directory?.Schedule;
    }

    /// <summary>
    /// What <paramref name="station"/> says about its own transmitter (issue #179): the
    /// frequency, sideband and locator of its newest remembered trusted directory that gives
    /// any, each part from the newest that gives that part. Null when no directory of its does
    /// (GB7RDG then has the built-in details, see <see cref="BuiltInDetails"/>; any other is
    /// known from its adverts).
    /// </summary>
    public StationDetails? HeardDetails(string station)
    {
        ArgumentNullException.ThrowIfNull(station);
        var newestFirst = _knownDirectories.Values
            .Where(k => k.Directory.Details is not null && k.Check.State == SignatureState.Trusted && StationOf(k.Directory, k.Check) == station)
            .OrderByDescending(k => k.Directory.Date).ThenByDescending(k => k.HeardAt)
            .Select(k => k.Directory.Details!)
            .ToList();
        if (Directory is { Details: not null } held && NewestCheck is { State: SignatureState.Trusted } heldCheck && StationOf(held, heldCheck) == station)
        {
            newestFirst.Add(held.Details!);
        }
        if (newestFirst.Count == 0)
        {
            return null;
        }
        // A newer directory that leaves the locator out does not unsay an older one. The sideband
        // belongs to the frequency: a directory giving hz= without sideband= means usb, never an
        // older directory's sideband.
        var withHz = newestFirst.FirstOrDefault(d => d.FrequencyHz is not null);
        return new StationDetails(
            withHz?.FrequencyHz,
            withHz is not null ? withHz.Sideband ?? Sideband.Usb : newestFirst.Select(d => d.Sideband).FirstOrDefault(v => v is not null),
            newestFirst.Select(d => d.Locator).FirstOrDefault(v => v is not null));
    }

    /// <summary>
    /// What a receiver has always assumed of GB7RDG, used until a directory of its says otherwise:
    /// 7.0538 MHz upper sideband, from IO91lk.
    /// </summary>
    public static StationDetails BuiltInDetails { get; } = new(7_053_800, Sideband.Usb, "IO91lk");

    // The advert taken for each station and place (callsign, frequency, sideband), and the date
    // of the directory it came from.
    private readonly Dictionary<(string Callsign, long FrequencyHz, Sideband Sideband), (StationAdvert Advert, DateOnly Date, DateOnly First)> _adverts = [];

    // Stations advertised only past a directory's first BroadcastDirectory.MostAdverts, with the station whose directory did.
    private readonly Dictionary<string, (string? By, DateOnly Date)> _beyondCap = new(StringComparer.Ordinal);

    /// <summary>
    /// Takes the adverts of a trusted signed directory: a station and place not yet advertised is
    /// added; one already advertised keeps its details unless this directory is dated later and
    /// says something different. So two head ends that disagree on the same day do not make the
    /// listening flip each time either is heard.
    /// </summary>
    private void NoteAdverts(BroadcastDirectory directory, DirectoryCheck check)
    {
        if (check.State != SignatureState.Trusted)
        {
            return;
        }
        foreach (var advert in directory.Adverts)
        {
            var place = (advert.Callsign, advert.FrequencyHz, advert.Sideband);
            if (!_adverts.TryGetValue(place, out var held))
            {
                _adverts[place] = (advert, directory.Date, directory.Date);
            }
            else if (directory.Date > held.Date)
            {
                _adverts[place] = (advert != held.Advert ? advert : held.Advert, directory.Date, held.First);
            }
        }
        foreach (var advert in directory.IgnoredAdverts)
        {
            _beyondCap[advert.Callsign] = (StationOf(directory, check), directory.Date);
        }
    }

    /// <summary>How many days an advert is kept when no directory repeats it (issue #187).</summary>
    public const int AdvertKeptDays = 2;

    /// <summary>Forgets adverts that no directory has repeated for <see cref="AdvertKeptDays"/> days.</summary>
    private void ExpireAdverts()
    {
        var oldest = DateOnly.FromDateTime(_options.Time.GetUtcNow().UtcDateTime).AddDays(-AdvertKeptDays);
        foreach (var place in _adverts.Where(a => a.Value.Date < oldest).Select(a => a.Key).ToList())
        {
            _adverts.Remove(place);
        }
        foreach (var call in _beyondCap.Where(b => b.Value.Date < oldest).Select(b => b.Key).ToList())
        {
            _beyondCap.Remove(call);
        }
    }

    /// <summary>The date of the first directory heard that advertised <paramref name="callsign"/>, or null if none has.</summary>
    public DateOnly? AdvertFirstSeen(string callsign)
    {
        ExpireAdverts();
        return AdvertFirstSeenKept(callsign);
    }

    private DateOnly? AdvertFirstSeenKept(string callsign) =>
        _adverts.Where(a => a.Key.Callsign == callsign).Select(a => (DateOnly?)a.Value.First).Min();

    /// <summary>
    /// Stations only ever advertised past a directory's first <see cref="BroadcastDirectory.MostAdverts"/>
    /// fields, each with the station whose directory did (null if none): not listened at, only shown.
    /// </summary>
    public IReadOnlyList<(string Callsign, string? AdvertisedBy)> AdvertsBeyondCap()
    {
        ExpireAdverts();
        return [.. _beyondCap.Where(b => !_adverts.Keys.Any(k => k.Callsign == b.Key)).Select(b => (b.Key, b.Value.By)).OrderBy(b => b.Key, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The stations the head ends heard tell listeners about (issue #178), one advert for each
    /// station and frequency, from trusted signed directories only (<see cref="NoteAdverts"/>).
    /// A station may be advertised on more than one frequency (HF and QO-100). Hints about where
    /// to listen, never trust.
    /// </summary>
    public IReadOnlyList<StationAdvert> Adverts()
    {
        ExpireAdverts();
        return [.. _adverts.Values.Select(v => v.Advert).OrderBy(a => a.Callsign, StringComparer.Ordinal).ThenBy(a => a.FrequencyHz)];
    }
}
