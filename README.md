# MMC Snap-in Host

A .NET 8 WinForms application that hosts MMC (Microsoft Management Console)
snap-ins **without depending on mmc.exe**. It implements the console-side
half of the MMC snap-in protocol itself (the "Node Manager" role mmc.exe
normally plays), so a snap-in loaded into this app cannot tell it apart
from the real console at the COM level.

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
`HKLM\SOFTWARE\Microsoft\Microsoft Management Console\SnapIns`) and drive
it against a WinForms `TreeView` (scope pane) and `ListView` (result pane)
instead of mmc.exe's own UI.

All interface GUIDs and struct layouts (`SCOPEDATAITEM`, `RESULTDATAITEM`,
etc.) were taken verbatim from the public Windows SDK's `mmc.idl`, since
COM identity and struct marshaling both depend on getting these exactly
right.

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

## Known limitations

This is a from-scratch reimplementation of a nontrivial part of Windows,
built and reviewed without access to a Windows machine to run it against
real snap-ins. Treat it as a solid, genuinely-COM-correct starting point,
not a drop-in mmc.exe replacement:

- **No extension snap-ins** (`IExtendContextMenu`, `IExtendControlbar`,
  dynamic `AddExtension`) - only *standalone* (primary) snap-ins are
  supported. Some built-in consoles (e.g. Computer Management) are
  themselves just a shell that only works via extensions and won't be
  useful here; single-purpose standalone snap-ins are the realistic target.
- **No taskpads, no custom OCX/web result views** - list/report view only.
- **No .msc save/load** - snap-ins are added per-session via the picker.
- **No multi-select, cut/copy/paste/drag-drop.**
- Some snap-ins may simply refuse to run outside mmc.exe if they check for
  it explicitly, or rely on undocumented behavior of MMC's real
  implementation. If a snap-in fails to load, the app reports the COM
  error rather than silently failing.
- `Properties` calls `IExtendPropertySheet` without wiring up
  `MMCPropertyChangeNotify` correlation, so "Apply" on a page won't refresh
  the tree/list automatically - closing and reopening properties (or
  Refresh) shows the change.

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
Administrator" if a snap-in appears empty or fails to load.

> This code was written and reviewed in a Linux sandbox with no Windows
> machine or .NET Desktop workload available, so it could not be compiled
> or run end-to-end before delivery. The COM interop layer (GUIDs, struct
> layouts, vtable ordering) was cross-checked against the public Windows
> SDK `mmc.idl`. Please build and smoke-test on Windows before relying on
> it, and report back anything that doesn't compile or misbehaves.
