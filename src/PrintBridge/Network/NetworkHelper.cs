using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PrintBridge.Network;

/// <summary>
/// Network discovery and IP resolution utilities.
/// </summary>
public static class NetworkHelper
{
    /// <summary>
    /// Returns the best local IPv4 address on active Wi-Fi or Ethernet interfaces.
    /// </summary>
    public static string GetLocalIPv4Address()
    {
        try
        {
            var candidates = new List<(int Score, string Address)>();

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback
                    or NetworkInterfaceType.Tunnel)
                    continue;

                var identity = $"{ni.Name} {ni.Description}".ToLowerInvariant();
                if (identity.Contains("vethernet") || identity.Contains("virtual")
                    || identity.Contains("docker") || identity.Contains("wsl")
                    || identity.Contains("vmware") || identity.Contains("virtualbox")
                    || identity.Contains("vbox") || identity.Contains("hyper-v")
                    || identity.Contains("tap-") || identity.Contains("tun")
                    || identity.Contains("vpn") || identity.Contains("loopback")
                    || identity.Contains("bluetooth"))
                {
                    continue;
                }

                int baseScore = ni.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => 100,
                    NetworkInterfaceType.Ethernet => 90,
                    _ => 40
                };

                var ipProps = ni.GetIPProperties();
                bool hasGateway = ipProps.GatewayAddresses.Any(g =>
                    g.Address != null
                    && g.Address.AddressFamily == AddressFamily.InterNetwork
                    && !g.Address.Equals(IPAddress.Any));

                foreach (var ua in ipProps.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    if (IPAddress.IsLoopback(ua.Address))
                        continue;

                    var ipStr = ua.Address.ToString();
                    if (ipStr.StartsWith("169.254.", StringComparison.Ordinal))
                        continue;

                    int score = baseScore;
                    if (hasGateway) score += 20;
                    if (IsPrivateAddress(ua.Address)) score += 10;

                    candidates.Add((score, ipStr));
                }
            }

            if (candidates.Count > 0)
                return candidates.OrderByDescending(c => c.Score).First().Address;
        }
        catch
        {
        }

        return "127.0.0.1";
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;

        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    /// <summary>
    /// Machine hostname.
    /// </summary>
    public static string GetHostName() => Dns.GetHostName();

    /// <summary>
    /// Returns a friendly network summary string for the UI.
    /// </summary>
    public static string GetNetworkSummary(int port)
    {
        var ip = GetLocalIPv4Address();
        var host = GetHostName();
        return $"{host} ({ip}:{port})";
    }
}
