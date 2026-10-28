using PrintBridge.Printing;
using PrintBridge.UI;

namespace PrintBridge;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // 1. Process Command-Line Utilities (Firewall helpers, etc.)
        if (args.Length > 0)
        {
            if (args.Contains("--firewall", StringComparer.OrdinalIgnoreCase))
            {
                FirewallHelper.AddFirewallRules();
                return;
            }
            if (args.Contains("--clean-firewall", StringComparer.OrdinalIgnoreCase))
            {
                FirewallHelper.RemoveFirewallRules();
                return;
            }
        }

        // 2. Single Instance Protection
        const string mutexName = @"Global\PrintBridge_SingleInstance_Mutex";
        using var mutex = new Mutex(true, mutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Easy Print Easy Scan is already running in your system tray.\nLook for the printer icon in your taskbar notification area.",
                "Easy Print Easy Scan", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 3. Initialize Windows Forms
        ApplicationConfiguration.Initialize();

        // 4. Run System Tray Context
        Application.Run(new TrayApplicationContext());
    }
}
