using System.Diagnostics;

namespace PrintBridge.Printing;

/// <summary>
/// Manages Windows Firewall rules for IPP (TCP 631) and mDNS (UDP 5353).
/// </summary>
public static class FirewallHelper
{
    public const string RuleIpp = "PrintBridge IPP Server (TCP 631)";
    public const string RuleMdns = "PrintBridge mDNS Discovery (UDP 5353)";

    /// <summary>
    /// Configures Windows Firewall rules by running netsh advfirewall with UAC prompt.
    /// </summary>
    public static bool AddFirewallRules(int ippPort = 631)
    {
        try
        {
            var exePath = Environment.ProcessPath ?? "";
            var appRule = string.IsNullOrWhiteSpace(exePath) ? "" : $"\r\nadvfirewall firewall add rule name=\"PrintBridge Application\" dir=in action=allow program=\"{exePath}\" enable=yes profile=any";

            var commands = $"""
                advfirewall firewall delete rule name="{RuleIpp}"
                advfirewall firewall delete rule name="{RuleMdns}"
                advfirewall firewall delete rule name="PrintBridge Application"
                advfirewall firewall add rule name="{RuleIpp}" dir=in action=allow protocol=TCP localport={ippPort} profile=any description="Allows iPhone and Android devices to send print jobs via IPP"
                advfirewall firewall add rule name="{RuleMdns}" dir=in action=allow protocol=UDP localport=5353 profile=any description="Allows AirPrint and IPP Everywhere service discovery"{appRule}
                """;

            var tempScript = Path.Combine(Path.GetTempPath(), $"pb_fw_{Guid.NewGuid():N}.bat");
            File.WriteAllText(tempScript, "@echo off\r\n" + commands + "\r\ndel \"%~f0\"\r\n");

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{tempScript}\"",
                Verb = "runas", // Triggers standard Windows UAC prompt
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            var proc = Process.Start(psi);
            proc?.WaitForExit(10_000);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to configure firewall: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Removes firewall rules on uninstall.
    /// </summary>
    public static void RemoveFirewallRules()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall delete rule name=\"PrintBridge*\"",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch { }
    }
}
