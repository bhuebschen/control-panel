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

The static-root lifecycle correction described below has now been run
against a real Windows 11 machine via the diagnostic command
(`ControlPanel.App.exe --diag-load-snapin "<name or {CLSID}>"`, which now
exercises all three phases: static node creation, scope expansion, and
result-view initialization) and, for several of these, through the actual
WinForms UI:

- **Full success, real content:** "Local Users and Groups" (1 static root
  node with 2 real children - "Users", "Groups" - previously this snap-in
  hit an unrecoverable "Internal CLR error", traced to this host freeing a
  string pointer it didn't own, see below); "Print Management" (1 static
  root node with 3 real children - "Custom Filters", "Print Servers",
  "Deployed Printers" - the first genuinely complex, multi-level system
  snap-in confirmed working end-to-end); **"Services"** (273 real, distinct
  services listed with correct names/columns - previously blocked first by
  a missing `IImageList` implementation, then by an unrecoverable crash in
  a legacy MFC dependency, both found and fixed, see below); **"Shared
  Folders"** (same underlying DLL and fixes as "Services").
- **Correct, expected empty content:** "Folder" and "ActiveX Control" each
  get their own static root node with legitimately zero children - these
  are generic container/demo snap-ins with no inherent content of their
  own, not a bug.
- **Custom OCX result views ("Component Services", "Disk Management") now
  work, including real content - the fix was deferring one notification by
  a message-pump cycle, not skipping it.** `IComponent.GetResultViewType`
  reports a custom result-view CLSID (`{410381DB-...}` / `{AEB84C83-...}`
  respectively) for these snap-ins, since they render their result pane as
  a custom OCX/tree control rather than the standard list view.
  `Hosting/GenericAxHost.cs` (an `AxHost` subclass bound to an arbitrary
  runtime CLSID) hosts that control directly. Sending the snap-in
  `MMCN_SHOW` immediately afterward, in the same synchronous call chain as
  `CreateControl()`, crashed the whole process - `AxHost.CreateControl()`
  already drives the control through OLE's `InPlaceActive` state
  synchronously, but the snap-in's own `MMCN_SHOW` handler apparently still
  needs a full WinForms message-pump cycle to settle before it's safe to
  reach it. Fixed by posting the `MMCN_SHOW`/`MMCN_SELECT` pair via
  `Control.BeginInvoke` (`MmcConsole.PostToUiThread`) instead of sending it
  synchronously - confirmed via live testing that "Disk Management" now
  shows its real UI with no crash, including a real, correctly-rendered
  error message ("the connection to the Virtual Disk service could not be
  established") when not running elevated, matching real mmc.exe's own
  behavior exactly. The deferred call is guarded against the node having
  changed again before it runs (`MmcConsole.CustomResultViewObject`
  identity check) and against a non-fatal exception reaching WinForms'
  default unhandled-exception dialog - it is not, and cannot be, guarded
  against a repeat `AccessViolationException` if some other custom-view
  snap-in hits a differently-shaped version of this same timing issue.
  "Classic Event Viewer" is correctly rejected up front as an
  extension-only snap-in (`Standalone` registry check) instead of being
  force-loaded as standalone and failing with a
  confusing native `E_NOINTERFACE`.
- **The `E_NOINTERFACE` at `IComponent::Initialize` for "Services" and
  "Shared Folders" - found and fixed:** both are implemented by the exact
  same DLL (`filemgmt.dll`), which explained why they failed identically.
  Live debugging straight into `CComponent::Initialize`'s disassembly
  showed it calling `QueryInterface(IID_IImageList)` directly on the
  console object itself (separately from - and in addition to - the
  `QueryScopeImageList`/`QueryResultImageList` methods on `IConsole`,
  which return their *own* dedicated `IImageList` objects) - real mmc.exe's
  console object apparently answers this directly too, but `MmcConsole`
  never implemented `IImageList` itself. Fixed by having `MmcConsole`
  implement `IImageList` too (delegating to the scope image list).
- **A new, deeper problem surfaced immediately after that fix, for the
  same two snap-ins - and it turned out to be unfixable from outside the
  process:** with `Initialize` now succeeding, both crash the whole
  process with an unrecoverable `AccessViolationException` shortly after,
  reading from address `0x0`. Live native debugging traced it to
  `mfc42u.dll!CCmdTarget::BeginWaitCursor`, called from
  `filemgmt.dll!CWaitCursor::CWaitCursor` while the snap-in populates its
  result list - `filemgmt.dll` is a **legacy MFC (Microsoft Foundation
  Classes) extension DLL** from the Visual C++ 6 / MFC 4.2 era, and
  `BeginWaitCursor` dereferences the process's `CWinApp` instance (via
  `AfxGetApp()`), which is `NULL` because this host never creates one - it
  is a plain WinForms/.NET application, not an MFC one, and real mmc.exe
  apparently has (or provides) one. A native "shim" DLL that constructs a
  minimal `CWinApp` was considered, but investigated and ruled out: (1) no
  MFC component is installed with this machine's Visual Studio toolchain,
  and (2) even if it were, it would be the wrong, ABI-incompatible modern
  MFC (14.x) - `AfxGetApp`/`AfxWinInit`/`AfxGetModuleState` are `inline`
  functions compiled directly into *each calling module* in classic MFC,
  reading that module's own private, unexported state, confirmed absent
  from both `mfc42u.dll`'s real export table (`dumpbin /EXPORTS`, only 8 of
  6884 exports are named at all) and Ghidra's independently-curated
  `mfc42u.dll` ordinal database - there is no stable, external way to
  construct a real, ABI-compatible `CWinApp` for `filemgmt.dll` to find.
- **Solved anyway, with a different technique: both "Services" and "Shared
  Folders" now load fully, with real content** (273 real services listed
  for "Services", correct column data throughout). Since a real `CWinApp`
  can't be constructed externally, `Native/MfcCompatibilityShim.cs`
  installs a Windows **Vectored Exception Handler** (a standard, documented
  OS mechanism, set up once via `AddVectoredExceptionHandler` before any
  snap-in loads) that traps this *exact* crash - an access violation
  reading address `0x0`, with the `this`-pointer register (`RCX`) also
  `0`, at an instruction confirmed (via `VirtualQuery`) to live inside
  `mfc42u.dll` itself - and redirects `RCX` to a fake "app object" whose
  entire (large) vtable points at a single real, exported,
  Control-Flow-Guard-valid function (`kernel32!GetCurrentThreadId`, chosen
  specifically because CFG's indirect-call check requires a genuinely
  address-taken target - an arbitrary `VirtualAlloc`'d "ret stub" would
  itself be a fatal, uncatchable CFG violation instead). Execution resumes
  at the exact same faulting instruction, now succeeds, and the eventual
  indirect call reaches the harmless stand-in function instead of crashing;
  since this call site is purely wait-cursor bookkeeping whose result
  nothing downstream consults, the net effect is just "no wait cursor
  shown," not corrupted state. This is a narrow, first-cut heuristic for
  the one exact crash shape found so far (this specific register, this
  specific module, a read of exactly address zero) - a differently-shaped
  null-`CWinApp` crash elsewhere would not match and would still be fatal;
  broadening the heuristic (or adding more matched shapes) is
  straightforward if that happens with another MFC-based snap-in.
- **Icons from one loaded snap-in changing - and the wrong new icons then
  showing up in an already-loaded, different snap-in - the moment a second
  snap-in is added:** every snap-in shares this host's one `MmcConsole`
  (and therefore its one scope/result `ImageList` each) for the process's
  whole lifetime, but each snap-in numbers its own icons from `0` via
  `IImageList::ImageListSetIcon`/`SetStrip`, with no idea any other
  snap-in shares the list - a second snap-in's icon `0` silently overwrote
  the first snap-in's icon already sitting at that same shared index.
  Fixed by giving each `SnapInSession` its own reserved, non-overlapping
  block of indices in each shared list (`SnapInSession.ScopeImageBase`/
  `ResultImageBase`, allocated by `MmcConsole.AllocateImageBases`) -
  `ImageListAdapter` adds the calling session's base before writing into
  the real, shared list, and `MmcConsole.OffsetImageIndex` adds it again
  wherever a `ScopeNode`/`ResultRow`'s own (still snap-in-raw) `ImageIndex`
  is used to set an actual `TreeNode`/`ListViewItem`'s image index. Not
  reproducible by the single-snap-in `--diag-load-snapin` diagnostic -
  needs at least two snap-ins loaded in the same running instance to
  exercise the shared list at all.
- **This shim is incompatible with an attached debugger.** With Visual
  Studio attached, even with the Win32 "Access Violation" exception
  setting unchecked (so VS itself doesn't break on it), loading "Services"
  or "Shared Folders" crashes with an unhandled
  `System.ExecutionEngineException` instead of recovering - worse than the
  original crash, since it signals the CLR considers its own internal
  state corrupted. A debugger sits *between* the OS and the process's own
  exception chain (first-chance exceptions go to the debug port before
  reaching the process's vectored handlers at all), and resuming execution
  mid-fault via `EXCEPTION_CONTINUE_EXECUTION` through that extra layer
  appears to desynchronize CoreCLR's own internal exception bookkeeping in
  a way it does not tolerate. Confirmed to work cleanly with no debugger
  attached at all (every `--diag-load-snapin` test run in this section was
  run that way) - **load "Services"/"Shared Folders" only via Ctrl+F5
  ("Start Without Debugging") or by running the built .exe directly**,
  never under an attached debugger.
- **The shim's first version caused intermittent native heap corruption
  (`STATUS_HEAP_CORRUPTION` / `0xC0000374`, seen via Windows Error
  Reporting) when actually opening and closing a Properties dialog for
  "Services"/"Local Users and Groups"** - worse than the crash it was
  meant to fix, since heap corruption can surface anywhere, later,
  unpredictably. Cause: the fake "app object" was only 8 bytes (just
  enough to hold its own vtable pointer) - fine for the one-off virtual
  call the wait-cursor crash needed, but a real property page (itself
  backed by the same MFC-based DLL) goes further and can read *or write*
  `CWinApp` member fields directly at other offsets from `this`, not just
  dispatch through the vtable - any write past 8 bytes corrupted whatever
  heap memory happened to follow it. Fixed by making the dummy object a
  full 4KB, zero-initialized, and pre-filled with the same vtable pointer
  at every 8-byte offset (covering the case where some other offset is
  itself read as a secondary vtable pointer, which C++ multiple
  inheritance - which `CWinApp` uses - can produce).
- **Every result-pane row showing the exact same text - found by "Local
  Users and Groups" listing 5 "users" that all read "Lokale Benutzer und
  Gruppen (lokal)" (the snap-in's own root description) instead of their
  actual account names:** live debugging into `localsec.dll!Component
  ::GetDisplayInfo` showed it reads the row's identity from
  `RESULTDATAITEM.lParam` (via `ComponentData::GetInstanceFromCookie`), not
  from `itemID` as this host had assumed - `GetResultColumnText` never set
  `RDI_PARAM`/`lParam` on its request, so `lParam` stayed zero for every
  row, and cookie zero conventionally means "the root item" - explaining
  why every row resolved to the same (root) description. Fixed by resupplying
  `RDI_PARAM` and the row's own `Cookie` (already captured at insert time)
  on every `GetDisplayInfo` request. A declarative-`[ComImport]`-marshaling
  bug for this same call was also independently found and fixed along the
  way (a `ref struct` parameter's input fields weren't reliably reaching
  the native call) - bypassed with the same raw-vtable-plus-manual-struct-
  marshaling technique used elsewhere in this project, though it turned out
  not to be the actual cause of the wrong-text symptom itself.
- **Properties dialogs silently empty ("This item does not provide a
  Properties page") for a real service row, even though the snap-in's
  `IExtendPropertySheet::QueryPagesFor` reported `S_OK`:** unlike every
  other CCW-argument bug found in this project, `CreatePropertyPages`
  didn't throw at all - it just never called back into this host's
  `PropertySheetCallback.AddPage`, silently returning zero pages. Fixed the
  same way as `IComponent::Initialize`'s `lpConsole` parameter: bypassed
  the declarative `[ComImport]` call and marshaled `lpProvider` as the
  exact interface type mmc.idl declares (`LPPROPERTYSHEETCALLBACK`, not a
  bare `IUnknown*`) via a raw vtable call
  (`PropertySheetHost.RawCreatePropertyPages`). Confirmed against real
  data: "Services" now shows 3 real property pages for an actual service,
  "Local Users and Groups" shows 3 for a real user account.
- **A real, confirmed-fixed bug, found by "Print Management" showing every
  child as a sibling of its parent instead of nested underneath it:**
  `IConsoleNameSpace::InsertItem`'s `relativeID=0` means "attach under
  whatever scope item is currently being expanded", not "attach under the
  absolute root" - see "Known limitations" for the fix
  (`MmcConsole.ActiveExpandHandle`).
- **Confirmed, via live mixed-mode debugging into `puiobj.dll`'s
  `TComponentData::Notify`, that "Print Management"'s empty "Print
  Servers" node is not a bug in this host:** the full call chain - our
  `Notify(MMCN_EXPAND)` call reaching the snap-in, it extracting the node
  type from our data object (`NMMCLibrary::GetNodeTypeFromDataObject`),
  `QueryInterface`-ing that same data object for two private interfaces
  (`ISnapinNodeDetails`, `ISnapinNotify` - both specific to this snap-in,
  obtained by round-tripping the data object this host got from
  `QueryDataObject` right back to the snap-in, not anything this host
  implements) and finally invoking `ISnapinNotify::Notify(event, arg,
  param)` - was traced instruction-by-instruction and returns a clean
  `S_OK` (`EAX=0`) at every step, including the final delegated call. The
  snap-in itself simply has no print servers to report: it almost
  certainly consults a persisted list of previously-added servers
  (normally populated via "Add/Remove Servers..." in a real mmc.exe
  session, or carried over from a saved `.msc` file), and this host starts
  every session from a blank slate - see "No .msc save/load, no
  persistence" below. This is a real scope boundary, not an interop defect.

This replaces an earlier, incorrect lifecycle where this host asked the
*snap-in* to insert its own static root node via `Notify(MMCN_EXPAND,
cookie=0)` immediately on load. Real MMC does this itself - a standalone
snap-in's static node is the console's own creation, inserted before the
snap-in is ever asked to enumerate anything; the snap-in only populates
*children* under that node, lazily, when it's actually expanded, and only
gets an `IComponent` (a result-pane rendering object) created and
initialized when its result view is actually about to be shown - not
eagerly at load time. Getting this wrong didn't just fail cleanly: some
snap-ins accepted the wrong-shaped calls and silently produced zero
content (misread earlier as "loads, but has nothing to show" rather than
"loading is fundamentally accepting the wrong protocol steps").

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

Also found by running rather than reviewing: `IComponentData`/`IComponent
.GetDisplayInfo` returns its display-name string through a pointer this
host does **not** own - mmc.idl's convention is that the snap-in retains
that allocation (until the next `GetDisplayInfo` call for the same item,
item deletion, or its own `Destroy`), unlike the more common COM pattern
where an `[out] BSTR`/`LPWSTR` parameter transfers ownership to the caller.
This host originally called `Marshal.FreeCoTaskMem` on it after copying
the string out, on the (wrong, but very standard-COM-shaped) assumption
that it was the caller's to free - corrupting the snap-in's own heap
allocator. The practical symptom was not a clean exception: "Local Users
and Groups" hit an unrecoverable "Internal CLR error" (the process
terminates outright, no catchable exception, no stack trace) sometime
after the corrupted heap was reused. Fixed by simply not freeing that
pointer; this host only ever borrows it. This is the same general lesson
as the `ImageListSetIcon`/`ImageListSetStrip` bug above - a parameter's
*type* being correct (or, here, entirely implicit/undocumented in the
struct layout itself) doesn't mean its ownership/calling convention
matches the more common COM pattern the type name suggests, and this
category of bug doesn't surface until a real snap-in's code path actually
exercises it.

Found immediately after the static-root lifecycle correction above, by
running the newly-recursive diagnostic against "Print Management": every
child a snap-in inserted with `relativeID=0` (the common case - a mask of
`SDI_PARENT`, MMC's default, with a zero handle) was attached directly
under this host's own synthetic, invisible "Console Root" container,
*not* under the scope item currently being expanded. A snap-in is
entitled to pass `relativeID=0` during its `Notify(MMCN_EXPAND)` handler
to mean "attach under whatever item this expand call is for" instead of
repeating that item's own `HSCOPEITEM` - this host was instead treating
zero as always meaning the true root, unconditionally. The practical
effect: every one of "Print Management"'s children ("Custom Filters",
"Print Servers", "Deployed Printers") rendered as *siblings* of "Print
Management" itself rather than nested under it (and identically for
"Local Users and Groups"'s "Users"/"Groups") - structurally wrong, but not
wrong in a way that throws, so nothing on the failure path caught it.
Fixed by tracking which `HSCOPEITEM` a `Notify(MMCN_EXPAND)` call is
currently in flight for (`MmcConsole.ActiveExpandHandle`, set by
`SnapInSession.ExpandNode`) and using it as the effective parent whenever
`InsertItem` receives a zero `relativeID` during that window.

Assume more exist and treat this as a serious-but-unverified starting
point, not a finished, drop-in mmc.exe replacement:

- **No extension snap-ins** (`IExtendContextMenu`, `IExtendControlbar`,
  dynamic `AddExtension`) - only *standalone* (primary) snap-ins are
  supported. Consoles that are themselves just a shell around extensions
  (e.g. Computer Management) won't be useful here; single-purpose
  standalone snap-ins are the realistic target.
- **No taskpads, no toolbars/controlbars.** Custom OCX result views
  ("Component Services", "Disk Management") do now work, including real
  content - see "Verified against real snap-ins" above.
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
  with the real Win32 `PropertySheet()` API. This originally flagged a
  theoretical risk around the `handle` (`LONG_PTR`) parameter of
  `CreatePropertyPages` - and it turned out to be real, found the hard way:
  `PropertySheetHost` originally passed a synthetic incrementing `IntPtr`
  as that handle, since nothing in the interop declarations said otherwise.
  Live debugging into a genuine, intermittent (only on close, not every
  time) native heap corruption (`STATUS_HEAP_CORRUPTION`) traced it to
  `localsec.dll!MMCPropertyPage::NotificationState`'s destructor calling
  `GlobalFree()` directly on that value when a page is destroyed - real
  mmc.exe apparently allocates this handle via `GlobalAlloc`, and
  `GlobalFree`-ing anything else (like a small integer) corrupts the
  process heap instead of failing cleanly. Fixed by actually calling
  `Win32.GlobalAlloc` for this handle and never freeing it here (ownership
  passes to the snap-in). `MMCPropertyChangeNotify` itself remains
  unimplemented - a page's "Apply" may still not refresh the tree/list -
  but page open/close no longer corrupts the process. A separate,
  unrelated bug in this project's own `PROPSHEETHEADER` struct (missing
  the modern, ComCtl32-v6-era trailing `hplWatermark`/`hbmHeader` fields,
  reported `dwSize` therefore describing a smaller struct than
  `PropertySheet()` expects) was found and fixed alongside this.
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
