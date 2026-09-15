using System.Runtime.InteropServices;

namespace ControlPanel.App.Interop;

// -----------------------------------------------------------------------
// Interfaces IMPLEMENTED BY THE CONSOLE (i.e. by this application, taking
// the role mmc.exe's "Node Manager" normally plays) and CALLED BY the
// snap-in. GUIDs and method order come verbatim from the Windows SDK
// mmc.idl - vtable order matters for COM marshaling, so derived interfaces
// re-declare their base interface's methods first, exactly as COM requires.
// -----------------------------------------------------------------------

[ComImport]
[Guid("43136EB1-D36C-11CF-ADBC-00AA00A80033")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IConsole
{
    // pHeader as a raw IntPtr, not a typed IHeaderCtrl RCW: found live, via
    // a Visual Studio mixed-mode debugging session (stepping through
    // real "Services"/"Component Services" calls) - control returns
    // directly to RunWithSession's `finally` block immediately after
    // SetHeader is entered, meaning the exception happens marshaling
    // *this* call, not in the snap-in's subsequent native logic as
    // previously assumed. This is the mirror case of every other typed-
    // custom-interface bug found this session: there it was our own CCW
    // object marshaled *out* through a typed parameter; here it's a
    // *native* interface pointer marshaled *in* to a strongly-typed RCW
    // parameter on a method our own CCW implements. We never use pHeader
    // (custom OCX/web views only), so there's no downside to leaving it
    // unmarshaled.
    void SetHeader(IntPtr pHeader);
    void SetToolbar(IntPtr pToolbar);
    void QueryResultView([MarshalAs(UnmanagedType.IUnknown)] out object pUnknown);
    // ppImageList/ppConsoleVerb: out parameters returning OUR OWN CCW
    // object (ImageListAdapter / this MmcConsole) to the native caller.
    // object + MarshalAs(Interface) was tried first (matching the fix that
    // worked for *input* parameters carrying our own CCW objects) but
    // still threw from within the interop marshaler - InvalidOperationException
    // for one real snap-in, NullReferenceException for another - always
    // right after this method's body already ran to completion, meaning
    // the failure is in marshaling the *return*, not in our code. Declaring
    // this as a raw IntPtr instead and manually calling
    // Marshal.GetComInterfaceForObject ourselves sidesteps the marshaler
    // for this direction entirely (IntPtr is blittable, no conversion
    // logic runs) - the same "drop to the primitive and do it by hand"
    // fix that already resolved the ImageListSetIcon/Strip and
    // IComponent.Initialize bugs.
    void QueryScopeImageList(out IntPtr ppImageList);
    void QueryResultImageList(out IntPtr ppImageList);
    void UpdateAllViews([MarshalAs(UnmanagedType.Interface)] object? lpDataObject, IntPtr data, IntPtr hint);
    void MessageBox(
        [MarshalAs(UnmanagedType.LPWStr)] string lpszText,
        [MarshalAs(UnmanagedType.LPWStr)] string lpszTitle,
        uint fuStyle,
        out int piRetval);
    void QueryConsoleVerb(out IntPtr ppConsoleVerb);
    void SelectScopeItem(IntPtr hScopeItem);
    void GetMainWindow(out IntPtr phwnd);
    void NewWindow(IntPtr hScopeItem, uint lOptions);
}

[ComImport]
[Guid("103D842A-AA63-11D1-A7E1-00C04FD8D565")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IConsole2 : IConsole
{
    // -- IConsole (must be repeated for correct vtable layout) --
    new void SetHeader(IntPtr pHeader);
    new void SetToolbar(IntPtr pToolbar);
    new void QueryResultView([MarshalAs(UnmanagedType.IUnknown)] out object pUnknown);
    new void QueryScopeImageList(out IntPtr ppImageList);
    new void QueryResultImageList(out IntPtr ppImageList);
    new void UpdateAllViews([MarshalAs(UnmanagedType.Interface)] object? lpDataObject, IntPtr data, IntPtr hint);
    new void MessageBox(
        [MarshalAs(UnmanagedType.LPWStr)] string lpszText,
        [MarshalAs(UnmanagedType.LPWStr)] string lpszTitle,
        uint fuStyle,
        out int piRetval);
    new void QueryConsoleVerb(out IntPtr ppConsoleVerb);
    new void SelectScopeItem(IntPtr hScopeItem);
    new void GetMainWindow(out IntPtr phwnd);
    new void NewWindow(IntPtr hScopeItem, uint lOptions);

    // -- IConsole2 additions --
    void Expand(IntPtr hItem, [MarshalAs(UnmanagedType.Bool)] bool bExpand);
    [PreserveSig]
    int IsTaskpadViewPreferred();
    void SetStatusText([MarshalAs(UnmanagedType.LPWStr)] string pszStatusText);
}

[ComImport]
[Guid("BEDEB620-F24D-11cf-8AFC-00AA003CA9F6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IConsoleNameSpace
{
    void InsertItem(ref SCOPEDATAITEM item);
    void DeleteItem(IntPtr hItem, int fDeleteThis);
    void SetItem(ref SCOPEDATAITEM item);
    void GetItem(ref SCOPEDATAITEM item);
    void GetChildItem(IntPtr item, out IntPtr pItemChild, out IntPtr pCookie);
    void GetNextItem(IntPtr item, out IntPtr pItemNext, out IntPtr pCookie);
    void GetParentItem(IntPtr item, out IntPtr pItemParent, out IntPtr pCookie);
}

[ComImport]
[Guid("255F18CC-65DB-11D1-A7DC-00C04FD8D565")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IConsoleNameSpace2 : IConsoleNameSpace
{
    new void InsertItem(ref SCOPEDATAITEM item);
    new void DeleteItem(IntPtr hItem, int fDeleteThis);
    new void SetItem(ref SCOPEDATAITEM item);
    new void GetItem(ref SCOPEDATAITEM item);
    new void GetChildItem(IntPtr item, out IntPtr pItemChild, out IntPtr pCookie);
    new void GetNextItem(IntPtr item, out IntPtr pItemNext, out IntPtr pCookie);
    new void GetParentItem(IntPtr item, out IntPtr pItemParent, out IntPtr pCookie);

    void Expand(IntPtr hItem);
    void AddExtension(IntPtr hItem, ref Guid lpClsid);
}

[ComImport]
[Guid("43136EB3-D36C-11CF-ADBC-00AA00A80033")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IHeaderCtrl
{
    void InsertColumn(int nCol, [MarshalAs(UnmanagedType.LPWStr)] string title, int nFormat, int nWidth);
    void DeleteColumn(int nCol);
    void SetColumnText(int nCol, [MarshalAs(UnmanagedType.LPWStr)] string title);
    void GetColumnText(int nCol, [MarshalAs(UnmanagedType.LPWStr)] out string text);
    void SetColumnWidth(int nCol, int nWidth);
    void GetColumnWidth(int nCol, out int pWidth);
}

[ComImport]
[Guid("9757abb8-1b32-11d1-a7ce-00c04fd8d565")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IHeaderCtrl2 : IHeaderCtrl
{
    new void InsertColumn(int nCol, [MarshalAs(UnmanagedType.LPWStr)] string title, int nFormat, int nWidth);
    new void DeleteColumn(int nCol);
    new void SetColumnText(int nCol, [MarshalAs(UnmanagedType.LPWStr)] string title);
    new void GetColumnText(int nCol, [MarshalAs(UnmanagedType.LPWStr)] out string text);
    new void SetColumnWidth(int nCol, int nWidth);
    new void GetColumnWidth(int nCol, out int pWidth);

    void SetChangeTimeOut(uint uTimeout);
    void SetColumnFilter(uint nColumn, uint dwType, IntPtr pFilterData);
    void GetColumnFilter(uint nColumn, ref uint pdwType, IntPtr pFilterData);
}

[ComImport]
[Guid("31DA5FA0-E0EB-11cf-9F21-00AA003CA9F6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IResultData
{
    void InsertItem(ref RESULTDATAITEM item);
    void DeleteItem(IntPtr itemID, int nCol);
    void FindItemByLParam(IntPtr lParam, out IntPtr pItemID);
    void DeleteAllRsltItems();
    void SetItem(ref RESULTDATAITEM item);
    void GetItem(ref RESULTDATAITEM item);
    void GetNextItem(ref RESULTDATAITEM item);
    void ModifyItemState(int nIndex, IntPtr itemID, uint uAdd, uint uRemove);
    void ModifyViewStyle(MMC_RESULT_VIEW_STYLE add, MMC_RESULT_VIEW_STYLE remove);
    void SetViewMode(int lViewMode);
    void GetViewMode(out int lViewMode);
    void UpdateItem(IntPtr itemID);
    void Sort(int nColumn, uint dwSortOptions, IntPtr lUserParam);
    void SetDescBarText([MarshalAs(UnmanagedType.LPWStr)] string descText);
    void SetItemCount(int nItemCount, uint dwOptions);
}

[ComImport]
[Guid("cc593830-b926-11d1-8063-0000f875a9ce")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDisplayHelp
{
    void ShowTopic([MarshalAs(UnmanagedType.LPWStr)] string pszHelpTopic);
}

[ComImport]
[Guid("43136EB8-D36C-11CF-ADBC-00AA00A80033")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IImageList
{
    void ImageListSetIcon(IntPtr pIcon, int nLoc);
    void ImageListSetStrip(IntPtr pBMapSm, IntPtr pBMapLg, int nStartLoc, int cMask);
}

[ComImport]
[Guid("85DE64DD-EF21-11cf-A285-00C04FD8DBE6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertySheetCallback
{
    void AddPage(IntPtr hPage);
    void RemovePage(IntPtr hPage);
}

[ComImport]
[Guid("43136EB7-D36C-11CF-ADBC-00AA00A80033")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenuCallback
{
    void AddItem(ref CONTEXTMENUITEM pItem);
}

internal enum MMC_CONTROL_TYPE
{
    TOOLBAR,
    MENUBUTTON,
    COMBOBOXBAR,
}

// A snap-in obtains these via a direct QueryInterface on the IConsole
// pointer it was handed (there is no dedicated "QueryControlbar" method on
// IConsole) - experiment: snap-ins with substantial toolbars (Services,
// Component Services) may probe for this early and fail their own
// Initialize when it's unsupported, since this host never implemented it
// (E_NOINTERFACE) while real mmc.exe always provides one.
[ComImport]
[Guid("69FB811E-6C1C-11D0-A2CB-00C04FD909DD")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IControlbar
{
    void Create(MMC_CONTROL_TYPE nType, IntPtr pExtendControlbar, out IntPtr ppUnknown);
    void Attach(MMC_CONTROL_TYPE nType, IntPtr lpUnknown);
    void Detach(IntPtr lpUnknown);
}

// Obtained by a snap-in via direct QueryInterface on the IConsole pointer,
// same story as IControlbar. GUID verified against the real Windows SDK
// header (um/MMC.h) - it differs from IExtendPropertySheet's GUID
// (85DE64DC-...) only in the last byte before the dash, easy to
// transcribe wrong from memory alone.
[ComImport]
[Guid("85DE64DE-EF21-11cf-A285-00C04FD8DBE6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertySheetProvider
{
    void CreatePropertySheet(
        [MarshalAs(UnmanagedType.LPWStr)] string title,
        [MarshalAs(UnmanagedType.U1)] bool type,
        IntPtr cookie,
        [MarshalAs(UnmanagedType.Interface)] object? pDataObject,
        uint dwOptions);

    // Real MMC returns a failure HRESULT here (not a thrown exception) to
    // mean "no property sheet is already open for this item" - the normal,
    // expected outcome, not an error - hence PreserveSig instead of letting
    // the declarative stub turn it into a .NET exception on every call.
    [PreserveSig]
    int FindPropertySheet(
        IntPtr hItem,
        [MarshalAs(UnmanagedType.Interface)] object? lpComponent,
        [MarshalAs(UnmanagedType.Interface)] object? lpDataObject);

    void AddPrimaryPages(
        [MarshalAs(UnmanagedType.Interface)] object? lpUnknown,
        [MarshalAs(UnmanagedType.Bool)] bool bCreateHandle,
        IntPtr hNotifyWindow,
        [MarshalAs(UnmanagedType.Bool)] bool bScopePane);

    void AddExtensionPages();

    void Show(IntPtr window, int page);
}

// Obtained by a snap-in via direct QueryInterface on the IConsole pointer,
// same story as IControlbar/IPropertySheetProvider - persists per-column
// width/order/sort customizations across sessions. GUID from the real
// Windows SDK header (um/MMC.h). Pointee struct layouts (SColumnSetID,
// MMC_COLUMN_SET_DATA, MMC_SORT_SET_DATA) are not reproduced here since
// this host never actually parses them - the raw IntPtr is opaque to us.
[ComImport]
[Guid("547C1354-024D-11d3-A707-00C04F8EF4CB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IColumnData
{
    void SetColumnConfigData(IntPtr pColID, IntPtr pColSetData);

    // PreserveSig: "no saved config exists for this column set" is a normal,
    // expected outcome (this host never saves any), not an exceptional one.
    [PreserveSig]
    int GetColumnConfigData(IntPtr pColID, out IntPtr ppColSetData);

    void SetColumnSortData(IntPtr pColID, IntPtr pColSortData);

    [PreserveSig]
    int GetColumnSortData(IntPtr pColID, out IntPtr ppColSortData);
}

[ComImport]
[Guid("43136EB9-D36C-11CF-ADBC-00AA00A80033")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IToolbar
{
    void AddBitmap(int nImages, IntPtr hbmp, int cxSize, int cySize, int crMask);
    void AddButtons(int nButtons, IntPtr lpButtons);
    void InsertButton(int nIndex, IntPtr lpButton);
    void DeleteButton(int nIndex);
    void GetButtonState(int idCommand, int nState, [MarshalAs(UnmanagedType.Bool)] out bool pState);
    void SetButtonState(int idCommand, int nState, [MarshalAs(UnmanagedType.Bool)] bool bState);
}

[ComImport]
[Guid("E49F7A60-74AF-11D0-A286-00C04FD8FE93")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IConsoleVerb
{
    void GetVerbState(MMC_CONSOLE_VERB eCmdID, MMC_BUTTON_STATE nState, [MarshalAs(UnmanagedType.Bool)] out bool pState);
    void SetVerbState(MMC_CONSOLE_VERB eCmdID, MMC_BUTTON_STATE nState, [MarshalAs(UnmanagedType.Bool)] bool bState);
    void SetDefaultVerb(MMC_CONSOLE_VERB eCmdID);
    void GetDefaultVerb(out MMC_CONSOLE_VERB peCmdID);
}
