using System.Windows.Forms;
using ControlPanel.App.Forms;
using ControlPanel.App.Native;

namespace ControlPanel.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Win32.EnsureCommonControlsInitialized();

        if (args.Length >= 1 && args[0] == "--diag-load-snapin")
        {
            if (args.Length < 2)
            {
                throw new ArgumentException("Usage: --diag-load-snapin <name-or-clsid> [output-file]");
            }

            SnapInDiagnostics.RunLoadDiagnostic(args[1], args.Length > 2 ? args[2] : null);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
