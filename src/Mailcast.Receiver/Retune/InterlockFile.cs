using System.Runtime.InteropServices;
using System.Text.Json;

namespace Mailcast.Receiver.Retune;

/// <summary>
/// The receiver's note that it may have turned LinBPQ's transmit off: written, flushed to disk
/// and renamed into place before <c>XMITOFF port 1</c> is sent and again before the rig is tuned,
/// and deleted only once <c>XMITOFF port 0</c> has been confirmed. Found at start-up, it means the
/// receiver stopped part way through a slot.
/// </summary>
public static class InterlockFile
{
    /// <summary>The file's name in the state directory.</summary>
    public const string Name = "interlock.json";

    /// <summary>What the file holds.</summary>
    /// <param name="Stage">"transmitOff" before XMITOFF is sent, "tuned" before the rig is tuned.</param>
    /// <param name="Node">LinBPQ's node, host:port.</param>
    /// <param name="HfPort">The LinBPQ port turned off.</param>
    /// <param name="Slot">The slot it was for.</param>
    /// <param name="Written">When the file was written.</param>
    /// <param name="SysopHeld">
    /// The port's transmit was already off before the slot (the sysop's doing), so it is left
    /// off afterwards rather than turned on.
    /// </param>
    public sealed record Note(string Stage, string Node, int HfPort, DateTimeOffset Slot, DateTimeOffset Written, bool SysopHeld = false);

    /// <summary>The file's path in <paramref name="stateDirectory"/>.</summary>
    public static string PathIn(string stateDirectory) => System.IO.Path.Combine(stateDirectory, Name);

    /// <summary>Writes the note so that after a crash the file is either the old one or this one, whole.</summary>
    public static void Write(string path, Note note)
    {
        string? directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }
        string tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(note, ReceiverConfig.Json));
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
        if (directory is not null)
        {
            FlushDirectory(directory);
        }
    }

    /// <summary>
    /// The note left in <paramref name="path"/>, or null when there is none. A file that cannot be
    /// read still says the receiver stopped part way, so it comes back as a note with what is known.
    /// </summary>
    public static Note? Read(string path, int hfPort)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<Note>(File.ReadAllText(path), ReceiverConfig.Json) is { HfPort: > 0, Stage: not null } note
                ? note
                : new Note("unknown", "", hfPort, default, default);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Note("unknown", "", hfPort, default, default);
        }
    }

    /// <summary>Removes the file, if it is there.</summary>
    public static void Delete(string path)
    {
        File.Delete(path);
        File.Delete(path + ".tmp");
    }

    /// <summary>fsync on the folder, so the rename itself is on disk.</summary>
    private static void FlushDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        int fd = NativeMethods.Open(directory, 0);
        if (fd < 0)
        {
            return;
        }
        _ = NativeMethods.Fsync(fd);
        _ = NativeMethods.Close(fd);
    }

    private static class NativeMethods
    {
        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        public static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        public static extern int Fsync(int fd);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        public static extern int Close(int fd);
    }
}
