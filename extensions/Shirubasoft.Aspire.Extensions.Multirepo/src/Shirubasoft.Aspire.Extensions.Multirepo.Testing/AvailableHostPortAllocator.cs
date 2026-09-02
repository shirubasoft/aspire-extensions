using System.Net;
using System.Net.Sockets;

namespace Aspire.Hosting.Testing;

internal static class AvailableHostPortAllocator
{
    private static readonly object Sync = new();
    private static readonly HashSet<int> AllocatedPorts = [];

    public static int Allocate()
    {
        lock (Sync)
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                if (AllocatedPorts.Add(port))
                {
                    return port;
                }
            }
        }

        throw new InvalidOperationException("Unable to allocate a unique loopback port for the Compose test endpoint.");
    }
}
