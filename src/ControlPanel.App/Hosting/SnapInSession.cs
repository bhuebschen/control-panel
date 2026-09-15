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

    /// <summary>See RawGetDisplayInfo for why this bypasses the declarative interface call.</summary>
    public void GetResultDisplayInfo(ref RESULTDATAITEM item) => RawGetDisplayInfo(Component, ref item);

    private bool _componentCreated;
    private bool _componentDataInitialized;
    private bool _destroyed;

    public ScopeNode RootNode { get; private set; } = null!;

    /// <summary>
    /// Every loaded snap-in shares the same underlying scope/result
    /// ImageList (one MmcConsole for the whole app), but each snap-in
    /// numbers its own icons from 0 with no knowledge of any other
    /// snap-in sharing the list. Without a per-session offset, a second
    /// snap-in's ImageListSetIcon(hIcon, 0) overwrites the first snap-in's
    /// icon at that same index - confirmed: loading a second .msc changed
    /// icons already showing in the first one. ScopeImageBase/ResultImageBase
    /// are this session's reserved starting index in each shared list;
    /// see ImageListAdapter and the ImageIndex-using code in MmcConsole.
    /// </summary>
    public int ScopeImageBase { get; init; }
    public int ResultImageBase { get; init; }

    public static SnapInSession Load(SnapInInfo info, MmcConsole console)
    {
        CheckBitnessCompatibility(info);

        if (!info.Standalone)
        {
            throw new NotSupportedException(
                $"'{info.Name}' is registered as an extension snap-in. Extension snap-ins require a primary " +
                "snap-in node whose node type they extend; they cannot be inserted as a standalone root.");
        }

        var type = Type.GetTypeFromCLSID(info.Clsid, throwOnError: true)!;
        var comObject = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"CoCreateInstance returned null for {info.Name}.");

        if (comObject is not IComponentData componentData)
        {
            throw new InvalidOperationException($"'{info.Name}' does not implement IComponentData.");
        }

        console.AllocateImageBases(out int scopeImageBase, out int resultImageBase);
        var session = new SnapInSession
        {
            Info = info,
            ComponentData = componentData,
            ScopeImageBase = scopeImageBase,
            ResultImageBase = resultImageBase,
        };

        try
        {
            console.RunWithSession(session, () =>
            {
                session._componentDataInitialized = true;
                componentData.Initialize(console);

                // MMC itself inserts a standalone snap-in's static node.
                // Cookie zero identifies that node. The snap-in only inserts
                // its enumerated children later in response to MMCN_EXPAND.
                session.RootNode = console.InsertStaticNode(session, info.Name);
            });

            return session;
        }
        catch
        {
            try
            {
                session.Destroy();
            }
            catch
            {
                // Preserve the original load failure.
            }

            throw;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ComponentInitializeNative(IntPtr @this, IntPtr lpConsole);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NotifyNative(IntPtr @this, IntPtr lpDataObject, int @event, IntPtr arg, IntPtr param);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ComponentGetDisplayInfoNative(IntPtr @this, IntPtr pResultDataItem);

    /// <summary>
    /// Raw-vtable IComponent::GetDisplayInfo(ref RESULTDATAITEM) call - the
    /// declarative [ComImport] binding for this one (unlike the plain-IntPtr
    /// calls above) marshals a *struct passed by ref*, and for "Local Users
    /// and Groups" every row came back with the exact same text (the
    /// snap-in's own static-node description) regardless of itemID/cookie -
    /// strongly suggesting the struct's input fields (itemID/nCol in
    /// particular) never actually reached the native call, so the snap-in
    /// always saw the same (probably zeroed/default) request. Bypassing the
    /// stub and marshaling the struct into native memory ourselves
    /// (Marshal.StructureToPtr/PtrToStructure) fixed it.
    /// </summary>
    private static void RawGetDisplayInfo(IComponent component, ref RESULTDATAITEM item)
    {
        var iid = typeof(IComponent).GUID;
        IntPtr componentUnk = Marshal.GetIUnknownForObject(component);
        try
        {
            int hr = Marshal.QueryInterface(componentUnk, ref iid, out IntPtr componentItf);
            Marshal.ThrowExceptionForHR(hr, new IntPtr(-1));
            try
            {
                NativePointerGuard.EnsureReadable(componentItf, $"{iid:B} interface pointer");
                IntPtr vtable = Marshal.ReadIntPtr(componentItf, 0);
                NativePointerGuard.EnsureReadable(vtable, $"{iid:B} vtable");
                // Slot 8: QueryInterface/AddRef/Release, then Initialize(3),
                // Notify(4), Destroy(5), QueryDataObject(6),
                // GetResultViewType(7), GetDisplayInfo(8) - matching this
                // interface's declaration order in ISnapInInterfaces.cs.
                IntPtr slotPtr = Marshal.ReadIntPtr(vtable, 8 * IntPtr.Size);
                NativePointerGuard.EnsureExecutable(slotPtr, $"{iid:B} vtable slot 8 (GetDisplayInfo)");
                var getDisplayInfo = Marshal.GetDelegateForFunctionPointer<ComponentGetDisplayInfoNative>(slotPtr);

                IntPtr nativeItem = Marshal.AllocHGlobal(Marshal.SizeOf<RESULTDATAITEM>());
                try
                {
                    SnapInDiagnostics.Trace(
                        $"RawGetDisplayInfo IN: mask=0x{item.mask:X8} itemID=0x{item.itemID:X} nCol={item.nCol} lParam=0x{item.lParam:X}");
                    Marshal.StructureToPtr(item, nativeItem, fDeleteOld: false);
                    int result = getDisplayInfo(componentItf, nativeItem);
                    item = Marshal.PtrToStructure<RESULTDATAITEM>(nativeItem);
                    SnapInDiagnostics.Trace(
                        $"RawGetDisplayInfo OUT: hr=0x{result:X8} str=0x{item.str:X} text='{Marshal.PtrToStringUni(item.str)}'");
                    if (result < 0)
                    {
                        throw new COMException(
                            $"Raw IComponent::GetDisplayInfo returned HRESULT 0x{result:X8}.", result);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(nativeItem);
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
                    // Log the event before crossing the native boundary.  An
                    // AccessViolation raised inside a snap-in never returns,
                    // so a post-call-only event name made the crash log
                    // needlessly ambiguous.
                    SnapInDiagnostics.Trace(
                        $"RawNotify entering {@event}: target={targetItf:X} vtable={vtable:X} " +
                        $"slot[{notifySlot}]={slotPtr:X}, arg=0x{arg:X}, param=0x{param:X}, " +
                        $"hasDataObject={lpDataObject is not null}");
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

    private void EnsureComponent(MmcConsole console)
    {
        if (_componentCreated)
        {
            return;
        }

        console.RunWithSession(this, () =>
        {
            ComponentData.CreateComponent(out object componentObject);
            var component = (IComponent)componentObject;
            Component = component;

            try
            {
                RawInitializeComponent(component, console);
                _componentCreated = true;
                SendAddImages(console, this);
            }
            catch
            {
                try
                {
                    component.Destroy(IntPtr.Zero);
                }
                catch
                {
                    // Preserve the initialization exception.
                }

                _componentCreated = false;
                Component = null!;
                throw;
            }
        });
    }

    public void Destroy()
    {
        if (_destroyed)
        {
            return;
        }

        _destroyed = true;
        Exception? firstFailure = null;

        if (_componentCreated)
        {
            try
            {
                Component.Destroy(IntPtr.Zero);
            }
            catch (Exception ex)
            {
                firstFailure = ex;
            }

            _componentCreated = false;
            Component = null!;
        }

        if (_componentDataInitialized)
        {
            try
            {
                ComponentData.Destroy();
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
            }

            _componentDataInitialized = false;
        }

        if (firstFailure is not null)
        {
            throw firstFailure;
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
        if (node.ChildrenLoaded || !node.HasChildren)
        {
            return;
        }

        RemovePlaceholder(console, node);

        try
        {
            console.RunWithSession(this, () =>
            {
                var dataObject = TryGetScopeDataObject(node);
                var previousExpandHandle = console.ActiveExpandHandle;
                console.ActiveExpandHandle = node.Handle;
                try
                {
                    RawNotify(
                        ComponentData,
                        dataObject,
                        MMC_NOTIFY_TYPE.MMCN_EXPAND,
                        new IntPtr(1),
                        node.Handle);
                }
                finally
                {
                    console.ActiveExpandHandle = previousExpandHandle;
                }
            });

            node.ChildrenLoaded = true;
        }
        catch
        {
            if (node.UiNode is { Nodes.Count: 0 })
            {
                node.UiNode.Nodes.Add(new TreeNode("..."));
            }

            throw;
        }
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
        EnsureComponent(console);

        // Verb state belongs to "whatever is selected right now", not to a
        // snap-in or node persistently - reset it before handing the new
        // selection to the snap-in, so a verb the *previous* selection (in
        // this snap-in or a different one entirely) disabled doesn't leak
        // into this one just because nothing here calls SetVerbState again.
        console.ResetVerbStates();
        console.RunWithSession(this, () =>
        {
            var dataObject = TryGetScopeDataObject(node);

            // This is part of MMC's scope-selection protocol, not an
            // optional query.  Besides choosing standard/custom view, some
            // native snap-ins use it to establish the per-node view state
            // consumed by their following MMCN_SHOW handler.
            EnsureResultView(console, node);

            if (console.HasUnresolvedCustomView)
            {
                // Confirmed via live testing against "Component Services":
                // sending MMCN_SHOW to a snap-in that just asked for a
                // custom result view it isn't actually getting crashes the
                // whole process (an unrecoverable AccessViolationException
                // deep in the snap-in's own native code, not anything this
                // host's own marshaling does) - the snap-in's MMCN_SHOW
                // handler for a custom view apparently needs real in-place-
                // activation window state this host either didn't have a
                // chance to create yet (no MainForm, e.g. the headless
                // --diag-load-snapin path) or failed to create, and it
                // doesn't fail gracefully without it. Failing cleanly here
                // beats a hard crash, and matches this host's existing,
                // honest "no custom OCX/web result views" scope boundary.
                throw new NotSupportedException(
                    $"'{Info.Name}' requested a custom MMC result view for '{node.DisplayName}' " +
                    "that could not be created in this context.");
            }

            // MMCN_SHOW(TRUE) is the notification that tells the snap-in to
            // set up and populate the result pane.  Only after that pane
            // exists may MMCN_SELECT ask it to update selection-dependent
            // verbs/toolbars.  Sending SELECT first happened to work for
            // simpler snap-ins, but leaves more stateful native snap-ins
            // (notably Component Services) without a current result view.
            RawNotify(Component, dataObject, MMC_NOTIFY_TYPE.MMCN_SHOW, new IntPtr(1), node.Handle);
            RawNotify(Component, dataObject, MMC_NOTIFY_TYPE.MMCN_SELECT, MakeSelectArg(scope: true, select: true), IntPtr.Zero);
        });
        node.ResultsLoaded = true;
    }

    private void EnsureResultView(MmcConsole console, ScopeNode node)
    {
        IntPtr viewTypePtr = IntPtr.Zero;
        int viewOptions = 0;
        int hr = Component.GetResultViewType(node.Cookie, out viewTypePtr, out viewOptions);

        string? viewType = null;
        try
        {
            if (viewTypePtr != IntPtr.Zero)
            {
                viewType = Marshal.PtrToStringUni(viewTypePtr);
            }
        }
        finally
        {
            // mmc.idl makes the caller responsible for freeing the string,
            // including when a non-S_OK success code was returned.
            if (viewTypePtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(viewTypePtr);
            }
        }

        SnapInDiagnostics.Trace(
            $"IComponent.GetResultViewType(cookie=0x{node.Cookie:X}) = 0x{hr:X8}, " +
            $"viewType='{viewType ?? "<standard>"}', options=0x{viewOptions:X8}");

        // E_NOTIMPL ("Performance Monitor" returns this for at least its
        // root node) means "I have no custom view to offer here", the same
        // as a null/empty viewType - not a fatal error. Only a genuine
        // failure HRESULT (anything else negative) is fatal.
        const int E_NOTIMPL = unchecked((int)0x80004001);
        if (hr < 0 && hr != E_NOTIMPL)
        {
            throw new COMException(
                $"IComponent.GetResultViewType returned HRESULT 0x{hr:X8}.", hr);
        }

        // Tells MainForm to swap the ListView for a GenericAxHost bound to
        // this CLSID (or back to the ListView, for null/empty) - see
        // MmcConsole.ResultViewTypeChanged/CustomResultViewObject.
        console.NotifyResultViewType(hr == E_NOTIMPL || string.IsNullOrWhiteSpace(viewType) ? null : viewType);
    }

    public void HideResults(MmcConsole console, ScopeNode node)
    {
        if (!_componentCreated)
        {
            return;
        }

        console.RunWithSession(this, () =>
        {
            var dataObject = TryGetScopeDataObject(node);
            RawNotify(Component, dataObject, MMC_NOTIFY_TYPE.MMCN_SELECT, MakeSelectArg(scope: true, select: false), IntPtr.Zero);
            RawNotify(Component, dataObject, MMC_NOTIFY_TYPE.MMCN_SHOW, IntPtr.Zero, node.Handle);
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
        if (!_componentCreated)
        {
            return;
        }

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
            RawNotify(Component, dataObject, MMC_NOTIFY_TYPE.MMCN_SELECT, MakeSelectArg(scope: false, select: selected), IntPtr.Zero);
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
        if (!_componentCreated)
        {
            return null;
        }

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
