using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Fyltrix.Core.Sharing;

/// <summary>An address of this PC that other devices may reach, with what is known about its adapter.</summary>
public sealed record LanAddress(IPAddress Address, string Adapter, string Kind, bool HasGateway, bool Virtual)
{
    public string Display => $"{Address}  ·  {Adapter} ({Kind}{(Virtual ? ", virtual" : "")})";
}

/// <summary>
/// Picks the addresses to put in share links. The adapter Windows lists first is often a virtual one
/// (WSL/Hyper-V vEthernet, VirtualBox, VPN), whose address no phone or other PC can reach; real Wi-Fi/Ethernet
/// adapters with a default gateway come first instead.
/// </summary>
public static class ShareNetwork
{
    private static readonly string[] VirtualMarkers =
    {
        "hyper-v", "vethernet", "virtual", "vmware", "virtualbox", "docker", "wsl", "tap-", "tap ", "tun", "vpn", "wireguard",
        "tailscale", "zerotier", "hamachi", "npcap", "loopback", "pseudo", "bluetooth", "wan miniport", "teredo", "isatap",
    };

    public static List<LanAddress> LanAddresses()
    {
        var list = new List<LanAddress>();
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return list; }
        foreach (var n in nics)
        {
            if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            IPInterfaceProperties props;
            try { props = n.GetIPProperties(); } catch (NetworkInformationException) { continue; }
            bool gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            var text = (n.Name + " " + n.Description).ToLowerInvariant();
            bool isVirtual = VirtualMarkers.Any(text.Contains);
            var kind = n.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "wired",
                NetworkInterfaceType.Ppp => "dial-up/VPN",
                _ => n.NetworkInterfaceType.ToString(),
            };
            foreach (var u in props.UnicastAddresses)
            {
                var a = u.Address;
                if (a.AddressFamily != AddressFamily.InterNetwork || !IsPrivate(a) || IPAddress.IsLoopback(a)) continue;
                list.Add(new LanAddress(a, n.Name, kind, gateway, isVirtual));
            }
        }
        return Rank(list);
    }

    /// <summary>Real adapters with a gateway first, then other real ones, link-local (169.254) and virtual adapters last.</summary>
    internal static List<LanAddress> Rank(IEnumerable<LanAddress> addresses) =>
        addresses.GroupBy(a => a.Address).Select(g => g.First())
            .OrderBy(a => a.Virtual)
            .ThenBy(a => IsLinkLocal(a.Address))
            .ThenByDescending(a => a.HasGateway)
            .ThenBy(a => a.Kind == "wired" ? 0 : a.Kind == "Wi-Fi" ? 1 : 2)
            .ToList();

    private static bool IsLinkLocal(IPAddress a) => a.AddressFamily == AddressFamily.InterNetwork && a.GetAddressBytes() is [169, 254, ..];

    /// <summary>Loopback, RFC 1918, link-local and CGNAT ranges (and IPv6 equivalents).</summary>
    public static bool IsPrivate(IPAddress a)
    {
        if (IPAddress.IsLoopback(a)) return true;
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily == AddressFamily.InterNetworkV6) return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || (a.GetAddressBytes()[0] & 0xFE) == 0xFC;
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }
}

/// <summary>What Windows Defender Firewall will probably do with connections from other devices.</summary>
public sealed record FirewallReport(bool Known, bool Enabled, string Profiles, bool PublicNetwork, bool BlocksByDefault, bool HasAllowRule, bool LocalRulesIgnored)
{
    public static readonly FirewallReport Unknown = new(false, false, "", false, false, false, false);

    public bool LikelyBlocked => Known && Enabled && BlocksByDefault && (!HasAllowRule || LocalRulesIgnored);

    public string Explain()
    {
        if (!Known) return $"{ProductBranding.Name} couldn't read the Windows Firewall settings. If other devices can't open the link, check the firewall.";
        if (!Enabled) return "Windows Firewall is off for this network, so it won't block the link.";
        if (LocalRulesIgnored)
            return $"This network uses the {Profiles} firewall profile, and your organisation's policy ignores firewall rules made on this PC for it. " +
                   "Other devices will be blocked. Ask your IT administrator, or connect to a network marked as Private.";
        if (HasAllowRule) return $"A Windows Firewall rule allows {ProductBranding.Name} on this network ({Profiles} profile).";
        return PublicNetwork
            ? $"This network is marked as Public, so Windows Firewall blocks incoming connections. Mark your home or office network as Private in Windows Settings, then allow {ProductBranding.Name} through the firewall."
            : $"Windows Firewall blocks incoming connections on this network ({Profiles} profile) unless {ProductBranding.Name} is allowed. Use \"Allow through firewall…\" (needs administrator approval).";
    }
}

/// <summary>Reads Windows Defender Firewall state (no admin needed) and adds a narrow allow rule with the user's consent (UAC).</summary>
public static class ShareFirewall
{
    public const string RuleName = "FileLens file sharing";
    private const int ProfileDomain = 1, ProfilePrivate = 2, ProfilePublic = 4;

    public static FirewallReport Check(int port, string? exePath)
    {
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type == null) return FirewallReport.Unknown;
            var fw = Activator.CreateInstance(type)!;
            int current = Get<int>(fw, "CurrentProfileTypes");
            bool enabled = false, blocks = false;
            foreach (var p in new[] { ProfileDomain, ProfilePrivate, ProfilePublic }.Where(p => (current & p) != 0))
            {
                if (Get<bool>(fw, "FirewallEnabled", p)) enabled = true;
                if (Get<int>(fw, "DefaultInboundAction", p) == 0) blocks = true; // NET_FW_ACTION_BLOCK
            }
            // NET_FW_MODIFY_STATE: 0 OK, 1 group policy overrides, 2 inbound blocked by policy. It misses Intune (MDM)
            // policy, so the policy stores' "merge local rules" switch is read too.
            int modify = Get<int>(fw, "LocalPolicyModifyState");
            if (modify == 0 && LocalRulesDisabledByPolicy(current)) modify = 1;
            bool hasRule = false;
            var rules = Get<object>(fw, "Rules");
            if (rules is System.Collections.IEnumerable all)
                foreach (var r in all)
                {
                    try { if (RuleAllows(r, current, port, exePath)) { hasRule = true; break; } }
                    finally { if (Marshal.IsComObject(r)) Marshal.ReleaseComObject(r); }
                }
            var names = new List<string>();
            if ((current & ProfileDomain) != 0) names.Add("Domain");
            if ((current & ProfilePrivate) != 0) names.Add("Private");
            if ((current & ProfilePublic) != 0) names.Add("Public");
            return new FirewallReport(true, enabled, string.Join("/", names), (current & ProfilePublic) != 0, blocks, hasRule, modify != 0);
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or InvalidCastException or UnauthorizedAccessException or MissingMethodException)
        {
            Diagnostics.Log("Sharing firewall check", ex);
            return FirewallReport.Unknown;
        }
    }

    /// <summary>True when Group Policy or Intune (MDM) tells the firewall to ignore rules made on this PC for a current profile.</summary>
    private static bool LocalRulesDisabledByPolicy(int currentProfiles)
    {
        var stores = new (int profile, string gp, string mdm)[] { (ProfileDomain, "DomainProfile", "DomainProfile"), (ProfilePrivate, "PrivateProfile", "StandardProfile"), (ProfilePublic, "PublicProfile", "PublicProfile") };
        foreach (var (profile, gp, mdm) in stores.Where(s => (currentProfiles & s.profile) != 0))
            foreach (var key in new[] { @"SOFTWARE\Policies\Microsoft\WindowsFirewall\" + gp, @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\Mdm\" + mdm })
            {
                try
                {
                    using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
                    if (k?.GetValue("AllowLocalPolicyMerge") is int v && v == 0) return true;
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
        return false;
    }

    private static bool RuleAllows(object r, int currentProfiles, int port, string? exe)
    {
        if (!Get<bool>(r, "Enabled") || Get<int>(r, "Direction") != 1 || Get<int>(r, "Action") != 1) return false; // inbound, allow
        if ((Get<int>(r, "Profiles") & currentProfiles) == 0) return false;
        // rules for Store apps (package SID) or for a Windows service never apply to Fyltrix.exe
        if (!string.IsNullOrEmpty(TryGet<string>(r, "LocalAppPackageId")) || !string.IsNullOrEmpty(TryGet<string>(r, "serviceName"))) return false;
        int protocol = Get<int>(r, "Protocol");
        if (protocol is not (6 or 256)) return false; // TCP or any
        var app = Get<string?>(r, "ApplicationName");
        var ports = Get<string?>(r, "LocalPorts");
        bool anyPort = string.IsNullOrEmpty(ports) || ports == "*";
        if (!string.IsNullOrEmpty(app))
            return exe != null && string.Equals(Environment.ExpandEnvironmentVariables(app), exe, StringComparison.OrdinalIgnoreCase) && (anyPort || PortListContains(ports!, port));
        // No program: only a rule that names the port counts. Program-less "any port" rules are almost always Store-app
        // rules scoped by package SID (not visible through this interface), which never apply to Fyltrix.exe.
        return !anyPort && PortListContains(ports!, port);
    }

    internal static bool PortListContains(string list, int port)
    {
        foreach (var part in list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = part.IndexOf('-');
            if (dash > 0 && int.TryParse(part[..dash], out var a) && int.TryParse(part[(dash + 1)..], out var b) && port >= a && port <= b) return true;
            if (int.TryParse(part, out var single) && single == port) return true;
        }
        return false;
    }

    private static T? TryGet<T>(object com, string property)
    {
        try { return Get<T>(com, property); }
        catch (Exception ex) when (ex is COMException or MissingMethodException or TargetInvocationException) { return default; }
    }

    private static T Get<T>(object com, string property, params object[] args)
    {
        var v = com.GetType().InvokeMember(property, BindingFlags.GetProperty, null, com, args);
        return v is null ? default! : (T)v;
    }

    /// <summary>
    /// The netsh arguments for the narrowest rule that works: inbound TCP on the share port, for Fyltrix.exe only,
    /// from the local subnet only, on Private and Domain networks (never Public).
    /// </summary>
    public static string AddRuleArguments(int port, string exePath) =>
        $"advfirewall firewall add rule name=\"{RuleName} (TCP {port})\" dir=in {AllowScope(port, exePath)}";

    public static string UpdateRuleArguments(int port, string exePath) =>
        $"advfirewall firewall set rule name=\"{RuleName} (TCP {port})\" dir=in new {AllowScope(port, exePath)}";

    private static string AllowScope(int port, string exePath) =>
        $"action=allow program=\"{exePath}\" protocol=TCP localport={port} remoteip=localsubnet profile=private,domain enable=yes";

    /// <summary>Read only, and only used when the user asks to authorize sharing. Null means inspection failed.</summary>
    public static bool? HasOwnedRule(int port)
    {
        object? policy = null, rules = null;
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type == null) return null;
            policy = Activator.CreateInstance(type)!;
            rules = Get<object>(policy, "Rules");
            if (rules is not System.Collections.IEnumerable all) return null;
            foreach (var rule in all)
            {
                try
                {
                    if (Get<string>(rule, "Name") == $"{RuleName} (TCP {port})" && Get<int>(rule, "Direction") == 1) return true;
                }
                finally { if (Marshal.IsComObject(rule)) Marshal.ReleaseComObject(rule); }
            }
            return false;
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or InvalidCastException or UnauthorizedAccessException or MissingMethodException)
        {
            Diagnostics.Log("Sharing firewall rule lookup", ex);
            return null;
        }
        finally
        {
            if (rules != null && Marshal.IsComObject(rules)) Marshal.ReleaseComObject(rules);
            if (policy != null && Marshal.IsComObject(policy)) Marshal.ReleaseComObject(policy);
        }
    }

    public static string DeleteRuleArguments(int port) => $"advfirewall firewall delete rule name=\"{RuleName} (TCP {port})\"";

    /// <summary>Runs netsh elevated; Windows shows the UAC prompt. Returns null on success or the reason it didn't happen.</summary>
    public static string? RunElevated(string netshArguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh.exe", netshArguments) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (p == null) return "netsh didn't start.";
            if (!p.WaitForExit(30_000)) return "netsh didn't finish.";
            return p.ExitCode == 0 ? null : $"netsh failed (exit code {p.ExitCode}).";
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return "Administrator approval was declined."; }
        catch (System.ComponentModel.Win32Exception ex) { return ex.Message; }
    }
}
