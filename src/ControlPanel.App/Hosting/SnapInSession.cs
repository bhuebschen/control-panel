using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ControlPanel.App.Interop;

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
