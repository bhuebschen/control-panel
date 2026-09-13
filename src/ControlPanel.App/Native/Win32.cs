using System.Runtime.InteropServices;

namespace ControlPanel.App.Native;

[StructLayout(LayoutKind.Sequential)]
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
    public IntPtr hbmHeaderOrWatermark;
}

internal static class Win32
{
    public const uint PSH_DEFAULT = 0x00000000;
    public const uint PSH_PROPTITLE = 0x00000001;
    public const uint PSH_NOAPPLYNOW = 0x00000080;
    public const uint ICC_STANDARD_CLASSES = 0x00008000;

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

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
