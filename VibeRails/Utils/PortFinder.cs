using System.Net;
using System.Net.Sockets;

namespace VibeRails.Utils;

public static class PortFinder
{
    public static int FindOpenPort(int startPort = 5000, int endPort = 5999)
    {
        for (int port = startPort; port <= endPort; port++)
        {
            if (IsPortAvailable(port))
                return port;
        }

        throw new InvalidOperationException($"No available port found in range {startPort}-{endPort}");
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
