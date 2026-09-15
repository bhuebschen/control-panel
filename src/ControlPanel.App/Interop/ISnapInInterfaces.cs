using System.Runtime.InteropServices;
// The WinForms SDK project style implicitly adds "using System.Windows.Forms;"
// to every file, and System.Windows.Forms.IDataObject (drag/drop, clipboard)
// collides with the COM IDataObject this file actually means - alias it
// explicitly rather than relying on the ComTypes using winning by chance.
using IDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace ControlPanel.App.Interop;

// -----------------------------------------------------------------------
// Interfaces IMPLEMENTED BY THE SNAP-IN and CALLED BY THE CONSOLE (i.e. by
// this application). These are the interop-facing declarations of the
// interfaces every MMC snap-in server exposes; the runtime creates RCWs
// for them automatically when a native COM object is cast to one of these
// types. GUIDs/order come from the Windows SDK mmc.idl.
// -----------------------------------------------------------------------

[ComImport]
[Guid("955AB28A-5218-11D0-A985-00C04FD8D565")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IComponentData
{
    void Initialize([MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    // ppComponent as object + explicit MarshalAs, for consistency with
    // every other interface-typed parameter in this file (receiving a
    // *native* interface pointer into an RCW, as this does, was not
    // itself implicated by testing - unlike passing our own CCW objects
    // as arguments/out-values elsewhere - but there's no reason to keep
    // the one inconsistent declaration once the others all changed).
    void CreateComponent([MarshalAs(UnmanagedType.Interface)] out object ppComponent);
    // lpDataObject is typed as a plain object (not the IDataObject interface
    // type) marshaled explicitly as an interface pointer, matching the
    // pattern already proven to work for Initialize's pUnknown above -
    // Notify is the first call in the whole session that passes a
    // COM-interface-typed argument (as opposed to a plain object) and,
    // with null, an interface-typed parameter here crashed inside coreclr's
    // own interop marshaling rather than in the snap-in's code.
    void Notify([MarshalAs(UnmanagedType.Interface)] object? lpDataObject, MMC_NOTIFY_TYPE @event, IntPtr arg, IntPtr param);
    void Destroy();
    void QueryDataObject(IntPtr cookie, DATA_OBJECT_TYPES type, out IDataObject ppDataObject);
    void GetDisplayInfo(ref SCOPEDATAITEM pScopeDataItem);
    [PreserveSig]
    int CompareObjects([MarshalAs(UnmanagedType.Interface)] object lpDataObjectA, [MarshalAs(UnmanagedType.Interface)] object lpDataObjectB);
}

[ComImport]
[Guid("43136EB2-D36C-11CF-ADBC-00AA00A80033")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IComponent
{
    // lpConsole is typed as a plain object (not the IConsole interface
    // type), for the exact same reason as IComponentData.Initialize's
    // pUnknown: empirically confirmed (via a headless diagnostic against
    // the real "Services" snap-in) that marshaling OUR OWN managed
    // CCW-backed object (MmcConsole) into a parameter statically typed as
    // one of our own [ComImport] interfaces throws InvalidCastException,
    // while the identical object marshaled through `object` +
    // MarshalAs(IUnknown) works. Our own raw QueryInterface tests proved
    // the CCW itself answers QueryInterface for IConsole correctly - the
    // failure is specifically in how the interop stub marshals a
    // statically-interface-typed *argument*, not in the object graph.
    void Initialize([MarshalAs(UnmanagedType.IUnknown)] object lpConsole);
    void Notify([MarshalAs(UnmanagedType.Interface)] object? lpDataObject, MMC_NOTIFY_TYPE @event, IntPtr arg, IntPtr param);
    void Destroy(IntPtr cookie);
    void QueryDataObject(IntPtr cookie, DATA_OBJECT_TYPES type, out IDataObject ppDataObject);
    // Keep the HRESULT so the host can log the exact result and distinguish
    // failures from nonzero success codes without relying on an exception.
    [PreserveSig]
    int GetResultViewType(IntPtr cookie, out IntPtr ppViewType, out int pViewOptions);
    void GetDisplayInfo(ref RESULTDATAITEM pResultDataItem);
    [PreserveSig]
    int CompareObjects([MarshalAs(UnmanagedType.Interface)] object lpDataObjectA, [MarshalAs(UnmanagedType.Interface)] object lpDataObjectB);
}

[ComImport]
[Guid("85DE64DC-EF21-11cf-A285-00C04FD8DBE6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IExtendPropertySheet
{
    // lpProvider: same fix as IComponent.Initialize above - it's always our
    // own PropertySheetCallback CCW object, never an RCW, so it must be
    // marshaled via object + MarshalAs rather than as a typed interface
    // parameter.
    void CreatePropertyPages([MarshalAs(UnmanagedType.Interface)] object lpProvider, IntPtr handle, [MarshalAs(UnmanagedType.Interface)] object lpIDataObject);
    [PreserveSig]
    int QueryPagesFor([MarshalAs(UnmanagedType.Interface)] object lpDataObject);
}

[ComImport]
[Guid("4F3B7A4F-CFAC-11CF-B8E3-00C04FD8D5B0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IExtendContextMenu
{
    // piCallback: same reasoning as IComponent.Initialize/CreatePropertyPages -
    // always our own CCW object, never an RCW.
    void AddMenuItems([MarshalAs(UnmanagedType.Interface)] object piDataObject, [MarshalAs(UnmanagedType.Interface)] object piCallback, ref int pInsertionAllowed);
    void Command(int lCommandID, [MarshalAs(UnmanagedType.Interface)] object piDataObject);
}

[ComImport]
[Guid("1245208C-A151-11D0-A7D7-00C04FD909DD")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISnapinAbout
{
    void GetSnapinDescription([MarshalAs(UnmanagedType.LPWStr)] out string lpDescription);
    void GetProvider([MarshalAs(UnmanagedType.LPWStr)] out string lpName);
    void GetSnapinVersion([MarshalAs(UnmanagedType.LPWStr)] out string lpVersion);
    void GetSnapinImage(out IntPtr hAppIcon);
    void GetStaticFolderImage(out IntPtr hSmallImage, out IntPtr hSmallImageOpen, out IntPtr hLargeImage, out int cMask);
}
