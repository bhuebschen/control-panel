using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace ControlPanel.App.Hosting;

/// <summary>
/// Enumerates snap-ins registered on the machine the same way mmc.exe's
/// "Add/Remove Snap-in" dialog does: under
/// HKLM\SOFTWARE\Microsoft\MMC\SnapIns\{CLSID}. (Not
/// "...\Microsoft Management Console\SnapIns" - that key doesn't exist;
/// per the MMC SDK docs, "MMC" is the literal registry key name.)
/// </summary>
internal static class SnapInRegistry
{
    private const string SnapInsKeyPath = @"SOFTWARE\Microsoft\MMC\SnapIns";

    public static List<SnapInInfo> EnumerateSnapIns()
    {
        var result = new List<SnapInInfo>();

        using var snapInsKey = Registry.LocalMachine.OpenSubKey(SnapInsKeyPath);
        if (snapInsKey is null)
        {
            return result;
        }

        foreach (var clsidString in snapInsKey.GetSubKeyNames())
        {
            if (!Guid.TryParse(clsidString.Trim('{', '}'), out var clsid))
            {
                continue;
            }

            using var key = snapInsKey.OpenSubKey(clsidString);
            if (key is null)
            {
                continue;
            }

            var nameRaw = (key.GetValue("NameString") as string)
                          ?? (key.GetValue(null) as string)
                          ?? clsidString;
            var name = ResolveIndirectString(nameRaw) ?? nameRaw;

            var providerRaw = key.GetValue("Provider") as string;
            var provider = providerRaw is null ? null : (ResolveIndirectString(providerRaw) ?? providerRaw);

            var version = key.GetValue("Version") as string;
            // MMC registration uses Standalone as a subkey. Accepting the
            // legacy/value form too costs nothing and keeps third-party
            // registrations that used it defensively compatible.
            using var standaloneKey = key.OpenSubKey("Standalone");
            var standalone = standaloneKey is not null || key.GetValue("Standalone") is not null;

            Guid? aboutClsid = null;
            if (key.GetValue("About") is string aboutString && Guid.TryParse(aboutString.Trim('{', '}'), out var ac))
            {
                aboutClsid = ac;
            }

            result.Add(new SnapInInfo
            {
                Clsid = clsid,
                Name = name,
                Provider = provider,
                Version = version,
                Standalone = standalone,
                AboutClsid = aboutClsid,
            });
        }

        return result.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Resolves "@shell32.dll,-123"-style indirect resource strings used by
    /// many built-in snap-in registrations, via shlwapi's SHLoadIndirectString.
    /// </summary>
    private static string? ResolveIndirectString(string value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '@')
        {
            return null;
        }

        var buffer = new StringBuilder(1024);
        int hr = SHLoadIndirectString(value, buffer, buffer.Capacity, IntPtr.Zero);
        return hr == 0 ? buffer.ToString() : null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(
        string pszSource,
        StringBuilder pszOutBuf,
        int cchOutBuf,
        IntPtr ppvReserved);
}
