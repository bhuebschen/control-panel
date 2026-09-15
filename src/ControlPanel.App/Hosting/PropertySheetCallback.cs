using System.Runtime.InteropServices;
using ControlPanel.App.Interop;

namespace ControlPanel.App.Hosting;

/// <summary>
/// Passed to a snap-in's IExtendPropertySheet.CreatePropertyPages so it can
/// hand us the native HPROPSHEETPAGE handles it created for its property
/// pages, which we then feed into the Win32 PropertySheet() API.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[ComDefaultInterface(typeof(IPropertySheetCallback))]
internal sealed class PropertySheetCallback : IPropertySheetCallback
{
    public List<IntPtr> Pages { get; } = new();

    public void AddPage(IntPtr hPage) => Pages.Add(hPage);

    public void RemovePage(IntPtr hPage) => Pages.Remove(hPage);
}
