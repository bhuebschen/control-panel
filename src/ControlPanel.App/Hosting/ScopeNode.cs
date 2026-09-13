using System.Windows.Forms;

namespace ControlPanel.App.Hosting;

/// <summary>
/// Our console-side record for one scope-pane (tree) item. The HSCOPEITEM
/// handle (<see cref="Handle"/>) is minted by us (the console) when the
/// snap-in calls IConsoleNameSpace2.InsertItem - the snap-in only ever
/// deals with the handle we hand back, while <see cref="Cookie"/> is the
/// snap-in's own private identifier (SCOPEDATAITEM.lParam) for the node.
/// </summary>
internal sealed class ScopeNode
{
    public required IntPtr Handle { get; init; }
    public required IntPtr ParentHandle { get; init; }
    public required IntPtr Cookie { get; init; }
    public required SnapInSession Session { get; init; }

    public string DisplayName { get; set; } = string.Empty;
    public int ImageIndex { get; set; } = -1;
    public int OpenImageIndex { get; set; } = -1;
    public bool HasChildren { get; set; }
    public bool ChildrenLoaded { get; set; }
    public bool ResultsLoaded { get; set; }

    public TreeNode? UiNode { get; set; }
}
