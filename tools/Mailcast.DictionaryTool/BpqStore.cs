using System.Buffers.Binary;
using Mailcast.Core;

namespace Mailcast.DictionaryTool;

/// <summary>
/// Reads bulletins from a copy of a LinBPQ mail store: the headers from DIRMES.SYS (an array of
/// 308-octet struct MsgInfo records, little-endian, the first a control record) and the text
/// from Mail/m_NNNNNN.mes. Offsets as bpqmail.h, as read by pdn-bbs's importer.
/// </summary>
internal static class BpqStore
{
    private const int RecordSize = 308;

    public static IEnumerable<Bulletin> ReadBulletins(string bpqDirectory, TextWriter log)
    {
        var dirmes = File.ReadAllBytes(Path.Combine(bpqDirectory, "DIRMES.SYS"));
        if (dirmes.Length % RecordSize != 0)
        {
            throw new InvalidDataException($"DIRMES.SYS is {dirmes.Length} octets, not a whole number of {RecordSize}-octet records; is it the current layout?");
        }
        for (int i = 1; i < dirmes.Length / RecordSize; i++)
        {
            var record = dirmes.AsSpan(i * RecordSize, RecordSize);
            char type = (char)record[0];
            int number = BinaryPrimitives.ReadInt32LittleEndian(record[2..]);
            if (type != 'B' || number == 0)
            {
                continue;
            }
            var path = Path.Combine(bpqDirectory, "Mail", $"m_{number:D6}.mes");
            if (!File.Exists(path))
            {
                continue;
            }
            Bulletin bulletin;
            try
            {
                long created = BinaryPrimitives.ReadInt64LittleEndian(record[255..]);
                bulletin = Bulletin.FromMessageText(
                    type,
                    CString(record.Slice(62, 7)),
                    CString(record.Slice(69, 7)),
                    CString(record.Slice(21, 41)),
                    CString(record.Slice(76, 13)),
                    CString(record.Slice(89, 61)),
                    DateTimeOffset.FromUnixTimeSeconds(created),
                    Bulletin.TextEncoding.GetString(File.ReadAllBytes(path)));
            }
            catch (ArgumentException e)
            {
                log.WriteLine($"skipped message {number}: {e.Message}");
                continue;
            }
            yield return bulletin;
        }
    }

    private static string CString(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        return Bulletin.TextEncoding.GetString(end < 0 ? field : field[..end]).Trim();
    }
}
