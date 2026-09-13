using System.Windows.Forms;
using ControlPanel.App.Forms;
using ControlPanel.App.Native;

namespace ControlPanel.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Win32.EnsureCommonControlsInitialized();

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
