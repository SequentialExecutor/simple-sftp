using System.Net;
using SimpleSftp.Core;

internal static class NetworkAddressChecks
{
    internal static void Run(Action<bool, string> check)
    {
        static NetworkAddresses.LocalAddress Candidate(string ip, bool physical = true, bool gateway = true, int index = 1) => new(IPAddress.Parse(ip), physical, gateway, index);
        check(NetworkAddresses.Select([Candidate("10.0.0.20", false, false), Candidate("192.168.1.25")]) == "192.168.1.25", "Automatic listen address prefers the active LAN adapter over a virtual adapter");
        check(NetworkAddresses.Select([Candidate("192.168.1.8", gateway: false), Candidate("192.168.2.8", index: 2)]) == "192.168.2.8", "Automatic listen address prefers an adapter with a gateway");
        check(NetworkAddresses.Select([Candidate("2001:db8::10"), Candidate("192.168.1.25")]) == "192.168.1.25", "Automatic listen address prefers IPv4 when IPv4 and IPv6 are available");
        check(NetworkAddresses.Select([Candidate("fe80::1"), Candidate("fd00::10")]) == "fd00::10", "Automatic listen address can fall back to usable IPv6 and skips link-local IPv6");
        check(NetworkAddresses.Select([Candidate("0.0.0.0"), Candidate("127.0.0.1"), Candidate("169.254.2.3"), Candidate("224.0.0.1"), Candidate("255.255.255.255")]) == "127.0.0.1", "Offline address detection safely falls back to localhost");
        check(NetworkAddresses.Select([Candidate("192.168.1.9", index: 9), Candidate("192.168.1.4", index: 4)]) == "192.168.1.4", "Multiple eligible network interfaces are selected deterministically");
        check(NetworkAddresses.IsAutomatic(new ServerSettings()) && !NetworkAddresses.IsAutomatic(new ServerSettings { ListenAddress = "0.0.0.0" }) && !NetworkAddresses.IsAutomatic(new ServerSettings { ListenAddress = "10.1.2.3" }), "Legacy localhost defaults migrate to automatic detection while custom addresses remain manual");
        check(!NetworkAddresses.IsAutomatic(new ServerSettings { AutoDetectListenAddress = false }) && NetworkAddresses.IsAutomatic(new ServerSettings { AutoDetectListenAddress = true, ListenAddress = "10.1.2.3" }) && IPAddress.TryParse(NetworkAddresses.Detect(), out _), "Explicit address modes persist and actual adapter detection returns a valid address");
    }
}
