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
    public static void ShowFor(Form owner, string caption, IDataObject? dataObject, params object?[] candidateSnapInObjects)
    {
        if (dataObject is null)
        {
            MessageBox.Show(owner, "This item did not provide a data object to query for properties.", caption);
            return;
        }

        if (!TryFindPages(dataObject, candidateSnapInObjects, out var pages))
        {
            MessageBox.Show(owner, "This item does not provide a Properties page.", caption);
            return;
        }

        ShowPropertySheet(owner, caption, pages);
    }

    /// <summary>
    /// Finds and creates the HPROPSHEETPAGE handles a snap-in provides for
    /// this item, without showing any UI - the headless-testable half of
    /// ShowFor, also used by the --diag-load-snapin properties check.
    /// </summary>
    public static bool TryFindPages(IDataObject dataObject, object?[] candidateSnapInObjects, out List<IntPtr> pages)
    {
        IExtendPropertySheet? extend = null;
        foreach (var candidate in candidateSnapInObjects)
        {
            SnapInDiagnostics.Trace($"PropertySheetHost candidate: {candidate?.GetType().FullName ?? "null"}, is IExtendPropertySheet={candidate is IExtendPropertySheet}");
            if (candidate is IExtendPropertySheet ep)
            {
                try
                {
                    int hr = ep.QueryPagesFor(dataObject);
                    SnapInDiagnostics.Trace($"  QueryPagesFor = 0x{hr:X8}");
                    if (hr == 0) // S_OK
                    {
                        extend = ep;
                        break;
                    }
                }
                catch (COMException ex)
                {
                    // This object doesn't support properties for this item; try the next candidate.
                    SnapInDiagnostics.Trace($"  QueryPagesFor threw {ex.GetType().Name}: {ex.Message} (HResult=0x{ex.HResult:X8})");
                }
            }
        }

        if (extend is null)
        {
            pages = new List<IntPtr>();
            return false;
        }

        var callback = new PropertySheetCallback();
        // Must be a real GlobalAlloc handle, not just any unique value - see
        // Win32.GlobalAlloc's doc comment. Ownership passes to the snap-in
        // once CreatePropertyPages is called (its own page-cleanup code
        // frees it via GlobalFree when the page is destroyed) - this host
        // must not free it itself.
        IntPtr handle = Win32.GlobalAlloc(Win32.GMEM_FIXED, (nuint)IntPtr.Size);
        RawCreatePropertyPages(extend, callback, handle, dataObject);
        SnapInDiagnostics.Trace($"  CreatePropertyPages -> {callback.Pages.Count} page(s)");

        pages = callback.Pages;
        return pages.Count > 0;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreatePropertyPagesNative(IntPtr @this, IntPtr lpProvider, IntPtr handle, IntPtr lpIDataObject);

    /// <summary>
    /// Raw-vtable IExtendPropertySheet::CreatePropertyPages call. The
    /// declarative [ComImport] binding (lpProvider typed as
    /// object+MarshalAs(Interface)) does not throw for "Services" - unlike
    /// every other CCW-argument bug this project has hit, which surfaced as
    /// exceptions - but silently never reaches our PropertySheetCallback:
    /// QueryPagesFor reports S_OK, yet zero AddPage calls ever arrive.
    /// Bypassing the stub and marshaling lpProvider as the exact interface
    /// type the IDL declares (LPPROPERTYSHEETCALLBACK, not IUnknown*) -
    /// same lesson as IComponent::Initialize's LPCONSOLE parameter - fixed it.
    /// </summary>
    private static void RawCreatePropertyPages(IExtendPropertySheet extend, PropertySheetCallback callback, IntPtr handle, IDataObject dataObject)
    {
        var iid = typeof(IExtendPropertySheet).GUID;
        IntPtr providerPtr = Marshal.GetComInterfaceForObject(callback, typeof(IPropertySheetCallback));
        try
        {
            IntPtr dataObjPtr = Marshal.GetComInterfaceForObject(dataObject, typeof(IDataObject));
            try
            {
                IntPtr extendUnk = Marshal.GetIUnknownForObject(extend);
                try
                {
                    int hr = Marshal.QueryInterface(extendUnk, ref iid, out IntPtr extendItf);
                    Marshal.ThrowExceptionForHR(hr, new IntPtr(-1));
                    try
                    {
                        NativePointerGuard.EnsureReadable(extendItf, $"{iid:B} interface pointer");
                        IntPtr vtable = Marshal.ReadIntPtr(extendItf, 0);
                        NativePointerGuard.EnsureReadable(vtable, $"{iid:B} vtable");
                        // Slot 3: QueryInterface/AddRef/Release, then
                        // CreatePropertyPages as the first method
                        // IExtendPropertySheet declares.
                        IntPtr slotPtr = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
                        NativePointerGuard.EnsureExecutable(slotPtr, $"{iid:B} vtable slot 3 (CreatePropertyPages)");
                        var createPages = Marshal.GetDelegateForFunctionPointer<CreatePropertyPagesNative>(slotPtr);
                        int result = createPages(extendItf, providerPtr, handle, dataObjPtr);
                        if (result < 0)
                        {
                            throw new COMException(
                                $"Raw IExtendPropertySheet::CreatePropertyPages returned HRESULT 0x{result:X8}.", result);
                        }
                    }
                    finally
                    {
                        Marshal.Release(extendItf);
                    }
                }
                finally
                {
                    Marshal.Release(extendUnk);
                }
            }
            finally
            {
                Marshal.Release(dataObjPtr);
            }
        }
        finally
        {
            Marshal.Release(providerPtr);
        }
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
