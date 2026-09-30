using System;
using System.Linq;
using System.Windows.Forms;

namespace KioskPaymentPoc;

internal static class Program
{
    /// <summary>
    ///   (none)       run the interactive kiosk controller (real webhook endpoint on localhost)
    ///   --gif=&lt;dir&gt;  render a scripted demo (pay → confirm → session → duplicate-ignored) to frames, then exit
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        string? gifDir = args
            .FirstOrDefault(a => a.StartsWith("--gif=", StringComparison.OrdinalIgnoreCase))
            ?.Substring("--gif=".Length);
        bool demo = gifDir != null || args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));

        Application.Run(new MainForm(demo, gifDir));
    }
}
