using System.Runtime.InteropServices;
using Mailcast.Receiver;
using Mailcast.Receiver.Web;

const string DefaultConfig = "/etc/pdn-mailcast/receiver.json";

string configPath = DefaultConfig;
string? decode = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config" when i + 1 < args.Length:
            configPath = args[++i];
            break;
        case "--decode" when i + 1 < args.Length:
            decode = args[++i];
            break;
        case "--version":
            Console.WriteLine($"pdn-mailcast-receiver {ReceiverHost.Version}");
            return 0;
        case "--help" or "-h":
            Console.WriteLine(
                $"""
                pdn-mailcast-receiver {ReceiverHost.Version}: hears GB7RDG's daily bulletin broadcast and hands each bulletin to your BBS.

                  --config PATH    the config file (default {DefaultConfig})
                  --decode FILE    decode one WAV recording, deliver what it completes, and exit
                  --version        print the version

                See README.md for the config file and how to set up LinBPQ or FBB.
                """);
            return 0;
        default:
            Console.Error.WriteLine($"unknown argument {args[i]}; try --help");
            return 2;
    }
}

ReceiverConfig config;
try
{
    config = ReceiverConfig.Load(configPath);
}
catch (ConfigException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

void Log(string line) => Console.WriteLine(Ascii.Clean(line));

using var stop = new CancellationTokenSource();
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    stop.Cancel();
});
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
{
    context.Cancel = true;
    stop.Cancel();
});

await using var host = new ReceiverHost(config, TimeProvider.System, Log);
try
{
    if (decode is not null)
    {
        string? failure = await host.DecodeOnceAsync(decode, stop.Token);
        if (failure is not null)
        {
            Log($"decode: not everything was delivered: {failure}");
            return 1;
        }
        Log("decode: done");
        return 0;
    }

    await using var page = new StatusPage(host, configPath, Log);
    try
    {
        page.Start();
    }
    catch (System.Net.HttpListenerException e)
    {
        Log($"web: cannot serve the status page on port {config.Web.Port}: {e.Message}. Another program may have the port; set \"web\".\"port\" to another.");
        return 2;
    }

    await host.RunAsync(stop.Token);
    Log("stopped");
    return 0;
}
catch (AudioSourceException e)
{
    Log(e.Message);
    return 1;
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    Log("stopped");
    return 0;
}
