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
    void CreateComponent(out IComponent ppComponent);
    void Notify(IDataObject? lpDataObject, MMC_NOTIFY_TYPE @event, IntPtr arg, IntPtr param);
    void Destroy();
    void QueryDataObject(IntPtr cookie, DATA_OBJECT_TYPES type, out IDataObject ppDataObject);
    void GetDisplayInfo(ref SCOPEDATAITEM pScopeDataItem);
    [PreserveSig]
    int CompareObjects(IDataObject lpDataObjectA, IDataObject lpDataObjectB);
}

[ComImport]
[Guid("43136EB2-D36C-11CF-ADBC-00AA00A80033")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IComponent
{
    void Initialize(IConsole lpConsole);
    void Notify(IDataObject? lpDataObject, MMC_NOTIFY_TYPE @event, IntPtr arg, IntPtr param);
    void Destroy(IntPtr cookie);
    void QueryDataObject(IntPtr cookie, DATA_OBJECT_TYPES type, out IDataObject ppDataObject);
    void GetResultViewType(IntPtr cookie, out IntPtr ppViewType, out int pViewOptions);
    void GetDisplayInfo(ref RESULTDATAITEM pResultDataItem);
    [PreserveSig]
    int CompareObjects(IDataObject lpDataObjectA, IDataObject lpDataObjectB);
}

[ComImport]
[Guid("85DE64DC-EF21-11cf-A285-00C04FD8DBE6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IExtendPropertySheet
{
    void CreatePropertyPages(IPropertySheetCallback lpProvider, IntPtr handle, IDataObject lpIDataObject);
    [PreserveSig]
    int QueryPagesFor(IDataObject lpDataObject);
}

[ComImport]
[Guid("4F3B7A4F-CFAC-11CF-B8E3-00C04FD8D5B0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IExtendContextMenu
{
    void AddMenuItems(IDataObject piDataObject, IContextMenuCallback piCallback, ref int pInsertionAllowed);
    void Command(int lCommandID, IDataObject piDataObject);
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
