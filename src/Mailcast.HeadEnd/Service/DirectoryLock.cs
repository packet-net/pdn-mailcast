namespace Mailcast.HeadEnd.Service;

/// <summary>
/// Claims a directory for this head end for as long as it runs, so that two head ends on one
/// machine never share a state or drop directory without noticing: a second one stops at start-up
/// and says which setting to change. The claim is a lock on a dot file in the directory (the drop
/// intake skips dot files); the system lets it go when the process ends, however it ends.
/// </summary>
public sealed class DirectoryLock : IDisposable
{
    /// <summary>The lock file's name in each claimed directory.</summary>
    public const string FileName = ".headend.lock";

    private readonly FileStream _file;

    private DirectoryLock(FileStream file) => _file = file;

    /// <summary>
    /// Claims <paramref name="directory"/>, making it if need be. Null, with
    /// <paramref name="problem"/> saying in one line what is wrong and what to change, when another
    /// head end holds it or it cannot be written.
    /// </summary>
    /// <param name="directory">The directory to claim.</param>
    /// <param name="setting">The configuration key that names the directory, for the message.</param>
    /// <param name="problem">Why not, in one line, when the result is null.</param>
    public static DirectoryLock? Take(string directory, string setting, out string? problem)
    {
        string path = Path.Combine(directory, FileName);
        problem = null;
        try
        {
            Directory.CreateDirectory(directory);
            // FileShare.None is an exclusive flock on Linux, held until the file is closed.
            return new DirectoryLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException e) when (Held(e))
        {
            problem = $"another head end is already using {directory}; give this one its own {setting} (leave it out under pdn-mailcast-headend@NAME)";
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problem = $"cannot write in {directory} ({setting}): {Ascii.Plain(e.Message)}; leave {setting} out of the configuration to use the service's own";
            return null;
        }
    }

    // The lock is taken: EWOULDBLOCK from flock (11 on Linux, 35 on macOS), or a sharing violation on Windows.
    private static bool Held(IOException e) =>
        e.GetType() == typeof(IOException) && e.HResult is 11 or 35 or unchecked((int)0x80070020);

    public void Dispose() => _file.Dispose();
}
