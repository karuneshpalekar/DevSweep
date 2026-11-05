using System.Globalization;
using DevSweep.Core.Detectors;
using Microsoft.Win32;

namespace DevSweep.Core.Health.Ports;

/// <summary>Windows Firewall profiles.</summary>
public enum FirewallProfile
{
    Domain,
    Private,
    Public,
}

/// <summary>Whether other devices could connect on one network profile, and why.</summary>
public sealed record ProfileVerdict(FirewallProfile Profile, bool? Allowed, string Reason);

/// <summary>Firewall reachability of one listener on each profile; <see cref="Known"/> is false when the rules could not be read.</summary>
public sealed record FirewallVerdict(bool Known, IReadOnlyList<ProfileVerdict> Profiles, string Summary)
{
    public static FirewallVerdict Unknown(string why) => new(false, [], $"Firewall unknown: {why}");
}

/// <summary>One inbound firewall rule, as stored in the registry.</summary>
public sealed record FirewallRule
{
    public required string Name { get; init; }

    public bool Allow { get; init; }

    public bool Active { get; init; }

    public bool Inbound { get; init; }

    /// <summary>6 TCP, 17 UDP; null means any protocol.</summary>
    public int? Protocol { get; init; }

    /// <summary>Profiles it applies to; empty means all.</summary>
    public IReadOnlyList<FirewallProfile> Profiles { get; init; } = [];

    /// <summary>Local ports or ranges ("3000", "8000-8100"); empty means any.</summary>
    public IReadOnlyList<string> LocalPorts { get; init; } = [];

    /// <summary>Program path (environment variables expanded); null means any program.</summary>
    public string? App { get; init; }

    /// <summary>Applies only to a service.</summary>
    public string? Service { get; init; }

    /// <summary>Restricted to some remote addresses.</summary>
    public bool RemoteRestricted { get; init; }

    /// <summary>
    /// Parses the registry form (<c>v2.31|Action=Allow|Active=TRUE|Dir=In|Protocol=6|Profile=Private|LPort=3000|App=...|Name=...|</c>).
    /// Null when it is not a rule DevSweep understands.
    /// </summary>
    public static FirewallRule? Parse(string text, Func<string, string>? expand = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] parts = text.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].StartsWith("v2.", StringComparison.Ordinal))
        {
            return null;
        }

        var profiles = new List<FirewallProfile>();
        var ports = new List<string>();
        string? action = null, active = null, dir = null, app = null, svc = null, name = null;
        int? protocol = null;
        bool remote = false;
        foreach (string part in parts.Skip(1))
        {
            int eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            string key = part[..eq];
            string value = part[(eq + 1)..];
            switch (key)
            {
                case "Action": action = value; break;
                case "Active": active = value; break;
                case "Dir": dir = value; break;
                case "Protocol": protocol = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) ? p : null; break;
                case "Profile" when Enum.TryParse(value, ignoreCase: true, out FirewallProfile profile): profiles.Add(profile); break;
                case "LPort": ports.Add(value); break;
                case "LPort2_10": ports.Add(value); break;
                case "App": app = expand is null ? value : expand(value); break;
                case "Svc": svc = value; break;
                case "Name": name = value; break;
                case "RA4" or "RA6" or "RA42" or "RA62" or "RMauth" or "RUAuth" when !value.Equals("*", StringComparison.Ordinal): remote = true; break;
                default: break;
            }
        }

        return new FirewallRule
        {
            Name = name ?? "(unnamed rule)",
            Allow = string.Equals(action, "Allow", StringComparison.OrdinalIgnoreCase),
            Active = string.Equals(active, "TRUE", StringComparison.OrdinalIgnoreCase),
            Inbound = string.Equals(dir, "In", StringComparison.OrdinalIgnoreCase),
            Protocol = protocol,
            Profiles = profiles,
            LocalPorts = ports,
            App = app,
            Service = svc,
            RemoteRestricted = remote,
        };
    }

    /// <summary>The rule applies to this program, port and protocol on this profile.</summary>
    public bool Matches(FirewallProfile profile, string? program, int port, PortProtocol protocol)
    {
        if (!Active || !Inbound || Service is not null)
        {
            return false;
        }

        if (Profiles.Count > 0 && !Profiles.Contains(profile))
        {
            return false;
        }

        if (Protocol is { } p && p != (protocol == PortProtocol.Tcp ? 6 : 17))
        {
            return false;
        }

        if (App is { } app && !string.Equals(app, "System", StringComparison.OrdinalIgnoreCase)
            && (program is null || !string.Equals(System.IO.Path.GetFullPath(app), System.IO.Path.GetFullPath(program), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return LocalPorts.Count == 0 || LocalPorts.Any(spec => PortMatches(spec, port));
    }

    private static bool PortMatches(string spec, int port)
    {
        foreach (string piece in spec.Split(','))
        {
            string[] range = piece.Split('-');
            if (range.Length == 1 && int.TryParse(range[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int single) && single == port)
            {
                return true;
            }

            if (range.Length == 2 && int.TryParse(range[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int low)
                && int.TryParse(range[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int high) && port >= low && port <= high)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Windows Firewall reachability, judged from the firewall's own configuration in the registry
/// (local rules in <c>SharedAccess\Parameters\FirewallPolicy\FirewallRules</c>, Group Policy rules in
/// <c>SOFTWARE\Policies\Microsoft\WindowsFirewall\FirewallRules</c>, and each profile's on/off and
/// default inbound action). Block rules win over allow rules, as in the firewall. Rules restricted
/// to services or remote addresses are reported as such rather than guessed. When the rules cannot be
/// read, the verdict is "unknown". (The COM API, <c>INetFwPolicy2</c>, is IDispatch-based and not
/// usable from a trimmed Native AOT engine; <c>netsh</c> output is localised.)
/// </summary>
public sealed class Firewall
{
    public const string PolicyKey = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";
    public const string GroupPolicyKey = @"SOFTWARE\Policies\Microsoft\WindowsFirewall";

    private readonly IReadOnlyList<FirewallRule> _rules;
    private readonly Dictionary<FirewallProfile, (bool Enabled, bool BlockByDefault)> _profiles;

    private Firewall(IReadOnlyList<FirewallRule> rules, Dictionary<FirewallProfile, (bool, bool)> profiles, string? problem)
    {
        _rules = rules;
        _profiles = profiles;
        Problem = problem;
    }

    /// <summary>Why the configuration could not be read; null when it was.</summary>
    public string? Problem { get; }

    public int RuleCount => _rules.Count;

    public static Firewall Load(IFirewallConfig config, Func<string, string>? expand = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var rules = new List<FirewallRule>();
        var profiles = new Dictionary<FirewallProfile, (bool, bool)>();
        try
        {
            IReadOnlyList<string>? texts = config.RuleTexts();
            if (texts is null)
            {
                return new Firewall([], [], "the firewall's rule store is missing");
            }

            rules.AddRange(texts.Select(t => FirewallRule.Parse(t, expand)).OfType<FirewallRule>());
            foreach ((FirewallProfile profile, string local, string policy) in (ReadOnlySpan<(FirewallProfile, string, string)>)[
                         (FirewallProfile.Domain, "DomainProfile", "DomainProfile"), (FirewallProfile.Private, "StandardProfile", "PrivateProfile"), (FirewallProfile.Public, "PublicProfile", "PublicProfile")])
            {
                bool enabled = (config.ProfileValue(policy, local, "EnableFirewall") ?? 1) != 0;
                bool block = (config.ProfileValue(policy, local, "DefaultInboundAction") ?? 1) != 0;
                profiles[profile] = (enabled, block);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return new Firewall([], [], $"the firewall configuration could not be read ({ex.Message})");
        }

        return new Firewall(rules, profiles, null);
    }

    /// <summary>Whether other devices could reach <paramref name="program"/> on this port, per profile.</summary>
    public FirewallVerdict Judge(string? program, int port, PortProtocol protocol)
    {
        if (Problem is not null)
        {
            return FirewallVerdict.Unknown(Problem);
        }

        var verdicts = new List<ProfileVerdict>();
        foreach (FirewallProfile profile in Enum.GetValues<FirewallProfile>())
        {
            (bool enabled, bool blockByDefault) = _profiles.GetValueOrDefault(profile, (true, true));
            if (!enabled)
            {
                verdicts.Add(new(profile, true, "the firewall is off for this profile"));
                continue;
            }

            List<FirewallRule> matching = _rules.Where(r => r.Matches(profile, program, port, protocol)).ToList();
            if (matching.FirstOrDefault(r => !r.Allow) is { } block)
            {
                verdicts.Add(new(profile, false, $"blocked by \"{block.Name}\""));
            }
            else if (matching.FirstOrDefault(r => r.Allow && !r.RemoteRestricted) is { } allow)
            {
                verdicts.Add(new(profile, true, $"allowed by \"{allow.Name}\""));
            }
            else if (matching.FirstOrDefault(r => r.Allow) is { } restricted)
            {
                verdicts.Add(new(profile, true, $"allowed from some addresses by \"{restricted.Name}\""));
            }
            else
            {
                verdicts.Add(new(profile, !blockByDefault, blockByDefault ? "no rule allows it (inbound is blocked by default)" : "inbound is allowed by default"));
            }
        }

        List<string> open = verdicts.Where(v => v.Allowed == true).Select(v => v.Profile.ToString()).ToList();
        string summary = open.Count == 0 ? "Blocked by Windows Firewall on every network type"
            : open.Count == 3 ? "Allowed through Windows Firewall on every network type"
            : $"Allowed on {Words.Join(open)} networks; blocked on {Words.Join(verdicts.Where(v => v.Allowed != true).Select(v => v.Profile.ToString()).ToList())}";
        return new FirewallVerdict(true, verdicts, summary);
    }
}

/// <summary>Where the firewall's configuration is read from. Faked in tests.</summary>
public interface IFirewallConfig
{
    /// <summary>Every rule's registry text (local, then Group Policy); null when the local rule store is missing.</summary>
    IReadOnlyList<string>? RuleTexts();

    /// <summary>A profile setting: Group Policy first (<paramref name="policyName"/>), then the local one (<paramref name="localName"/>).</summary>
    int? ProfileValue(string policyName, string localName, string value);
}

/// <summary>Firewall configuration held in memory (tests). Profile values are keyed "ProfileName\Value".</summary>
public sealed class FixedFirewallConfig : IFirewallConfig
{
    public List<string>? Rules { get; init; } = [];

    public Dictionary<string, int> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string>? RuleTexts() => Rules;

    public int? ProfileValue(string policyName, string localName, string value)
        => Values.TryGetValue(policyName + "\\" + value, out int gp) ? gp : Values.TryGetValue(localName + "\\" + value, out int own) ? own : null;
}

/// <summary>The machine's firewall configuration in the registry (read only).</summary>
public sealed class WindowsFirewallConfig : IFirewallConfig
{
    public static WindowsFirewallConfig Instance { get; } = new();

    public IReadOnlyList<string>? RuleTexts()
    {
        using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? local = machine.OpenSubKey(Firewall.PolicyKey + @"\FirewallRules");
        if (local is null)
        {
            return null;
        }

        var texts = new List<string>();
        texts.AddRange(local.GetValueNames().Select(n => local.GetValue(n) as string).OfType<string>());
        using RegistryKey? policy = machine.OpenSubKey(Firewall.GroupPolicyKey + @"\FirewallRules");
        if (policy is not null)
        {
            texts.AddRange(policy.GetValueNames().Select(n => policy.GetValue(n) as string).OfType<string>());
        }

        return texts;
    }

    public int? ProfileValue(string policyName, string localName, string value)
    {
        using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey? gp = machine.OpenSubKey(Firewall.GroupPolicyKey + "\\" + policyName);
        if (gp?.GetValue(value) is int fromPolicy)
        {
            return fromPolicy;
        }

        using RegistryKey? own = machine.OpenSubKey(Firewall.PolicyKey + "\\" + localName);
        return own?.GetValue(value) is int fromLocal ? fromLocal : null;
    }
}
