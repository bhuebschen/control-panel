using System.Runtime.InteropServices;

namespace ControlPanel.App.Native;

[StructLayout(LayoutKind.Sequential)]
// Must match the real, modern (ComCtl32 v6 / Windows XP+) PROPSHEETHEADERW
// layout exactly, trailing fields included - even when we never set the
// PSH_USEHBMWATERMARK/PSH_USEHPLWATERMARK/PSH_USEHBMHEADER flags that would
// make PropertySheet() actually read them. Declaring dwSize as
// sizeof(this struct) while the struct itself is missing trailing fields
// the real header has (this one was 16 bytes/2 pointers short - hplWatermark
// and the hbmHeader/pszbmHeader union - confirmed via live debugging into
// ntdll!RtlReportCriticalFailure, a heap-corruption detection, triggered
// immediately on entering PropertySheet()) tells comctl32 the struct is a
// different size than PropertySheet()'s own internal handling expects,
// and it can read or write past the too-small buffer this host actually
// allocated for it.
internal struct PROPSHEETHEADER
{
    public uint dwSize;
    public uint dwFlags;
    public IntPtr hwndParent;
    public IntPtr hInstance;
    public IntPtr hIconOrBitmap;
    [MarshalAs(UnmanagedType.LPWStr)] public string? pszCaption;
    public uint nPages;
    public IntPtr startPageUnion;   // nStartPage (uint) or pStartPage (LPCWSTR), we always use index 0
    public IntPtr ppspOrPhpage;     // pointer to native HPROPSHEETPAGE[] array (PSH_PROPSHEETPAGE not set)
    public IntPtr pfnCallback;
    public IntPtr hbmWatermarkOrPszbmWatermark; // PSH_USEHBMWATERMARK / PSH_USEHPLWATERMARK
    public IntPtr hplWatermark;                 // HPALETTE, always present in the modern struct regardless of flags
    public IntPtr hbmHeaderOrPszbmHeader;       // PSH_USEHBMHEADER
}

internal static class Win32
{
    public const uint PSH_DEFAULT = 0x00000000;
    public const uint PSH_PROPTITLE = 0x00000001;
    public const uint PSH_NOAPPLYNOW = 0x00000080;
    public const uint ICC_STANDARD_CLASSES = 0x00008000;

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    public const uint GMEM_FIXED = 0x0000;

    /// <summary>
    /// Used for IExtendPropertySheet.CreatePropertyPages's "handle"
    /// parameter - real mmc.exe apparently allocates this via GlobalAlloc,
    /// not just any unique value: confirmed via live debugging that at
    /// least one snap-in's page-cleanup code (localsec.dll's
    /// MMCPropertyPage::NotificationState destructor) calls GlobalFree on
    /// whatever value it was given as this handle when the page is
    /// destroyed. A synthetic incrementing IntPtr there is not a valid
    /// GlobalAlloc handle, and GlobalFree-ing it corrupts the process heap
    /// (STATUS_HEAP_CORRUPTION) instead of just failing cleanly.
    /// </summary>
    [DllImport("kernel32.dll")]
    public static extern IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    [StructLayout(LayoutKind.Sequential)]
    public struct INITCOMMONCONTROLSEX
    {
        public uint dwSize;
        public uint dwICC;
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    public static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX icce);

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int PropertySheet(ref PROPSHEETHEADER psh);

    [DllImport("comctl32.dll", SetLastError = true)]
    public static extern bool DestroyPropertySheetPage(IntPtr hPage);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    public static void EnsureCommonControlsInitialized()
    {
        var icce = new INITCOMMONCONTROLSEX
        {
            dwSize = (uint)Marshal.SizeOf<INITCOMMONCONTROLSEX>(),
            dwICC = ICC_STANDARD_CLASSES,
        };
        InitCommonControlsEx(ref icce);
    }
}
