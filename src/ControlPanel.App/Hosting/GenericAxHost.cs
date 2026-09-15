using System.Windows.Forms;

namespace ControlPanel.App.Hosting;

/// <summary>
/// Hosts an arbitrary ActiveX control by CLSID at runtime - used for a
/// snap-in's custom MMC result view (IComponent::GetResultViewType
/// returning a CLSID instead of null/empty for the standard list view).
///
/// AxHost normally expects a design-time, aximp.exe-generated subclass
/// bound to a specific control's type library (for strongly-typed
/// properties/events), but its actual OLE-container machinery
/// (IOleClientSite/IOleInPlaceSite/IOleInPlaceFrame, in-place activation,
/// sizing/window creation) is entirely CLSID-driven internally and works
/// the same regardless - a trivial subclass is enough to reach the
/// protected constructor and GetOcx() for an arbitrary, runtime-known
/// CLSID this project has no compile-time type information for at all.
/// </summary>
internal sealed class GenericAxHost : AxHost
{
    public GenericAxHost(string clsid) : base(clsid)
    {
    }

    /// <summary>
    /// The raw COM object AxHost activated - this is what gets handed back
    /// to the snap-in via IConsole::QueryResultView so it can drive the
    /// view directly through whatever interface it actually implements.
    /// Only valid after the control's window has been created (this
    /// project calls CreateControl() explicitly right after construction
    /// to force that synchronously, rather than waiting for it to become
    /// visible on screen).
    /// </summary>
    public object GetOcxWrapper() => GetOcx();
}
