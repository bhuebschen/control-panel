using System.Runtime.InteropServices;

namespace ControlPanel.App.Interop;

/// <summary>
/// Constants, enums and structs from the MMC (Microsoft Management Console)
/// SDK's mmc.idl, reproduced here so this application can act as its own
/// snap-in host (a "Node Manager") instead of relying on mmc.exe.
///
/// Verified against the public Windows SDK source
/// (Include/&lt;version&gt;/um/MMC.Idl) to keep GUIDs and struct layouts
/// exact - COM identity and struct marshaling both depend on getting these
/// byte-for-byte right, since a real snap-in DLL (Device Manager,
/// Certificates, Group Policy, ...) will call back into these shapes.
/// </summary>
internal enum MMC_NOTIFY_TYPE
{
    MMCN_ACTIVATE = 0x8001,
    MMCN_ADD_IMAGES = 0x8002,
    MMCN_BTN_CLICK = 0x8003,
    MMCN_CLICK = 0x8004,
    MMCN_COLUMN_CLICK = 0x8005,
    MMCN_CONTEXTMENU = 0x8006,
    MMCN_CUTORMOVE = 0x8007,
    MMCN_DBLCLICK = 0x8008,
    MMCN_DELETE = 0x8009,
    MMCN_DESELECT_ALL = 0x800A,
    MMCN_EXPAND = 0x800B,
    MMCN_HELP = 0x800C,
    MMCN_MENU_BTNCLICK = 0x800D,
    MMCN_MINIMIZED = 0x800E,
    MMCN_PASTE = 0x800F,
    MMCN_PROPERTY_CHANGE = 0x8010,
    MMCN_QUERY_PASTE = 0x8011,
    MMCN_REFRESH = 0x8012,
    MMCN_REMOVE_CHILDREN = 0x8013,
    MMCN_RENAME = 0x8014,
    MMCN_SELECT = 0x8015,
    MMCN_SHOW = 0x8016,
    MMCN_VIEW_CHANGE = 0x8017,
    MMCN_SNAPINHELP = 0x8018,
    MMCN_CONTEXTHELP = 0x8019,
    MMCN_INITOCX = 0x801A,
    MMCN_FILTER_CHANGE = 0x801B,
    MMCN_FILTERBTN_CLICK = 0x801C,
    MMCN_RESTORE_VIEW = 0x801D,
    MMCN_PRINT = 0x801E,
    MMCN_PRELOAD = 0x801F,
    MMCN_LISTPAD = 0x8020,
    MMCN_EXPANDSYNC = 0x8021,
    MMCN_COLUMNS_CHANGED = 0x8022,
    MMCN_CANPASTE_OUTOFPROC = 0x8023,
}

internal enum DATA_OBJECT_TYPES
{
    CCT_SCOPE = 0x8000,
    CCT_RESULT = 0x8001,
    CCT_SNAPIN_MANAGER = 0x8002,
    CCT_UNINITIALIZED = 0xFFFF,
}

internal enum MMC_RESULT_VIEW_STYLE
{
    MMC_SINGLESEL = 0x0001,
    MMC_SHOWSELALWAYS = 0x0002,
    MMC_NOSORTHEADER = 0x0004,
    MMC_ENSUREFOCUSVISIBLE = 0x0008,
}

internal static class MmcConsts
{
    // SCOPEDATAITEM.mask (SDI_*)
    public const uint SDI_STR = 0x00002;
    public const uint SDI_IMAGE = 0x00004;
    public const uint SDI_OPENIMAGE = 0x00008;
    public const uint SDI_STATE = 0x00010;
    public const uint SDI_PARAM = 0x00020;
    public const uint SDI_CHILDREN = 0x00040;
    public const uint SDI_PARENT = 0x00000000;
    public const uint SDI_PREVIOUS = 0x10000000;
    public const uint SDI_NEXT = 0x20000000;
    public const uint SDI_FIRST = 0x08000000;

    // RESULTDATAITEM.mask (RDI_*)
    public const uint RDI_STR = 0x0002;
    public const uint RDI_IMAGE = 0x0004;
    public const uint RDI_STATE = 0x0008;
    public const uint RDI_PARAM = 0x0010;
    public const uint RDI_INDEX = 0x0020;
    public const uint RDI_INDENT = 0x0040;

    // Sentinel for LPOLESTR fields meaning "call GetDisplayInfo for the real text"
    public static readonly IntPtr MMC_CALLBACK = new(-1);

    public const int MMCLV_AUTO = -1;
    public const int MMCLV_NOICON = -1;

    public static readonly IntPtr MMC_MULTI_SELECT_COOKIE = new(-2);
    public static readonly IntPtr MMC_WINDOW_COOKIE = new(-3);
}

[StructLayout(LayoutKind.Sequential)]
internal struct SCOPEDATAITEM
{
    public uint mask;
    public IntPtr displayname;   // LPOLESTR - CoTaskMem string, or MMC_CALLBACK sentinel
    public int nImage;
    public int nOpenImage;
    public uint nState;
    public int cChildren;
    public IntPtr lParam;        // snap-in's private cookie for this node
    public IntPtr relativeID;    // HSCOPEITEM - sibling/parent depending on mask high bits
    public IntPtr ID;            // HSCOPEITEM - filled in by the console on InsertItem
}

[StructLayout(LayoutKind.Sequential)]
internal struct RESULTDATAITEM
{
    public uint mask;
    [MarshalAs(UnmanagedType.Bool)]
    public bool bScopeItem;
    public IntPtr itemID;        // HRESULTITEM
    public int nIndex;
    public int nCol;
    public IntPtr str;           // LPOLESTR
    public int nImage;
    public uint nState;
    public IntPtr lParam;
    public int iIndent;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct CONTEXTMENUITEM
{
    [MarshalAs(UnmanagedType.LPWStr)] public string strName;
    [MarshalAs(UnmanagedType.LPWStr)] public string strStatusBarText;
    public int lCommandID;
    public int lInsertionPointID;
    public int fFlags;
    public int fSpecialFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MMC_FILTERDATA
{
    public IntPtr pszText;
    public int cchTextMax;
    public int lValue;
}
