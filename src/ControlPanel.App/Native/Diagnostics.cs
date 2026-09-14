namespace ControlPanel.App.Native;

/// <summary>
/// Minimal diagnostic logging. This host cannot be debugged remotely, so
/// anything a snap-in does that we deliberately don't support (custom
/// views, new windows, ...) is written to the debug output stream - visible
/// in Visual Studio's Output window or DebugView - instead of vanishing
/// silently behind an HRESULT the snap-in may or may not surface to the user.
/// </summary>
internal static class Diagnostics
{
    public static void Log(string message) => System.Diagnostics.Debug.WriteLine($"[MmcHost] {message}");
}
