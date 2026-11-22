using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SimpleSftp.Core;

public static class NetworkAddresses
{
    public static string Detect()
    {
        var candidates = new List<LocalAddress>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                try
                {
                    var properties = adapter.GetIPProperties();
                    var gateway = properties.GatewayAddresses.Any(g => !IPAddress.IsLoopback(g.Address) && !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));
                    var physical = adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211;
                    var index = adapter.Supports(NetworkInterfaceComponent.IPv4) ? properties.GetIPv4Properties()?.Index ?? int.MaxValue : int.MaxValue;
                    foreach (var address in properties.UnicastAddresses)
                        candidates.Add(new(address.Address, physical, gateway, index));
                }
                catch (NetworkInformationException) { }
                catch (PlatformNotSupportedException) { }
            }
        }
        catch (NetworkInformationException) { }
        return Select(candidates);
    }

    internal sealed record LocalAddress(IPAddress Address, bool EthernetOrWifi, bool HasGateway, int Index);
    internal static string Select(IEnumerable<LocalAddress> addresses)
    {
        return addresses.Where(a => IsUsable(a.Address))
            .OrderBy(a => a.Address.AddressFamily != AddressFamily.InterNetwork)
            .ThenBy(a => !a.EthernetOrWifi).ThenBy(a => !a.HasGateway).ThenBy(a => a.Index)
            .ThenBy(a => a.Address.ToString(), StringComparer.Ordinal)
            .Select(a => a.Address.ToString()).FirstOrDefault() ?? IPAddress.Loopback.ToString();
    }
    private static bool IsUsable(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return !address.IsIPv6LinkLocal && !address.IsIPv6Multicast;
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] != 0 && bytes[0] < 224 && !(bytes[0] == 169 && bytes[1] == 254);
    }

    public static bool IsAutomatic(ServerSettings settings) => settings.AutoDetectListenAddress ??
        (string.IsNullOrWhiteSpace(settings.ListenAddress) || IPAddress.TryParse(settings.ListenAddress, out var ip) && IPAddress.IsLoopback(ip));
}
