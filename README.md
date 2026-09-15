# MMC Snap-in Host

A .NET 8 WinForms application that hosts MMC (Microsoft Management Console)
snap-ins **without depending on mmc.exe**. It implements the console-side
half of the MMC snap-in protocol itself (the "Node Manager" role mmc.exe
normally plays): the COM interfaces, GUIDs and struct layouts are the real
ones from the Windows SDK, not a simulation of them.

**Status: builds and runs on real Windows, and has loaded real snap-ins
end-to-end** (see "Verified against real snap-ins" below) - this was
initially written without access to a Windows machine and later built,
debugged and fixed against one; several real interop bugs were found this
way and are documented in "Known limitations" below for what they say
about the parts that haven't been exercised yet.

## Verified against real snap-ins

Using the headless diagnostic (`ControlPanel.App.exe --diag-load-snapin
"<name or {CLSID}>" [output-file]`, see `Native/SnapInDiagnostics.cs`) to
run the full `CoCreateInstance` &rarr; `IComponentData.Initialize` &rarr;
`CreateComponent` &rarr; `IComponent.Initialize` &rarr; `MMCN_EXPAND`
sequence non-interactively against every snap-in registered on a real
Windows 11 machine:

- **Fully successful, end-to-end, with real root scope nodes inserted:**
  the "Folder" and "ActiveX Control" snap-ins (`{C96401CC-...}` /
  `{C96401CF-...}`).
- **Fails for reasons unrelated to this host's interop code** (confirmed
  by testing raw COM calls that bypass this project's C# interface
  declarations entirely): "Classic Event Viewer" - its own `Initialize`
  genuinely returns `E_NOINTERFACE`, most likely because (per its own
  registration name) it's an extension snap-in, never meant to be loaded
  standalone.
- **Real bugs found and fixed this way, in order:** an `InvalidCastException`
  from `IComponent.Initialize(IConsole lpConsole)` (fixed: `object` +
  `MarshalAs(IUnknown)`, see "Known limitations"); after that, an
  `InvalidOperationException`/`NullReferenceException` (different exception
  types for different snap-ins, same underlying cause) thrown by .NET's own
  interop marshaler - not this project's code, not the snap-in's - when
  returning this host's own `IImageList`/`IConsoleVerb` objects through
  `out object` parameters marshaled as `MarshalAs(Interface)`; fixed by
  dropping to a raw `out IntPtr` and calling
  `Marshal.GetComInterfaceForObject` explicitly instead of relying on the
  high-level marshaler for that direction.
- **Progress, not yet fully resolved:** "Services" and "Component Services"
  both now get further than before each fix - reaching real reentrant
  calls into this host's `IConsole`/`IImageList` (`QueryScopeImageList`,
  `SetHeader`) that complete successfully, with nothing further calling
  back into this host afterward (confirmed via a durable, append-per-line
  diagnostic log immune to losing data on a hard crash) - but still
  ultimately fail with a generic `InvalidOperationException` when their
  own `IComponent::Initialize` call returns. Ruled out so far, all with no
  effect on the outcome: every marshaling variant tried, running elevated,
  calling `CoInitializeSecurity` (in case mmc.exe configures DCOM security
  in a way this host's default COM initialization doesn't), adding
  `IControlbar`/`IToolbar` stubs (in case a missing controlbar makes a
  snap-in with real toolbar buttons abort its own `Initialize`), and
  renaming the host executable to `mmc.exe` (in case a snap-in checks its
  own host process's name). The failing HRESULT
  (`0x80131509` = `COR_E_INVALIDOPERATION`, the CLR's own code, confirmed
  by calling `Initialize` through a raw vtable pointer that bypasses this
  project's C# declarations entirely and still getting it back) is real,
  managed-runtime-flavored evidence that *something* about hosting these
  two specific snap-ins outside mmc.exe trips a .NET interop invariant
  this project hasn't identified yet - and real mmc.exe on the exact same
  machine opens both without issue, which rules out an environment/service
  problem. Two independent ReactOS MMC reimplementation attempts
  (`base/applications/mmc` and the more complete `mmc_new`) were checked
  for a reference and turned out not to help - the relevant methods there
  are unfinished `__debugbreak()` stubs or code disabled with `#if 0`,
  never actually exercised against a real snap-in either. "Local Users and
  Groups" hits a related but more severe "Internal CLR error"
  (unrecoverable) at the same general point. Root cause not yet found;
  genuinely getting further here likely needs native (mixed-mode)
  debugging to see what these snap-ins' own code does after the last
  callback into this host returns, which wasn't available this session.

The two full successes prove the core mechanism - real snap-in DLL,
loaded by CLSID, driven through this host's own `IConsole`/
`IConsoleNameSpace2`/`IImageList` implementation with no mmc.exe involved
- genuinely works end-to-end. The remaining failures are a mix of "not
this host's fault" (confirmed) and "real, only partially understood
interop behavior" (flagged honestly above rather than papered over).

## Why this exists / how it's built

Windows does not expose a public API to embed mmc.exe's own console engine
inside a third-party window. Snap-ins are COM servers that implement
`IComponentData` / `IComponent` (plus a handful of extension interfaces);
the console they run inside implements the other half of the contract -
`IConsole`, `IConsoleNameSpace2`, `IHeaderCtrl2`, `IResultData`,
`IDisplayHelp`, etc. - and drives them via a small set of notifications
(`MMCN_EXPAND`, `MMCN_SELECT`, `MMCN_SHOW`, ...).

This project implements that console side directly in C#/COM interop, so
it can `CoCreateInstance` any snap-in registered on the machine (under
`HKLM\SOFTWARE\Microsoft\MMC\SnapIns`) and drive it against a WinForms
`TreeView` (scope pane) and `ListView` (result pane) instead of mmc.exe's
own UI.

All interface GUIDs and struct layouts (`SCOPEDATAITEM`, `RESULTDATAITEM`,
etc.) were taken from the public Windows SDK's `mmc.idl` and hand-ported to
C# interop declarations (there is no MIDL-to-.NET pipeline used here, so
this was a manual transcription, cross-checked against the SDK source
rather than generated from it - see "Known limitations" for what that
implies).

## Project layout

```
src/ControlPanel.App/
  Interop/            COM interface + struct/enum declarations from mmc.idl
  Native/             Win32 P/Invoke (PropertySheet, common controls, GDI)
  Hosting/            The actual "Node Manager": MmcConsole, SnapInSession,
                      registry enumeration, image list + property sheet glue
  Forms/              MainForm (tree/list UI) and the Add Snap-in picker
```

The core of the host is `Hosting/MmcConsole.cs`: a single object that
implements `IConsole2`, `IConsoleNameSpace2`, `IHeaderCtrl2`, `IResultData`
and `IDisplayHelp` simultaneously (exactly as mmc.exe's own Node Manager
does), backed by the WinForms `TreeView`/`ListView`. `Hosting/SnapInSession.cs`
loads one snap-in and drives it through the standard notification sequence.

## Features implemented

- Loading any standalone snap-in registered on the machine, picked from a
  dialog that reads the same registry location mmc.exe's "Add/Remove
  Snap-in" dialog uses.
- Scope pane (namespace tree): lazy expansion, icons (via `IImageList`,
  including icon-strip bitmaps), synchronous and callback-style
  (`MMC_CALLBACK`) display names.
- Result pane (list view): columns via `IHeaderCtrl2`, rows via
  `IResultData`, with lazy per-cell text resolution through
  `IComponent.GetDisplayInfo` (the same "virtual list" pattern MMC itself
  uses, so large result sets don't require querying every cell up front).
- Properties dialogs: queries a node's data object via
  `IComponentData`/`IComponent.QueryDataObject`, negotiates pages through
  `IExtendPropertySheet`/`IPropertySheetCallback`, and displays them with
  the real Win32 `PropertySheet()` API (so the snap-in's own page controls
  render normally).
- Add / Remove snap-in, Refresh, status bar wired to `SetStatusText`.
- `IConsoleVerb` (`QueryConsoleVerb`) is implemented and actually wired to
  the UI: `MMCN_SELECT` is now sent for result-pane selection too (not just
  the scope tree), and the Properties/Refresh menu items and toolbar
  buttons reflect whatever verb state a snap-in last set - a snap-in that
  disables Properties for a given item is respected, not just silently
  stored.
- A 32-/64-bit mismatch between the host process and an in-process
  (`InprocServer32`) snap-in DLL is detected before `CoCreateInstance` is
  even attempted, by reading the target DLL's PE header, so it surfaces as
  a clear message instead of the same generic "class not registered" error
  an unregistered snap-in would produce. **This is not full 32-/64-bit
  support** - see "Known limitations".
- `QueryResultView`/`NewWindow` (custom OCX/web views, multiple console
  windows - both out of scope, see below) log which snap-in hit them via
  `Debug.WriteLine`, since this host can't be debugged remotely.

## Known limitations and open risks

This is a from-scratch reimplementation of a nontrivial, only partially
documented part of Windows, written and reviewed without access to a
Windows machine to compile or run it against real snap-ins. Several real
bugs were already found and fixed this way (see git history), all from
external review rather than execution: the registry path this app read
from was wrong (`...\Microsoft Management Console\SnapIns` instead of the
actual `...\MMC\SnapIns`, which would have made the snap-in picker come up
empty on every machine); scope-item insertion mishandled
`SDI_PREVIOUS`/`SDI_NEXT` relative positioning (siblings could be inserted
as children of the wrong node); the central "Properties" action always
opened the scope node's properties even when a result-pane row was
selected (first fixed by checking which pane had input focus at click
time, then refined into an explicit `MainForm.ActivePane` field updated
via `Control.Enter`, since a focus check alone breaks for keyboard/menu
use and doesn't fall back sensibly when the result pane is active but
empty); and `IConsoleVerb` state was a single dictionary that was never
reset between selections, so a verb one snap-in disabled could stay
disabled after switching to a completely unrelated node or snap-in.

The first bug actually caught by *running* the host (rather than review)
was an `AccessViolationException` in `ImageListAdapter`: despite mmc.idl
typing `ImageListSetIcon`/`ImageListSetStrip`'s handle parameters as
`LONG_PTR*` ("pointer to a pointer-sized value"), real snap-ins pass the
HICON/HBITMAP handle *value itself*, reinterpret-cast to that pointer
type (`ImageListSetIcon((LONG_PTR*)hIcon, nLoc)`), not the address of a
variable holding it. Dereferencing that "pointer" (`Marshal.ReadIntPtr`)
read whatever memory address happened to numerically match the handle
value - not a valid pointer - and crashed the process outright, the one
kind of bug in this codebase category that a try/catch cannot contain
(`AccessViolationException` is a corrupted-state exception the CLR won't
let ordinary code catch). This is exactly the class of defect that only
running against a real snap-in reveals - the parameter *type* was right
and reviewed as such; its documented real-world calling convention was
not what the type name implied.

The next round, still found by running rather than reviewing, was a
family of `InvalidCastException`/`InvalidOperationException` failures
inside .NET's own COM interop marshaling (not this project's code, and
not the snap-in's) whenever a method parameter or `out` value was typed
as one of this project's own custom `[ComImport]` interfaces (`IConsole`,
`IPropertySheetCallback`, `IImageList`, `IConsoleVerb`) to carry an
instance of a class *this host itself implements* (`MmcConsole`,
`ImageListAdapter`, `PropertySheetCallback` - i.e. a CCW, not an RCW).
Confirmed empirically: `IComponent.Initialize(IConsole lpConsole)` threw
`InvalidCastException` when called with a real snap-in
(`{58221C66-...}`, "Services"); changing the parameter to
`object`/`[MarshalAs(UnmanagedType.IUnknown)]` (matching
`IComponentData.Initialize`'s already-correct `pUnknown` parameter)
fixed it, and the same fix was applied consistently everywhere else this
host hands one of its own objects to a snap-in
(`IConsole.QueryScopeImageList`/`QueryResultImageList`/
`QueryConsoleVerb`'s `out` values, `IExtendPropertySheet
.CreatePropertyPages`'s callback, `IExtendContextMenu.AddMenuItems`'s
callback). Receiving a *native* interface pointer into a strongly-typed
RCW (`IComponentData.CreateComponent`'s `out IComponent`,
`IComponentData/IComponent.QueryDataObject`'s `out IDataObject`) was not
itself implicated by this - it's specifically the "hand our own CCW
object to native code through a statically-typed custom interface"
direction that breaks; `CreateComponent`'s `out` value was changed to
`object` anyway for consistency, without a diagnostic proving it was
necessary on its own.

Assume more exist and treat this as a serious-but-unverified starting
point, not a finished, drop-in mmc.exe replacement:

- **No extension snap-ins** (`IExtendContextMenu`, `IExtendControlbar`,
  dynamic `AddExtension`) - only *standalone* (primary) snap-ins are
  supported. Consoles that are themselves just a shell around extensions
  (e.g. Computer Management) won't be useful here; single-purpose
  standalone snap-ins are the realistic target.
- **No taskpads, no custom OCX/web result views, no toolbars/controlbars**
  - list/report view only.
- **No .msc save/load, no persistence** - snap-ins are added per-session via
  the picker, and nothing is passed to a snap-in to tell it *what* to
  target. Several well-known snap-ins need exactly that at add-time -
  Certificates asks "My user account / Service account / Computer
  account", Group Policy Object Editor needs a GPO target - normally
  supplied through mechanisms (wizard pages, `.msc`-stored init data) this
  host doesn't implement. Expect these specific snap-ins to load in a
  degraded, default, or non-functional state rather than to work fully.
- **No multi-select, cut/copy/paste/drag-drop.**
- **The 32-/64-bit check only covers one specific failure mode, not the
  general problem.** This host runs as one process bitness (64-bit by
  default), and `HKEY_LOCAL_MACHINE\SOFTWARE\...` is subject to WOW64
  registry redirection: a 64-bit process transparently sees only the
  64-bit view of `...\Microsoft\MMC\SnapIns` and `CLSID\...\InprocServer32`,
  never the `WOW6432Node` view a 32-bit-only snap-in would be registered
  under (and vice versa for a 32-bit build of this host). A 32-bit-only
  snap-in therefore doesn't reach the bitness check at all - it simply
  never appears in the Add Snap-in list to begin with, silently. The
  check that does exist only catches the narrower case of a snap-in
  that's visible in the current view but whose actual DLL turns out to
  be the wrong architecture. Properly supporting both would mean
  enumerating and offering the other bitness's view too (via
  `RegistryKey.OpenBaseKey(..., RegistryView.Registry32/64)`) and being
  honest in the picker about which of those this process can actually
  load - not yet implemented.
- Some snap-ins may simply refuse to run outside mmc.exe if they check for
  it explicitly, or rely on undocumented behavior of MMC's real
  implementation that this reimplementation doesn't reproduce. If a
  snap-in fails to load, the app reports the COM error rather than
  silently failing - but a snap-in that loads without erroring is not
  proof it's behaving correctly.
- `Properties` calls `IExtendPropertySheet` and shows the resulting pages
  with the real Win32 `PropertySheet()` API, but does not implement the
  `MMCPropertyChangeNotify`/`MMCFreeNotifyHandle` handle-correlation
  protocol a property page's own code may call into (these are exported
  by `mmc.lib`/expected to resolve against the hosting console process).
  Best case, "Apply" just won't refresh the tree/list automatically.
  Worse case, for a page that calls these unconditionally, property pages
  could fail to behave correctly or even to load - this has not been
  verified against a real snap-in.
- `MmcConsole.ActiveSession` is a single ambient "who's calling me right
  now" field, set around each call into a snap-in
  (`MmcConsole.RunWithSession`). This is correct for the synchronous,
  same-thread reentrancy MMC's protocol is built around (a snap-in calling
  back into `IConsoleNameSpace2.InsertItem` while still inside the
  `Notify` call that asked it to), but it is **not** thread-safe: a
  snap-in that calls back from a different thread (e.g. after a background
  operation) would see the wrong - or no - active session. No such
  guard/detection is implemented; this would surface as wrong data
  attribution or a `NullReferenceException`, not a clean error.
- The C# interop declarations in `Interop/` were hand-transcribed from
  `mmc.idl` (cross-checked against the SDK source, not generated from it),
  and only cover the interfaces this host actually uses. Treat any
  interface/struct here as reviewed-but-unverified until it's been
  exercised against a real snap-in on Windows.

## Building

Requires Windows, the .NET 8 SDK, and the Windows Desktop workload
(`dotnet workload install` is not needed for the SDK-provided WinForms
templates - just `dotnet build` from a machine with .NET 8 installed):

```
dotnet build ControlPanel.sln
```

Run `src/ControlPanel.App/bin/Debug/net8.0-windows/ControlPanel.App.exe`.
Many useful snap-ins (Device Manager, Group Policy, Certificates, ...)
require running elevated to fully populate - launch the app "as
Administrator" if a snap-in appears empty or fails to load (though note
running elevated has not, on its own, fixed either of the two snap-ins
currently failing - see "Verified against real snap-ins" above).

To debug why a *specific* snap-in fails to load without going through the
UI each time, use the built-in headless diagnostic:

```
ControlPanel.App.exe --diag-load-snapin "<snap-in name or {CLSID}>" [output-file]
```

It runs the full load sequence outside any message loop, installs an
`AppDomain.FirstChanceException` handler (so exceptions are logged at
their true throw site - reentrant native&rarr;managed callbacks otherwise
lose their original stack trace once they cross back out through the COM
boundary), and writes the complete exception chain to a text file. This is
how every bug listed above was actually found and fixed.
