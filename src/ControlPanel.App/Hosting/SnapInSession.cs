using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
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
            componentData.CreateComponent(out var component);
            session.Component = component;
            component.Initialize(console);

            // Ask the primary snap-in to insert its static root node(s)
            // (parent handle 0 == our synthetic "Console Root").
            componentData.Notify(null, MMC_NOTIFY_TYPE.MMCN_EXPAND, new IntPtr(1), IntPtr.Zero);

            SendAddImages(console, session);
        });

        return session;
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
            session.Component.Notify(null, MMC_NOTIFY_TYPE.MMCN_ADD_IMAGES, imageListPtr, IntPtr.Zero);
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
