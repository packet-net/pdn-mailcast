// Builds and checks the zstd dictionary for bulletins.
//
//   import-bpq <bpq-dir> <out-dir>       bulletins from a copy of a LinBPQ mail store, one
//                                        file each, named yyyyMMdd-BID.bulletin
//   train <dir> <out.zdict> [options]    train a dictionary on the bulletin files in a directory
//       --size <bytes>                   dictionary size (default 32768)
//       --exclude-from <yyyy-MM-dd>      leave out bulletins dated from this day ...
//       --exclude-to <yyyy-MM-dd>        ... to this day, to hold them out for evaluation
//   evaluate <dir> [options]             compressed sizes, each bulletin on its own
//       --dictionary <file.zdict>        compare against this dictionary
//       --from <yyyy-MM-dd> --to <yyyy-MM-dd>   only bulletins dated in this range
//
// A bulletin file is Bulletin.Serialize(), the form that is compressed and broadcast. Files
// whose text contains a 7plus header can be left out of training with --no-7plus.

using System.Globalization;
using System.IO.Compression;
using Mailcast.Core;
using Mailcast.DictionaryTool;
using ZstdSharp;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: import-bpq <bpq-dir> <out-dir> | train <dir> <out.zdict> [--size N] [--exclude-from D --exclude-to D] [--no-7plus] | evaluate <dir> [--dictionary F] [--from D --to D]");
    return 2;
}

switch (args[0])
{
    case "import-bpq":
        return ImportBpq(args[1], args[2]);
    case "train":
        return Train(args[1], args[2], Options(args[3..]));
    case "evaluate":
        return Evaluate(args[1], Options(args[2..]));
    default:
        Console.Error.WriteLine($"unknown command {args[0]}");
        return 2;
}

static Dictionary<string, string> Options(string[] rest)
{
    var options = new Dictionary<string, string>();
    for (int i = 0; i < rest.Length; i++)
    {
        if (rest[i] == "--no-7plus")
        {
            options["no-7plus"] = "yes";
        }
        else if (rest[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < rest.Length)
        {
            options[rest[i][2..]] = rest[++i];
        }
        else
        {
            throw new ArgumentException($"unexpected argument {rest[i]}");
        }
    }
    return options;
}

static int ImportBpq(string bpqDirectory, string outDirectory)
{
    Directory.CreateDirectory(outDirectory);
    int count = 0;
    foreach (var bulletin in BpqStore.ReadBulletins(bpqDirectory, Console.Error))
    {
        string safeBid = string.Concat(bulletin.Bid.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' ? c : '_'));
        string name = $"{bulletin.Date.UtcDateTime:yyyyMMdd}-{safeBid}.bulletin";
        File.WriteAllBytes(Path.Combine(outDirectory, name), bulletin.Serialize());
        count++;
    }
    Console.WriteLine($"wrote {count} bulletins to {outDirectory}");
    return 0;
}

static List<(string Name, DateOnly Date, byte[] Bytes)> Load(string directory, Dictionary<string, string> options)
{
    var files = new List<(string, DateOnly, byte[])>();
    foreach (var path in Directory.EnumerateFiles(directory, "*.bulletin").Order(StringComparer.Ordinal))
    {
        var bytes = File.ReadAllBytes(path);
        var bulletin = Bulletin.Parse(bytes);
        var date = DateOnly.FromDateTime(bulletin.Date.UtcDateTime);
        if (options.TryGetValue("from", out var from) && date < DateOnly.Parse(from, CultureInfo.InvariantCulture))
        {
            continue;
        }
        if (options.TryGetValue("to", out var to) && date > DateOnly.Parse(to, CultureInfo.InvariantCulture))
        {
            continue;
        }
        if (options.TryGetValue("exclude-from", out var xf) && options.TryGetValue("exclude-to", out var xt)
            && date >= DateOnly.Parse(xf, CultureInfo.InvariantCulture) && date <= DateOnly.Parse(xt, CultureInfo.InvariantCulture))
        {
            continue;
        }
        if (options.ContainsKey("no-7plus") && Is7plus(bulletin))
        {
            continue;
        }
        files.Add((Path.GetFileName(path), date, bytes));
    }
    return files;
}

static bool Is7plus(Bulletin bulletin) => bulletin.Body.Contains(" go_7+.", StringComparison.Ordinal);

static int Train(string directory, string output, Dictionary<string, string> options)
{
    int size = options.TryGetValue("size", out var s) ? int.Parse(s, CultureInfo.InvariantCulture) : 32768;
    var samples = Load(directory, options);
    var dictionary = DictBuilder.TrainFromBuffer(samples.Select(f => f.Bytes), size);
    File.WriteAllBytes(output, dictionary);
    Console.WriteLine($"trained a {dictionary.Length}-octet dictionary on {samples.Count} bulletins ({samples.Sum(f => f.Bytes.Length)} octets) into {output}");
    return 0;
}

static int Evaluate(string directory, Dictionary<string, string> options)
{
    var files = Load(directory, options);
    var plain = new Compression([]);
    Compression? withDictionary = null;
    if (options.TryGetValue("dictionary", out var path))
    {
        withDictionary = new Compression([new ZstdDictionary(1, File.ReadAllBytes(path))]);
    }

    long raw = 0, gzip = 0, zstd = 0, dict = 0, symbols = 0, frames = 0;
    var schedule = new ScheduleOptions();
    foreach (var (name, _, bytes) in files)
    {
        if (!Bulletin.Parse(bytes).Serialize().AsSpan().SequenceEqual(bytes))
        {
            throw new InvalidOperationException($"{name} does not round-trip");
        }
        raw += bytes.Length;
        gzip += GzipLength(bytes);
        zstd += plain.Compress(bytes, Compression.NoDictionary).Length;
        if (withDictionary is not null)
        {
            var compressed = withDictionary.Compress(bytes, 1);
            if (!withDictionary.Decompress(compressed, 1).AsSpan().SequenceEqual(bytes))
            {
                throw new InvalidOperationException("round trip failed");
            }
            dict += compressed.Length;
            int k = (int)((compressed.Length + 1 + MailcastFrame.StandardSymbolSize - 1) / MailcastFrame.StandardSymbolSize);
            symbols += k;
            frames += BroadcastScheduler.SymbolsPerDay(k, schedule).Sum();
        }
    }

    Console.WriteLine($"{files.Count} bulletins, each compressed on its own");
    Console.WriteLine($"  raw              {raw,10} octets  100.0%");
    Console.WriteLine($"  gzip -9          {gzip,10} octets  {Percent(gzip, raw)}");
    Console.WriteLine($"  zstd -19         {zstd,10} octets  {Percent(zstd, raw)}");
    if (withDictionary is not null)
    {
        Console.WriteLine($"  zstd -19 + dict  {dict,10} octets  {Percent(dict, raw)}");
        Console.WriteLine($"with the dictionary, {symbols} symbols of {MailcastFrame.StandardSymbolSize} octets (K summed), {frames} frames over each bulletin's three days");
    }
    return 0;
}

static string Percent(long part, long whole) => (100.0 * part / whole).ToString("F1", CultureInfo.InvariantCulture) + "%";

static int GzipLength(byte[] data)
{
    using var buffer = new MemoryStream();
    using (var gz = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
    {
        gz.Write(data);
    }
    return (int)buffer.Length;
}
