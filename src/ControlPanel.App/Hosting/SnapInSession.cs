using System.Runtime.InteropServices;
// See ISnapInInterfaces.cs: the WinForms SDK project style implicitly
// pulls in System.Windows.Forms.IDataObject everywhere, which collides
// with the COM one this file means.
using IDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using ControlPanel.App.Interop;
using ControlPanel.App.Native;
using Microsoft.Win32;

namespace ControlPanel.App.Hosting;

/// <summary>
/// One loaded snap-in: its IComponentData plus the single IComponent we
/// use for the (only) view we render. Drives the notification protocol
/// (MMCN_EXPAND / MMCN_SELECT / MMCN_SHOW / MMCN_ADD_IMAGES) that a real
/// console sends to make the snap-in populate the scope tree and result
/// list through the <see cref="MmcConsole"/> callback object.
/// </summary>
internal sealed class SnapInSession
{
    public required SnapInInfo Info { get; init; }
    public required IComponentData ComponentData { get; init; }
    public IComponent Component { get; private set; } = null!;

    public static SnapInSession Load(SnapInInfo info, MmcConsole console)
    {
        CheckBitnessCompatibility(info);

        var type = Type.GetTypeFromCLSID(info.Clsid, throwOnError: true)!;
        var comObject = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"CoCreateInstance returned null for {info.Name}.");

        if (comObject is not IComponentData componentData)
        {
            throw new InvalidOperationException($"'{info.Name}' does not implement IComponentData.");
        }

        var session = new SnapInSession
        {
            Info = info,
            ComponentData = componentData,
        };

        console.RunWithSession(session, () =>
        {
            componentData.Initialize(console);
            componentData.CreateComponent(out object componentObj);
            var component = (IComponent)componentObj;
            session.Component = component;

            // Unlike IComponentData.Initialize (same object+MarshalAs(IUnknown)
            // shape, called two lines above, works fine here), calling
            // IComponent.Initialize through the declarative [ComImport]
            // interface throws InvalidOperationException ("Operation is not
            // valid due to the current state of the object") for "Services"
            // and "Component Services" specifically - confirmed via
            // FirstChanceException logging to be the true throw site, not a
            // rethrow of some other failure, and confirmed via live
            // debugging that the snap-in's native Initialize does get
            // entered (it calls back into SetHeader/QueryScopeImageList
            // before this surfaces). Bypassing coreclr's own interop stub
            // for just this one call - identical technique already proven
            // for QueryScopeImageList/QueryResultImageList/QueryConsoleVerb -
            // avoids whatever internal state check the declarative stub is
            // failing here.
            RawInitializeComponent(component, console);

            // Ask the primary snap-in to insert its static root node(s)
            // (parent handle 0 == our synthetic "Console Root").
            //
            // Tried reordering this before CreateComponent/IComponent.Initialize
            // (matching a hypothesis that real mmc.exe defers IComponent
            // creation until a result view is needed) - it did not fix
            // "Component Services" (identical E_INVALIDARG) and made
            // "Services" fail earlier/differently (E_UNEXPECTED here instead
            // of E_NOINTERFACE at IComponent.Initialize), so call order
            // relative to IComponent is not the cause. Reverted to this order,
            // which is what "Folder"/"ActiveX Control" were validated against.
            //
            // The declarative [ComImport] Notify call throws ArgumentException
            // ("Value does not fall within the expected range") for
            // "Services"/"Component Services" even with a null data object -
            // confirmed via FirstChanceException logging to be the true throw
            // site, not a rethrow. Bypassed via the same raw-vtable technique.
            // TEMPORARY (live-debugging "Component Services"): forcing NULL
            // again to step through comsnap.dll's null-data-object branch,
            // which disassembly showed is a distinct code path (probably the
            // real "populate my own static root" special case) from the one
            // a real data object takes (which does nothing useful). Revert
            // to the QueryDataObject-based lookup above once this is understood.
            IDataObject? rootDataObject = null;
            RawNotify(componentData, rootDataObject, MMC_NOTIFY_TYPE.MMCN_EXPAND, new IntPtr(1), IntPtr.Zero);

            SendAddImages(console, session);
        });

        return session;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ComponentInitializeNative(IntPtr @this, IntPtr lpConsole);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NotifyNative(IntPtr @this, IntPtr lpDataObject, int @event, IntPtr arg, IntPtr param);

    /// <summary>
    /// Raw-vtable Notify call - see the call sites for why. targetIid/slot
    /// select IComponentData::Notify (slot 5) or IComponent::Notify (slot 4);
    /// the vtable layout is QueryInterface/AddRef/Release followed by each
    /// interface's own methods in mmc.idl declaration order.
    /// </summary>
    private static void RawNotify(object target, Guid targetIid, int notifySlot, IDataObject? lpDataObject, MMC_NOTIFY_TYPE @event, IntPtr arg, IntPtr param)
    {
        IntPtr targetUnk = Marshal.GetIUnknownForObject(target);
        try
        {
            int hr = Marshal.QueryInterface(targetUnk, ref targetIid, out IntPtr targetItf);
            Marshal.ThrowExceptionForHR(hr, new IntPtr(-1));
            try
            {
                IntPtr dataObjPtr = lpDataObject is null ? IntPtr.Zero : Marshal.GetComInterfaceForObject(lpDataObject, typeof(IDataObject));
                try
                {
                    NativePointerGuard.EnsureReadable(targetItf, $"{targetIid:B} interface pointer");
                    IntPtr vtable = Marshal.ReadIntPtr(targetItf, 0);
                    NativePointerGuard.EnsureReadable(vtable, $"{targetIid:B} vtable");
                    IntPtr slotPtr = Marshal.ReadIntPtr(vtable, notifySlot * IntPtr.Size);
                    NativePointerGuard.EnsureExecutable(slotPtr, $"{targetIid:B} vtable slot {notifySlot} (Notify)");
                    SnapInDiagnostics.Trace($"RawNotify target={targetItf:X} vtable={vtable:X} slot[{notifySlot}]={slotPtr:X}");
                    var notify = Marshal.GetDelegateForFunctionPointer<NotifyNative>(slotPtr);
                    int result = notify(targetItf, dataObjPtr, (int)@event, arg, param);
                    SnapInDiagnostics.Trace($"RawNotify({@event}, arg=0x{arg:X}, param=0x{param:X}, hasDataObject={lpDataObject is not null}) = 0x{result:X8}");
                    // result < 0, not != 0: S_FALSE (1) is a legitimate
                    // success code some snap-ins return from Notify, not a
                    // failure - only the HRESULT sign bit means failure.
                    if (result < 0)
                    {
                        throw new COMException($"Raw Notify({@event}) returned HRESULT 0x{result:X8}.", result);
                    }
                }
                finally
                {
                    if (dataObjPtr != IntPtr.Zero)
                    {
                        Marshal.Release(dataObjPtr);
                    }
                }
            }
            finally
            {
                Marshal.Release(targetItf);
            }
        }
        finally
        {
            Marshal.Release(targetUnk);
        }
    }

    private static void RawNotify(IComponentData target, IDataObject? lpDataObject, MMC_NOTIFY_TYPE @event, IntPtr arg, IntPtr param) =>
        RawNotify(target, typeof(IComponentData).GUID, 5, lpDataObject, @event, arg, param);

    private static void RawNotify(IComponent target, IDataObject? lpDataObject, MMC_NOTIFY_TYPE @event, IntPtr arg, IntPtr param) =>
        RawNotify(target, typeof(IComponent).GUID, 4, lpDataObject, @event, arg, param);

    /// <summary>
    /// Manually resolves and invokes IComponent::Initialize's vtable slot
    /// (slot 3: QueryInterface, AddRef, Release, then Initialize as the
    /// first method IComponent declares), bypassing the declarative
    /// [ComImport] interface call that throws InvalidOperationException for
    /// some snap-ins - see the call site in Load() for the full story.
    /// </summary>
    private static void RawInitializeComponent(IComponent component, MmcConsole console)
    {
        var iid = typeof(IComponent).GUID;
        // mmc.idl declares this parameter as LPCONSOLE (= IConsole*), not
        // IUnknown* - a snap-in is entitled to use the pointer directly as
        // an IConsole vtable without QueryInterface-ing it first. Handing
        // out our CCW's bare IUnknown identity pointer instead (as the
        // declarative [MarshalAs(UnmanagedType.IUnknown)] binding effectively
        // did) only happens to work for snap-ins that QI before use.
        IntPtr consoleUnk = Marshal.GetComInterfaceForObject(console, typeof(IConsole));
        try
        {
            IntPtr componentUnk = Marshal.GetIUnknownForObject(component);
            try
            {
                int hr = Marshal.QueryInterface(componentUnk, ref iid, out IntPtr componentItf);
                // The IntPtr(-1) "ignore IErrorInfo" overload is deliberate:
                // the default overload picks up whatever IErrorInfo happens
                // to be sitting on the current thread, which can belong to a
                // completely unrelated earlier COM call and produces a
                // misleading exception message/type for *this* HRESULT.
                Marshal.ThrowExceptionForHR(hr, new IntPtr(-1));
                try
                {
                    NativePointerGuard.EnsureReadable(componentItf, $"{iid:B} interface pointer");
                    IntPtr vtable = Marshal.ReadIntPtr(componentItf, 0);
                    NativePointerGuard.EnsureReadable(vtable, $"{iid:B} vtable");
                    IntPtr initializeSlot = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
                    NativePointerGuard.EnsureExecutable(initializeSlot, $"{iid:B} vtable slot 3 (Initialize)");
                    SnapInDiagnostics.Trace($"RawInitializeComponent target={componentItf:X} vtable={vtable:X} slot[3]={initializeSlot:X}");
                    var initialize = Marshal.GetDelegateForFunctionPointer<ComponentInitializeNative>(initializeSlot);
                    int result = initialize(componentItf, consoleUnk);
                    // result < 0, not != 0: only the HRESULT sign bit means
                    // failure - see the identical fix in RawNotify below for
                    // why (S_FALSE == 1 broke "ActiveX Control"/"Folder" here).
                    if (result < 0)
                    {
                        throw new COMException(
                            $"Raw IComponent::Initialize returned HRESULT 0x{result:X8}.", result);
                    }
                }
                finally
                {
                    Marshal.Release(componentItf);
                }
            }
            finally
            {
                Marshal.Release(componentUnk);
            }
        }
        finally
        {
            Marshal.Release(consoleUnk);
        }
    }

    /// <summary>
    /// An in-process (InprocServer32) COM DLL must match the host process's
    /// bitness - a 32-bit-only snap-in cannot load into a 64-bit host, or
    /// vice versa. Without this check, that failure surfaces from
    /// CoCreateInstance as REGDB_E_CLASSNOTREG - the exact same HRESULT as
    /// "not installed at all" - making the real cause impossible to tell
    /// from the error alone. Out-of-process (LocalServer32) servers are not
    /// affected by this - COM marshals across bitness boundaries fine for
    /// those - so this only checks the Inproc case.
    /// </summary>
    private static void CheckBitnessCompatibility(SnapInInfo info)
    {
        using var clsidKey = Registry.ClassesRoot.OpenSubKey($@"CLSID\{info.Clsid:B}\InprocServer32");
        if (clsidKey?.GetValue(null) is not string rawPath || string.IsNullOrWhiteSpace(rawPath))
        {
            return; // Out-of-process server, or nothing registered (CoCreateInstance will report that clearly).
        }

        var path = Environment.ExpandEnvironmentVariables(rawPath);
        if (!PeImage.IsLikelyCompatibleWithCurrentProcess(path, out var dllMachine))
        {
            throw new InvalidOperationException(
                $"'{info.Name}' is registered as a {dllMachine} DLL ({path}), but this host is running as " +
                $"{PeImage.CurrentProcessMachine}. An in-process COM server must match the host process's " +
                "architecture - rebuild/run this host for that architecture, or use a snap-in build that matches it.");
        }
    }

    private static void SendAddImages(MmcConsole console, SnapInSession session)
    {
        IntPtr imageListPtr = Marshal.GetComInterfaceForObject(console.ResultImages, typeof(IImageList));
        try
        {
            // Same declarative-interop-stub bug as the other Notify/Initialize
            // calls above ("Value does not fall within the expected range")
            // hits IComponent.Notify too for "Component Services" - bypassed
            // via the same raw-vtable technique.
            RawNotify(session.Component, null, MMC_NOTIFY_TYPE.MMCN_ADD_IMAGES, imageListPtr, IntPtr.Zero);
        }
        catch (COMException)
        {
            // Optional notification - not every snap-in cares about result-pane images up front.
        }
        finally
        {
            Marshal.Release(imageListPtr);
        }
    }

    public void ExpandNode(MmcConsole console, ScopeNode node)
    {
        if (node.ChildrenLoaded)
        {
            return;
        }

        RemovePlaceholder(console, node);
        node.ChildrenLoaded = true;

        console.RunWithSession(this, () =>
        {
            var dataObject = TryGetScopeDataObject(node);
            Component.Notify(dataObject, MMC_NOTIFY_TYPE.MMCN_EXPAND, new IntPtr(1), node.Handle);
        });
    }

    private static void RemovePlaceholder(MmcConsole console, ScopeNode node)
    {
        if (node.UiNode is { Nodes.Count: 1 } && node.UiNode.Nodes[0].Text == "..." &&
            !console.NodesByUiNode.ContainsKey(node.UiNode.Nodes[0]))
        {
            node.UiNode.Nodes.Clear();
        }
    }

    public void ShowResults(MmcConsole console, ScopeNode node)
    {
        // Verb state belongs to "whatever is selected right now", not to a
        // snap-in or node persistently - reset it before handing the new
        // selection to the snap-in, so a verb the *previous* selection (in
        // this snap-in or a different one entirely) disabled doesn't leak
        // into this one just because nothing here calls SetVerbState again.
        console.ResetVerbStates();
        console.RunWithSession(this, () =>
        {
            var dataObject = TryGetScopeDataObject(node);
            Component.Notify(dataObject, MMC_NOTIFY_TYPE.MMCN_SELECT, MakeSelectArg(scope: true, select: true), IntPtr.Zero);
            Component.Notify(dataObject, MMC_NOTIFY_TYPE.MMCN_SHOW, new IntPtr(1), node.Handle);
        });
        node.ResultsLoaded = true;
    }

    public void HideResults(MmcConsole console, ScopeNode node)
    {
        console.RunWithSession(this, () =>
        {
            var dataObject = TryGetScopeDataObject(node);
            Component.Notify(dataObject, MMC_NOTIFY_TYPE.MMCN_SELECT, MakeSelectArg(scope: true, select: false), IntPtr.Zero);
            Component.Notify(null, MMC_NOTIFY_TYPE.MMCN_SHOW, IntPtr.Zero, node.Handle);
        });
    }

    /// <summary>
    /// Real MMC sends MMCN_SELECT for result-pane selection changes too
    /// (bScope=FALSE), not just for the scope tree - several snap-ins only
    /// enable/disable standard verbs (Properties in particular) in response
    /// to *which* result row is selected, via IConsoleVerb.SetVerbState
    /// inside their MMCN_SELECT handler. Without this, verb state would
    /// only ever reflect the last-selected scope node.
    /// </summary>
    public void NotifyResultSelect(MmcConsole console, ResultRow row, bool selected)
    {
        if (selected)
        {
            // Same reasoning as in ShowResults: a newly-selected row starts
            // from a clean verb-state slate, not whatever the previously
            // selected row (or the scope node itself) left behind.
            console.ResetVerbStates();
        }

        console.RunWithSession(this, () =>
        {
            var dataObject = TryGetResultDataObject(row);
            Component.Notify(dataObject, MMC_NOTIFY_TYPE.MMCN_SELECT, MakeSelectArg(scope: false, select: selected), IntPtr.Zero);
        });
    }

    private static IntPtr MakeSelectArg(bool scope, bool select)
    {
        int low = scope ? 1 : 0;
        int high = select ? 1 : 0;
        return new IntPtr(low | (high << 16));
    }

    public IDataObject? TryGetScopeDataObject(ScopeNode node)
    {
        try
        {
            ComponentData.QueryDataObject(node.Cookie, DATA_OBJECT_TYPES.CCT_SCOPE, out var dataObject);
            return dataObject;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public IDataObject? TryGetResultDataObject(ResultRow row)
    {
        try
        {
            Component.QueryDataObject(row.Cookie, DATA_OBJECT_TYPES.CCT_RESULT, out var dataObject);
            return dataObject;
        }
        catch (COMException)
        {
            return null;
        }
    }
}
