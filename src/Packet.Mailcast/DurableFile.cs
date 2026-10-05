using System.Runtime.InteropServices;

namespace Packet.Mailcast;

/// <summary>Writes files so that after a crash each is either absent or complete, and stays put.</summary>
internal static class DurableFile
{
    /// <summary>
    /// Writes to a temporary name, flushes it to disk, renames it over the target, then flushes
    /// the folder so the rename itself is on disk before anything written afterwards.
    /// </summary>
    public static void WriteAtomically(string path, ReadOnlySpan<byte> content, DateTimeOffset? lastWrite = null, bool flush = true)
    {
        string tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: flush);
        }
        if (lastWrite is { } time)
        {
            File.SetLastWriteTimeUtc(tmp, time.UtcDateTime);
        }
        File.Move(tmp, path, overwrite: true);
        if (flush)
        {
            FlushDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        }
    }

    /// <summary>fsync on a folder, where the platform has it; Windows has no equivalent and needs none.</summary>
    public static void FlushDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD())
        {
            return;
        }
        int fd = NativeMethods.Open(directory, 0); // O_RDONLY
        if (fd < 0)
        {
            throw new IOException($"Cannot open {directory} to flush it (errno {Marshal.GetLastPInvokeError()}).");
        }
        try
        {
            if (NativeMethods.Fsync(fd) != 0)
            {
                throw new IOException($"Cannot flush {directory} (errno {Marshal.GetLastPInvokeError()}).");
            }
        }
        finally
        {
            _ = NativeMethods.Close(fd);
        }
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
