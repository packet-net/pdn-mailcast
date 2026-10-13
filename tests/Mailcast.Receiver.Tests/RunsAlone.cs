namespace Mailcast.Receiver.Tests;

/// <summary>
/// Tests that run with nothing else beside them, after the parallel ones: those that fan their
/// work out over the whole thread pool. Run beside the rest, they starved it, and the async
/// loopback fakes (LinBPQ's node, rigctld, pdn-soundmodem, the page's HTTP) missed real
/// timeouts: a different test or two failed on most runs, on Linux and Windows alike.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RunsAlone
{
    /// <summary>The collection's name, for <c>[Collection]</c>.</summary>
    public const string Name = "Alone";
}
