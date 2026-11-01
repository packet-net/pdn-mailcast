using System.Runtime.CompilerServices;

namespace Packet.Mailcast.Tests;

/// <summary>Nothing in the product knows a station: the store tests that need one have the fixture station (in daylight).</summary>
internal static class FixtureStations
{
    [ModuleInitializer]
    internal static void Use() => ReceiverStoreOptions.DefaultStations = StationFixture.Stations;
}
