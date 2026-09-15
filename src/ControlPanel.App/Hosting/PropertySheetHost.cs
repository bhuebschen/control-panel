using System.Runtime.InteropServices;
// See ISnapInInterfaces.cs: System.Windows.Forms.IDataObject (used for
// drag/drop and clipboard, and in scope here via the using below) collides
// with the COM IDataObject this file actually means.
using IDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using System.Windows.Forms;
using ControlPanel.App.Interop;
using ControlPanel.App.Native;

namespace ControlPanel.App.Hosting;

/// <summary>
/// Drives the property-page half of the MMC snap-in protocol: asks the
/// snap-in (via IExtendPropertySheet, which most snap-ins implement on
/// their IComponentData or IComponent object) whether a node has property
/// pages, collects the native HPROPSHEETPAGE handles it creates, and shows
/// them with the standard Win32 PropertySheet() modal dialog.
/// </summary>
internal static class PropertySheetHost
{
    private static long _nextNotifyHandle = 1;

    public static void ShowFor(Form owner, string caption, IDataObject? dataObject, params object?[] candidateSnapInObjects)
    {
        if (dataObject is null)
        {
            MessageBox.Show(owner, "This item did not provide a data object to query for properties.", caption);
            return;
        }

        IExtendPropertySheet? extend = null;
        foreach (var candidate in candidateSnapInObjects)
        {
            if (candidate is IExtendPropertySheet ep)
            {
                try
                {
                    if (ep.QueryPagesFor(dataObject) == 0) // S_OK
                    {
                        extend = ep;
                        break;
                    }
                }
                catch (COMException)
                {
                    // This object doesn't support properties for this item; try the next candidate.
                }
            }
        }

        if (extend is null)
        {
            MessageBox.Show(owner, "This item does not provide a Properties page.", caption);
            return;
        }

        var callback = new PropertySheetCallback();
        var handle = new IntPtr(_nextNotifyHandle++);
        extend.CreatePropertyPages(callback, handle, dataObject);

        if (callback.Pages.Count == 0)
        {
            MessageBox.Show(owner, "This item does not provide a Properties page.", caption);
            return;
        }

        ShowPropertySheet(owner, caption, callback.Pages);
    }

    private static void ShowPropertySheet(Form owner, string caption, List<IntPtr> pages)
    {
        Win32.EnsureCommonControlsInitialized();

        IntPtr pageArray = Marshal.AllocHGlobal(IntPtr.Size * pages.Count);
        try
        {
            for (int i = 0; i < pages.Count; i++)
            {
                Marshal.WriteIntPtr(pageArray, i * IntPtr.Size, pages[i]);
            }

            var header = new PROPSHEETHEADER
            {
                dwSize = (uint)Marshal.SizeOf<PROPSHEETHEADER>(),
                dwFlags = Win32.PSH_PROPTITLE,
                hwndParent = owner.Handle,
                hInstance = Win32.GetModuleHandle(null),
                pszCaption = caption,
                nPages = (uint)pages.Count,
                ppspOrPhpage = pageArray,
            };

            Win32.PropertySheet(ref header);
            // PropertySheet() takes ownership of and destroys the HPROPSHEETPAGE
            // handles itself once the dialog closes - we must not touch them again.
        }
        finally
        {
            Marshal.FreeHGlobal(pageArray);
        }
    }
}
