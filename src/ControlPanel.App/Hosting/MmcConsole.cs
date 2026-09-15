using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ControlPanel.App.Interop;
using ControlPanel.App.Native;

namespace ControlPanel.App.Hosting;

/// <summary>
/// This is the "Node Manager": the object that plays the role mmc.exe's
/// own console engine plays for a real MMC session. A snap-in never knows
/// whether it is being hosted by mmc.exe or by us - it only ever talks to
/// whatever COM object implements IConsole/IConsoleNameSpace2/IHeaderCtrl2/
/// IResultData/IDisplayHelp, which is this class.
///
/// It owns the mapping between MMC's abstract HSCOPEITEM/HRESULTITEM
/// handles and the WinForms TreeView/ListView actually shown to the user.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[ComDefaultInterface(typeof(IConsole2))]
internal sealed class MmcConsole : IConsole2, IConsoleNameSpace2, IHeaderCtrl2, IResultData, IDisplayHelp, IConsoleVerb, IControlbar, IToolbar, IPropertySheetProvider, IColumnData, IImageList
{
    private readonly TreeView _tree;
    private readonly ListView _list;
    private readonly TreeNode _consoleRoot;
    private readonly Form _ownerForm;

    private readonly Dictionary<IntPtr, ScopeNode> _scopeNodesByHandle = new();
    private readonly Dictionary<TreeNode, ScopeNode> _scopeNodesByUiNode = new();
    private long _nextScopeHandle = 1;

    private readonly List<ResultRow> _resultRows = new();
    private readonly Dictionary<IntPtr, ResultRow> _resultRowsById = new();
    private long _nextResultItemId = 1;

    public ImageListAdapter ScopeImages { get; }
    public ImageListAdapter ResultImages { get; }

    /// <summary>
    /// The snap-in session whose IComponentData/IComponent is currently
    /// making calls into us. Must be set (via <see cref="RunWithSession"/>)
    /// before invoking anything on a snap-in, since the snap-in's call may
    /// synchronously re-enter us (e.g. IConsoleNameSpace2.InsertItem while
    /// handling MMCN_EXPAND).
    /// </summary>
    public SnapInSession? ActiveSession { get; set; }

    /// <summary>
    /// The scope item a Notify(MMCN_EXPAND) call is currently in flight for
    /// (set by SnapInSession.ExpandNode around that call). Several snap-ins
    /// (Print Management among them) pass relativeID=0 to InsertItem to mean
    /// "attach under whatever item is currently being expanded" instead of
    /// repeating that item's own HSCOPEITEM - real mmc.exe apparently
    /// resolves that the same way. IntPtr.Zero here means "no expand is
    /// currently in flight", in which case relativeID=0 falls back to
    /// meaning the true (synthetic) console root, as before.
    /// </summary>
    public IntPtr ActiveExpandHandle { get; set; } = IntPtr.Zero;

    public event Action<string>? StatusTextChanged;

    private int _nextScopeImageBase;
    private int _nextResultImageBase;
    private const int ImageIndexBlockSize = 512;

    /// <summary>
    /// Reserves a fresh, non-overlapping block of indices in each shared
    /// image list for one snap-in session - see SnapInSession.ScopeImageBase
    /// for why this is needed (every session shares one MmcConsole, and
    /// each snap-in numbers its own icons from 0 with no idea another
    /// snap-in shares the list). A fixed block size is pragmatic: generous
    /// enough that no real snap-in is likely to register more distinct
    /// icons than this, and avoids needing to know a snap-in's icon count
    /// up front.
    /// </summary>
    public void AllocateImageBases(out int scopeImageBase, out int resultImageBase)
    {
        scopeImageBase = _nextScopeImageBase;
        resultImageBase = _nextResultImageBase;
        _nextScopeImageBase += ImageIndexBlockSize;
        _nextResultImageBase += ImageIndexBlockSize;
    }

    /// <summary>
    /// Converts a snap-in's own icon index (always 0-based from that
    /// snap-in's point of view) into the real index in the shared
    /// TreeView/ListView ImageList, by adding the owning session's
    /// reserved block start - -1 ("no icon") passes through unchanged.
    /// Only ever applied at the WinForms-control-facing edge
    /// (TreeNode.ImageIndex, ListViewItem.ImageIndex); ScopeNode.ImageIndex/
    /// ResultRow.ImageIndex themselves stay in the snap-in's own raw
    /// numbering, since GetItem hands them straight back to the snap-in
    /// and must not apply this offset.
    /// </summary>
    internal static int OffsetImageIndex(int rawIndex, int indexBase) => rawIndex < 0 ? -1 : rawIndex + indexBase;

    public MmcConsole(TreeView tree, ListView list, TreeNode consoleRoot, ImageList scopeImageList, ImageList resultImageList, Form ownerForm)
    {
        _tree = tree;
        _list = list;
        _consoleRoot = consoleRoot;
        _ownerForm = ownerForm;
        ScopeImages = new ImageListAdapter(scopeImageList, () => ActiveSession?.ScopeImageBase ?? 0);
        ResultImages = new ImageListAdapter(resultImageList, () => ActiveSession?.ResultImageBase ?? 0);
    }

    public T RunWithSession<T>(SnapInSession session, Func<T> action)
    {
        var previous = ActiveSession;
        ActiveSession = session;
        try
        {
            return action();
        }
        finally
        {
            ActiveSession = previous;
        }
    }

    public void RunWithSession(SnapInSession session, Action action)
    {
        RunWithSession<object?>(session, () => { action(); return null; });
    }

    public IReadOnlyDictionary<TreeNode, ScopeNode> NodesByUiNode => _scopeNodesByUiNode;

    public IReadOnlyList<ResultRow> ResultRows => _resultRows;

    /// <summary>
    /// Inserts the static root node owned by a standalone snap-in. MMC's
    /// console creates this node itself; the snap-in only inserts enumerated
    /// children beneath it when it receives MMCN_EXPAND.
    /// </summary>
    public ScopeNode InsertStaticNode(SnapInSession session, string displayName)
    {
        var handle = new IntPtr(_nextScopeHandle++);
        var node = new ScopeNode
        {
            Handle = handle,
            ParentHandle = IntPtr.Zero,
            Cookie = IntPtr.Zero,
            Session = session,
            DisplayName = displayName,
            HasChildren = true,
        };

        var uiNode = new TreeNode(displayName)
        {
            ImageIndex = -1,
            SelectedImageIndex = -1,
        };
        node.UiNode = uiNode;

        // Until the first MMCN_EXPAND completes we must expose an expansion
        // glyph. ExpandNode removes this placeholder before the snap-in adds
        // its real children.
        uiNode.Nodes.Add(new TreeNode("..."));
        _consoleRoot.Nodes.Add(uiNode);
        _scopeNodesByHandle[handle] = node;
        _scopeNodesByUiNode[uiNode] = node;

        SnapInDiagnostics.Trace($"Inserted static node '{displayName}' as HSCOPEITEM 0x{handle:X}");
        return node;
    }

    // ------------------------------------------------------------------
    // IConsole / IConsole2
    // ------------------------------------------------------------------

    public void SetHeader(IntPtr pHeader)
    {
        SnapInDiagnostics.Trace(nameof(SetHeader));
        // Only relevant to custom (OCX/web) result views, which we don't support.
    }

    public void SetToolbar(IntPtr pToolbar)
    {
        SnapInDiagnostics.Trace(nameof(SetToolbar));
    }

    /// <summary>
    /// Fired when the currently-shown node's result view type changes -
    /// null/empty for the standard list view, a CLSID string (as
    /// IComponent::GetResultViewType returns it, e.g.
    /// "{AEB84C83-95DC-11D0-B7FC-B61140119C4A}") for a custom OCX view.
    /// MainForm owns the actual UI swap (hiding the ListView, creating/
    /// destroying a GenericAxHost) since MmcConsole has no Control of its
    /// own; it reports the resulting COM object back via
    /// CustomResultViewObject so QueryResultView can hand it to the snap-in.
    /// </summary>
    public event Action<string?>? ResultViewTypeChanged;

    /// <summary>
    /// Set by MainForm once it has created (or torn down) the
    /// GenericAxHost for the current node's custom result view - this is
    /// exactly what QueryResultView must return to the snap-in, since a
    /// custom view is driven directly through whatever interface this
    /// object implements, not through IResultData/IHeaderCtrl2.
    /// </summary>
    public object? CustomResultViewObject { get; set; }

    /// <summary>
    /// Whether IComponent::GetResultViewType actually reported a custom
    /// view for the node currently being shown - distinct from whether we
    /// managed to create a real object for it (CustomResultViewObject).
    /// Needed to tell apart the two very different reasons
    /// CustomResultViewObject can be null: "no custom view was ever
    /// requested here" (safe to paper over, see below) vs. "one was
    /// requested but this host couldn't actually produce it" (e.g. the
    /// headless --diag-load-snapin path, which has no MainForm to create a
    /// GenericAxHost at all, or GenericAxHost construction itself failing) -
    /// silently handing the snap-in something else in the second case is
    /// how "Component Services" (a genuine custom-view snap-in) crashed
    /// when tested that way: it got this console object standing in for
    /// its real result view and dereferenced it through an interface this
    /// object doesn't implement.
    /// </summary>
    private bool _customViewRequestedForCurrentNode;

    public void NotifyResultViewType(string? viewType)
    {
        _customViewRequestedForCurrentNode = viewType is not null;
        ResultViewTypeChanged?.Invoke(viewType);
    }

    /// <summary>
    /// True right after NotifyResultViewType(nonNull) if nothing produced a
    /// real CustomResultViewObject for it - no MainForm subscribed (the
    /// headless --diag-load-snapin path), or GenericAxHost construction
    /// itself failed. SnapInSession.ShowResults checks this *before* MMCN_SHOW:
    /// sending it anyway is what actually crashed "Component Services" -
    /// the snap-in's own MMCN_SHOW handler for a custom view apparently
    /// needs real in-place-activation window state (a live HWND, message
    /// pump) that plain existing is not enough to provide, and it doesn't
    /// fail gracefully without it.
    /// </summary>
    public bool HasUnresolvedCustomView => _customViewRequestedForCurrentNode && CustomResultViewObject is null;

    /// <summary>
    /// True whenever a custom view was requested for the node currently
    /// being shown, regardless of whether a real CustomResultViewObject
    /// exists for it. SnapInSession.ShowResults uses this (not just
    /// HasUnresolvedCustomView) to decide whether to send MMCN_SHOW:
    /// confirmed via live testing that sending it crashes the process
    /// even when GenericAxHost creation *succeeded* - "Component
    /// Services"' own MMCN_SHOW handler apparently expects a private
    /// interface on the object QueryResultView hands back that a generic
    /// AxHost wrapper doesn't provide, not just "some object or other".
    /// </summary>
    public bool CustomViewRequestedForCurrentNode => _customViewRequestedForCurrentNode;

    public void QueryResultView(out object pUnknown)
    {
        SnapInDiagnostics.Trace(nameof(QueryResultView));

        if (CustomResultViewObject is not null)
        {
            pUnknown = CustomResultViewObject;
            return;
        }

        if (_customViewRequestedForCurrentNode)
        {
            // A real custom view was asked for (GetResultViewType returned
            // a CLSID) but isn't actually available right now - most likely
            // no MainForm exists to host a GenericAxHost (the headless
            // diagnostic), or creating it failed. Handing back "this" here
            // would be actively wrong, not just unhelpful: unlike the
            // fallback below, this snap-in has every reason to believe a
            // real custom view exists and will dereference whatever it
            // gets through that view's actual interface.
            Diagnostics.Log("QueryResultView: a custom result view was requested for this node but is not available (no UI host, or creation failed) - failing.");
            pUnknown = null!;
            throw new NotSupportedException("A custom MMC result view was requested but is not available in this context.");
        }

        // Used to throw here unconditionally (-> E_NOTIMPL/NotImplementedException).
        // Confirmed via live debugging that "Performance Monitor"
        // (wdc.dll!WdcComponent::Notify) crashes reading address
        // 0xFFFFFFFFFFFFFFFF right after calling this for a node that
        // legitimately uses the standard view (GetResultViewType already
        // reported E_NOTIMPL/no custom view for it) - it apparently calls
        // QueryResultView unconditionally regardless of that, and doesn't
        // check this call's own HRESULT before dereferencing whatever
        // pUnknown ends up holding. Returning this object itself - always
        // real, non-null, and already COM-visible - instead of failing
        // means a snap-in that leaves its own pointer uninitialized on
        // failure gets a genuine pointer instead of garbage, even though
        // it isn't the special result-view interface it was hoping for.
        Diagnostics.Log("QueryResultView: no custom result view was requested for the current node - returning this console instead of failing.");
        pUnknown = this;
    }

    public void QueryScopeImageList(out IntPtr ppImageList)
    {
        SnapInDiagnostics.Trace(nameof(QueryScopeImageList));
        ppImageList = Marshal.GetComInterfaceForObject(ScopeImages, typeof(IImageList));
    }

    public void QueryResultImageList(out IntPtr ppImageList)
    {
        SnapInDiagnostics.Trace(nameof(QueryResultImageList));
        ppImageList = Marshal.GetComInterfaceForObject(ResultImages, typeof(IImageList));
    }

    public void UpdateAllViews(object? lpDataObject, IntPtr data, IntPtr hint)
    {
        foreach (var row in _resultRows)
        {
            row.ColumnCache.Clear();
        }
        _list.Invalidate();
    }

    public void MessageBox(string lpszText, string lpszTitle, uint fuStyle, out int piRetval)
    {
        var buttons = (fuStyle & 0xF) switch
        {
            1 => MessageBoxButtons.OKCancel,
            2 => MessageBoxButtons.AbortRetryIgnore,
            3 => MessageBoxButtons.YesNoCancel,
            4 => MessageBoxButtons.YesNo,
            5 => MessageBoxButtons.RetryCancel,
            _ => MessageBoxButtons.OK,
        };

        var result = System.Windows.Forms.MessageBox.Show(_ownerForm, lpszText, lpszTitle, buttons);
        piRetval = result switch
        {
            DialogResult.OK => 1,
            DialogResult.Cancel => 2,
            DialogResult.Abort => 3,
            DialogResult.Retry => 4,
            DialogResult.Ignore => 5,
            DialogResult.Yes => 6,
            DialogResult.No => 7,
            _ => 0,
        };
    }

    public void QueryConsoleVerb(out IntPtr ppConsoleVerb)
    {
        SnapInDiagnostics.Trace(nameof(QueryConsoleVerb));
        ppConsoleVerb = Marshal.GetComInterfaceForObject(this, typeof(IConsoleVerb));
    }

    public void SelectScopeItem(IntPtr hScopeItem)
    {
        SnapInDiagnostics.Trace(nameof(SelectScopeItem));
        if (_scopeNodesByHandle.TryGetValue(hScopeItem, out var node) && node.UiNode is not null)
        {
            _tree.SelectedNode = node.UiNode;
        }
    }

    public void GetMainWindow(out IntPtr phwnd)
    {
        SnapInDiagnostics.Trace(nameof(GetMainWindow));
        phwnd = _ownerForm.Handle;
    }

    public void NewWindow(IntPtr hScopeItem, uint lOptions)
    {
        SnapInDiagnostics.Trace(nameof(NewWindow));
        Diagnostics.Log($"NewWindow: multiple console windows are not supported by this host (requested root handle 0x{hScopeItem:X}).");
        throw new NotImplementedException("This host only supports a single window rooted at Console Root.");
    }

    public void Expand(IntPtr hItem, bool bExpand)
    {
        if (!_scopeNodesByHandle.TryGetValue(hItem, out var node) || node.UiNode is null)
        {
            return;
        }

        if (bExpand)
        {
            node.UiNode.Expand();
        }
        else
        {
            node.UiNode.Collapse();
        }
    }

    [PreserveSig]
    public int IsTaskpadViewPreferred()
    {
        SnapInDiagnostics.Trace(nameof(IsTaskpadViewPreferred));
        return 1; // S_FALSE - classic (list) view only
    }

    public void SetStatusText(string pszStatusText)
    {
        SnapInDiagnostics.Trace(nameof(SetStatusText));
        StatusTextChanged?.Invoke(pszStatusText);
    }

    // ------------------------------------------------------------------
    // IConsoleNameSpace / IConsoleNameSpace2
    // ------------------------------------------------------------------

    public void InsertItem(ref SCOPEDATAITEM item)
    {
        SnapInDiagnostics.Trace(nameof(IConsoleNameSpace2) + "." + nameof(InsertItem));
        var session = ActiveSession;
        if (session is null)
        {
            throw new InvalidOperationException("InsertItem called with no active snap-in session.");
        }

        // The top 4 bits of mask say how relativeID relates to the new item:
        // SDI_PARENT (default, 0): relativeID IS the parent's handle.
        // SDI_PREVIOUS / SDI_NEXT: relativeID is a SIBLING handle instead -
        // the real parent is that sibling's own parent. Treating relativeID
        // as "always the parent" (as an earlier version of this method did)
        // inserts every SDI_PREVIOUS/SDI_NEXT item as a *child* of the
        // sibling it was supposed to be next to.
        uint relKind = item.mask & 0xF0000000;
        IntPtr parentHandle;
        TreeNode parentUiNode;
        int insertAt;

        if (relKind is MmcConsts.SDI_PREVIOUS or MmcConsts.SDI_NEXT)
        {
            if (!_scopeNodesByHandle.TryGetValue(item.relativeID, out var sibling) || sibling.UiNode is null)
            {
                throw new InvalidOperationException("InsertItem: SDI_PREVIOUS/SDI_NEXT relativeID does not refer to a known item.");
            }

            parentHandle = sibling.ParentHandle;
            parentUiNode = sibling.UiNode.Parent ?? _consoleRoot;
            insertAt = relKind == MmcConsts.SDI_PREVIOUS ? sibling.UiNode.Index + 1 : sibling.UiNode.Index;
        }
        else
        {
            parentHandle = item.relativeID != IntPtr.Zero ? item.relativeID : ActiveExpandHandle;
            if (parentHandle == IntPtr.Zero)
            {
                parentUiNode = _consoleRoot;
            }
            else if (_scopeNodesByHandle.TryGetValue(parentHandle, out var parentNode) && parentNode.UiNode is not null)
            {
                parentUiNode = parentNode.UiNode;
            }
            else
            {
                // Silently falling back to Console Root produces a plausible
                // but corrupt tree and loses the evidence needed to diagnose
                // a bad/stale HSCOPEITEM.
                throw new InvalidOperationException(
                    $"InsertItem: parent HSCOPEITEM 0x{parentHandle:X} is not known to this console.");
            }

            insertAt = (item.mask & MmcConsts.SDI_FIRST) != 0 ? 0 : parentUiNode.Nodes.Count;
        }

        var handle = new IntPtr(_nextScopeHandle++);
        var node = new ScopeNode
        {
            Handle = handle,
            ParentHandle = parentHandle,
            Cookie = (item.mask & MmcConsts.SDI_PARAM) != 0 ? item.lParam : IntPtr.Zero,
            Session = session,
            // Per SCOPEDATAITEM, setting SDI_CHILDREN with cChildren=0 is
            // the explicit "leaf" declaration.  If the flag is omitted,
            // MMC must initially assume that children may exist.
            HasChildren = (item.mask & MmcConsts.SDI_CHILDREN) == 0 || item.cChildren > 0,
        };

        if ((item.mask & MmcConsts.SDI_STR) != 0)
        {
            var itemSnapshot = item; // ref parameters cannot be captured by a lambda
            node.DisplayName = ResolveDisplayName(item.displayname, () =>
            {
                var self = itemSnapshot;
                self.mask = MmcConsts.SDI_STR;
                session.ComponentData.GetDisplayInfo(ref self);
                return self.displayname;
            });
        }

        if ((item.mask & MmcConsts.SDI_IMAGE) != 0)
        {
            node.ImageIndex = item.nImage;
        }
        if ((item.mask & MmcConsts.SDI_OPENIMAGE) != 0)
        {
            node.OpenImageIndex = item.nOpenImage;
        }
        var uiNode = new TreeNode(node.DisplayName)
        {
            ImageIndex = OffsetImageIndex(node.ImageIndex, session.ScopeImageBase),
            SelectedImageIndex = OffsetImageIndex(
                node.OpenImageIndex >= 0 ? node.OpenImageIndex : node.ImageIndex, session.ScopeImageBase),
        };
        node.UiNode = uiNode;

        if (node.HasChildren)
        {
            // Placeholder child so the +/- expand glyph shows; replaced on first real expand.
            uiNode.Nodes.Add(new TreeNode("..."));
        }

        parentUiNode.Nodes.Insert(Math.Min(insertAt, parentUiNode.Nodes.Count), uiNode);

        _scopeNodesByHandle[handle] = node;
        _scopeNodesByUiNode[uiNode] = node;

        item.ID = handle;

        SnapInDiagnostics.Trace(
            $"IConsoleNameSpace2.InsertItem name='{node.DisplayName}', mask=0x{item.mask:X8}, " +
            $"relativeID=0x{item.relativeID:X}, relation=0x{relKind:X8}, " +
            $"parent=0x{parentHandle:X}, assigned=0x{handle:X}, hasChildren={node.HasChildren}");
    }

    private static string ResolveDisplayName(IntPtr displayNamePtr, Func<IntPtr> resolveViaCallback)
    {
        if (displayNamePtr == MmcConsts.MMC_CALLBACK)
        {
            var resolved = resolveViaCallback();
            return BorrowedStringToManaged(resolved) ?? string.Empty;
        }

        return Marshal.PtrToStringUni(displayNamePtr) ?? string.Empty;
    }

    /// <summary>
    /// Copies a string returned through IComponentData::GetDisplayInfo or
    /// IComponent::GetDisplayInfo.  The pointer is borrowed: mmc.idl keeps
    /// ownership with the snap-in, which may retain that allocation until
    /// the next GetDisplayInfo call for the item, item deletion, or
    /// IComponent[Data]::Destroy.  Freeing it here corrupts the snap-in's
    /// allocator and eventually terminates the process in ntdll.
    /// </summary>
    private static string? BorrowedStringToManaged(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        return Marshal.PtrToStringUni(ptr);
    }

    // NOTE: IConsoleNameSpace.DeleteItem(IntPtr,int) and IResultData.DeleteItem(IntPtr,int)
    // have identical C# signatures but different meanings, so both need explicit
    // interface implementations here to avoid one implicit method silently
    // satisfying (and being mistaken for) both interface slots.
    void IConsoleNameSpace.DeleteItem(IntPtr hItem, int fDeleteThis) => DeleteScopeItem(hItem, fDeleteThis);
    void IConsoleNameSpace2.DeleteItem(IntPtr hItem, int fDeleteThis) => DeleteScopeItem(hItem, fDeleteThis);

    private void DeleteScopeItem(IntPtr hItem, int fDeleteThis)
    {
        if (!_scopeNodesByHandle.TryGetValue(hItem, out var node))
        {
            return;
        }

        if (node.UiNode is not null)
        {
            // Snapshot first: recursive deletes below remove children from
            // node.UiNode.Nodes, which would otherwise invalidate this enumeration.
            foreach (TreeNode child in node.UiNode.Nodes.Cast<TreeNode>().ToList())
            {
                if (_scopeNodesByUiNode.TryGetValue(child, out var childNode))
                {
                    DeleteScopeItem(childNode.Handle, 1);
                }
            }

            if (fDeleteThis != 0)
            {
                _scopeNodesByUiNode.Remove(node.UiNode);
                node.UiNode.Remove();
            }
        }

        if (fDeleteThis != 0)
        {
            _scopeNodesByHandle.Remove(hItem);
        }
    }

    public void SetItem(ref SCOPEDATAITEM item)
    {
        if (!_scopeNodesByHandle.TryGetValue(item.ID, out var node))
        {
            return;
        }

        if ((item.mask & MmcConsts.SDI_STR) != 0 && ActiveSession is { } session)
        {
            var itemSnapshot = item; // ref parameters cannot be captured by a lambda
            node.DisplayName = ResolveDisplayName(item.displayname, () =>
            {
                var self = itemSnapshot;
                self.mask = MmcConsts.SDI_STR;
                session.ComponentData.GetDisplayInfo(ref self);
                return self.displayname;
            });
            if (node.UiNode is not null)
            {
                node.UiNode.Text = node.DisplayName;
            }
        }

        if ((item.mask & MmcConsts.SDI_IMAGE) != 0 && node.UiNode is not null)
        {
            node.ImageIndex = item.nImage;
            node.UiNode.ImageIndex = OffsetImageIndex(item.nImage, node.Session.ScopeImageBase);
        }

        if ((item.mask & MmcConsts.SDI_OPENIMAGE) != 0 && node.UiNode is not null)
        {
            node.OpenImageIndex = item.nOpenImage;
            node.UiNode.SelectedImageIndex = OffsetImageIndex(item.nOpenImage, node.Session.ScopeImageBase);
        }

        if ((item.mask & MmcConsts.SDI_CHILDREN) != 0)
        {
            node.HasChildren = item.cChildren > 0;

            if (node.UiNode is { } uiNode && !node.ChildrenLoaded)
            {
                bool hasPlaceholder = uiNode.Nodes.Count == 1 &&
                    uiNode.Nodes[0].Text == "..." &&
                    !_scopeNodesByUiNode.ContainsKey(uiNode.Nodes[0]);

                if (node.HasChildren && uiNode.Nodes.Count == 0)
                {
                    uiNode.Nodes.Add(new TreeNode("..."));
                }
                else if (!node.HasChildren && hasPlaceholder)
                {
                    uiNode.Nodes.Clear();
                }
            }
        }
    }

    public void GetItem(ref SCOPEDATAITEM item)
    {
        if (!_scopeNodesByHandle.TryGetValue(item.ID, out var node))
        {
            return;
        }

        if ((item.mask & MmcConsts.SDI_PARAM) != 0)
        {
            item.lParam = node.Cookie;
        }
        if ((item.mask & MmcConsts.SDI_IMAGE) != 0)
        {
            item.nImage = node.ImageIndex;
        }
        if ((item.mask & MmcConsts.SDI_OPENIMAGE) != 0)
        {
            item.nOpenImage = node.OpenImageIndex;
        }
        if ((item.mask & MmcConsts.SDI_CHILDREN) != 0)
        {
            item.cChildren = node.HasChildren ? 1 : 0;
        }
        if ((item.mask & MmcConsts.SDI_STR) != 0)
        {
            item.displayname = Marshal.StringToCoTaskMemUni(node.DisplayName);
        }
    }

    public void GetChildItem(IntPtr item, out IntPtr pItemChild, out IntPtr pCookie)
    {
        var parentUi = item == IntPtr.Zero ? _consoleRoot : (_scopeNodesByHandle.TryGetValue(item, out var p) ? p.UiNode : null);
        if (parentUi is { Nodes.Count: > 0 } && _scopeNodesByUiNode.TryGetValue(parentUi.Nodes[0], out var childNode))
        {
            pItemChild = childNode.Handle;
            pCookie = childNode.Cookie;
        }
        else
        {
            pItemChild = IntPtr.Zero;
            pCookie = IntPtr.Zero;
        }
    }

    public void GetNextItem(IntPtr item, out IntPtr pItemNext, out IntPtr pCookie)
    {
        if (_scopeNodesByHandle.TryGetValue(item, out var node) && node.UiNode is { NextNode: { } next } && _scopeNodesByUiNode.TryGetValue(next, out var nextNode))
        {
            pItemNext = nextNode.Handle;
            pCookie = nextNode.Cookie;
        }
        else
        {
            pItemNext = IntPtr.Zero;
            pCookie = IntPtr.Zero;
        }
    }

    public void GetParentItem(IntPtr item, out IntPtr pItemParent, out IntPtr pCookie)
    {
        if (_scopeNodesByHandle.TryGetValue(item, out var node) && node.UiNode?.Parent is { } parentUi && _scopeNodesByUiNode.TryGetValue(parentUi, out var parentNode))
        {
            pItemParent = parentNode.Handle;
            pCookie = parentNode.Cookie;
        }
        else
        {
            pItemParent = IntPtr.Zero;
            pCookie = IntPtr.Zero;
        }
    }

    public void Expand(IntPtr hItem) => Expand(hItem, true);

    public void AddExtension(IntPtr hItem, ref Guid lpClsid)
    {
        // Dynamic snap-in extensions are out of scope for this host.
    }

    // ------------------------------------------------------------------
    // IHeaderCtrl / IHeaderCtrl2 (result pane columns)
    // ------------------------------------------------------------------

    public void InsertColumn(int nCol, string title, int nFormat, int nWidth)
    {
        SnapInDiagnostics.Trace($"{nameof(InsertColumn)}({nCol}, \"{title}\")");
        var alignment = nFormat switch
        {
            1 => HorizontalAlignment.Right,
            2 => HorizontalAlignment.Center,
            _ => HorizontalAlignment.Left,
        };
        int index = Math.Min(Math.Max(nCol, 0), _list.Columns.Count);
        _list.Columns.Insert(index, title, nWidth <= 0 ? 100 : nWidth, alignment);
    }

    public void DeleteColumn(int nCol)
    {
        if (nCol >= 0 && nCol < _list.Columns.Count)
        {
            _list.Columns.RemoveAt(nCol);
        }
    }

    public void SetColumnText(int nCol, string title)
    {
        if (nCol >= 0 && nCol < _list.Columns.Count)
        {
            _list.Columns[nCol].Text = title;
        }
    }

    public void GetColumnText(int nCol, out string text) =>
        text = (nCol >= 0 && nCol < _list.Columns.Count) ? _list.Columns[nCol].Text : string.Empty;

    public void SetColumnWidth(int nCol, int nWidth)
    {
        if (nCol >= 0 && nCol < _list.Columns.Count)
        {
            _list.Columns[nCol].Width = nWidth;
        }
    }

    public void GetColumnWidth(int nCol, out int pWidth) =>
        pWidth = (nCol >= 0 && nCol < _list.Columns.Count) ? _list.Columns[nCol].Width : 0;

    public void SetChangeTimeOut(uint uTimeout)
    {
    }

    public void SetColumnFilter(uint nColumn, uint dwType, IntPtr pFilterData)
    {
        // Column filtering UI is not implemented in this host.
    }

    public void GetColumnFilter(uint nColumn, ref uint pdwType, IntPtr pFilterData)
    {
        pdwType = 0x8000; // HDFT_HASNOVALUE
    }

    // ------------------------------------------------------------------
    // IResultData (result pane rows)
    // ------------------------------------------------------------------

    public void InsertItem(ref RESULTDATAITEM item)
    {
        if (ActiveSession is null)
        {
            throw new InvalidOperationException("InsertItem called with no active snap-in session.");
        }

        var id = new IntPtr(_nextResultItemId++);
        var row = new ResultRow
        {
            ItemId = id,
            Cookie = (item.mask & MmcConsts.RDI_PARAM) != 0 ? item.lParam : IntPtr.Zero,
            Session = ActiveSession,
        };

        if ((item.mask & MmcConsts.RDI_STR) != 0 && item.str != MmcConsts.MMC_CALLBACK && item.str != IntPtr.Zero)
        {
            row.ColumnCache[Math.Max(item.nCol, 0)] = Marshal.PtrToStringUni(item.str) ?? string.Empty;
        }

        if ((item.mask & MmcConsts.RDI_IMAGE) != 0)
        {
            row.ImageIndex = item.nImage;
        }

        int index = (item.mask & MmcConsts.RDI_INDEX) != 0 && item.nIndex >= 0
            ? Math.Min(item.nIndex, _resultRows.Count)
            : _resultRows.Count;

        _resultRows.Insert(index, row);
        _resultRowsById[id] = row;
        _list.VirtualListSize = _resultRows.Count;

        item.itemID = id;
    }

    public void DeleteItem(IntPtr itemID, int nCol)
    {
        if (_resultRowsById.Remove(itemID, out var row))
        {
            _resultRows.Remove(row);
            _list.VirtualListSize = _resultRows.Count;
        }
    }

    public void FindItemByLParam(IntPtr lParam, out IntPtr pItemID)
    {
        var match = _resultRows.FirstOrDefault(r => r.Cookie == lParam);
        pItemID = match?.ItemId ?? IntPtr.Zero;
    }

    public void DeleteAllRsltItems()
    {
        _resultRows.Clear();
        _resultRowsById.Clear();
        _list.VirtualListSize = 0;
    }

    public void SetItem(ref RESULTDATAITEM item)
    {
        if (!_resultRowsById.TryGetValue(item.itemID, out var row))
        {
            return;
        }

        if ((item.mask & MmcConsts.RDI_STR) != 0 && item.str != IntPtr.Zero && item.str != MmcConsts.MMC_CALLBACK)
        {
            row.ColumnCache[Math.Max(item.nCol, 0)] = Marshal.PtrToStringUni(item.str) ?? string.Empty;
        }
        if ((item.mask & MmcConsts.RDI_IMAGE) != 0)
        {
            row.ImageIndex = item.nImage;
        }
    }

    public void GetItem(ref RESULTDATAITEM item)
    {
        if (!_resultRowsById.TryGetValue(item.itemID, out var row))
        {
            return;
        }

        if ((item.mask & MmcConsts.RDI_PARAM) != 0)
        {
            item.lParam = row.Cookie;
        }
        if ((item.mask & MmcConsts.RDI_IMAGE) != 0)
        {
            item.nImage = row.ImageIndex;
        }
        if ((item.mask & MmcConsts.RDI_INDEX) != 0)
        {
            item.nIndex = _resultRows.IndexOf(row);
        }
    }

    public void GetNextItem(ref RESULTDATAITEM item)
    {
        int startIndex = item.nIndex < 0 ? -1 : item.nIndex;
        int next = startIndex + 1;
        if (next >= 0 && next < _resultRows.Count)
        {
            var row = _resultRows[next];
            item.itemID = row.ItemId;
            item.nIndex = next;
            item.lParam = row.Cookie;
        }
        else
        {
            item.itemID = IntPtr.Zero;
        }
    }

    public void ModifyItemState(int nIndex, IntPtr itemID, uint uAdd, uint uRemove)
    {
        // Per-item list-view state (selection/checked/etc.) is not tracked in this host.
    }

    public void ModifyViewStyle(MMC_RESULT_VIEW_STYLE add, MMC_RESULT_VIEW_STYLE remove)
    {
        if ((add & MMC_RESULT_VIEW_STYLE.MMC_SINGLESEL) != 0)
        {
            _list.MultiSelect = false;
        }
        if ((remove & MMC_RESULT_VIEW_STYLE.MMC_SINGLESEL) != 0)
        {
            _list.MultiSelect = true;
        }
    }

    public void SetViewMode(int lViewMode)
    {
    }

    public void GetViewMode(out int lViewMode) => lViewMode = 1; // LVS_REPORT

    public void UpdateItem(IntPtr itemID)
    {
        if (_resultRowsById.TryGetValue(itemID, out var row))
        {
            row.ColumnCache.Clear();
        }
        _list.Invalidate();
    }

    public void Sort(int nColumn, uint dwSortOptions, IntPtr lUserParam)
    {
        // Sorting delegated to the snap-in via IResultDataCompare is not implemented;
        // the list keeps insertion order.
    }

    public void SetDescBarText(string descText)
    {
    }

    public void SetItemCount(int nItemCount, uint dwOptions)
    {
    }

    // ------------------------------------------------------------------
    // IDisplayHelp
    // ------------------------------------------------------------------

    public void ShowTopic(string pszHelpTopic)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "hh.exe",
                Arguments = "\"" + pszHelpTopic + "\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            // Help viewer not available - nothing sensible to fall back to.
        }
    }

    // ------------------------------------------------------------------
    // IConsoleVerb
    //
    // Many snap-ins call QueryConsoleVerb during their MMCN_SELECT handler
    // to enable/disable standard verbs (Rename, Delete, Refresh, ...) for
    // whatever is now selected, and MainForm reflects that state onto the
    // Properties/Refresh menu items and toolbar buttons. This state is
    // scoped to "whatever is currently selected", not to a snap-in or
    // node persistently - real MMC resets it before each new selection so
    // a snap-in only needs to call SetVerbState for what it wants to
    // change from the default. ResetVerbStates() must be called before
    // every MMCN_SELECT(select=true); without it, a verb one item disabled
    // stays disabled for every later selection that doesn't explicitly
    // re-enable it, including selections in a completely different
    // snap-in.
    // ------------------------------------------------------------------

    private readonly Dictionary<(MMC_CONSOLE_VERB, MMC_BUTTON_STATE), bool> _verbState = new();
    private MMC_CONSOLE_VERB _defaultVerb = MMC_CONSOLE_VERB.MMC_VERB_PROPERTIES;

    public void GetVerbState(MMC_CONSOLE_VERB eCmdID, MMC_BUTTON_STATE nState, out bool pState) =>
        pState = _verbState.TryGetValue((eCmdID, nState), out var value) ? value : nState == MMC_BUTTON_STATE.ENABLED;

    public void SetVerbState(MMC_CONSOLE_VERB eCmdID, MMC_BUTTON_STATE nState, bool bState) =>
        _verbState[(eCmdID, nState)] = bState;

    public void SetDefaultVerb(MMC_CONSOLE_VERB eCmdID) => _defaultVerb = eCmdID;

    public void GetDefaultVerb(out MMC_CONSOLE_VERB peCmdID) => peCmdID = _defaultVerb;

    public void ResetVerbStates()
    {
        _verbState.Clear();
        _defaultVerb = MMC_CONSOLE_VERB.MMC_VERB_PROPERTIES;
    }

    // ------------------------------------------------------------------
    // IControlbar / IToolbar
    //
    // Obtained by a snap-in via a direct QueryInterface on the IConsole
    // pointer (no dedicated Query method exists for this on IConsole
    // itself), for snap-ins that add their own toolbar buttons. Not
    // wired to any real UI in this host - these are no-op stubs purely so
    // that snap-ins expecting *some* controlbar to exist don't get
    // E_NOINTERFACE and potentially abort their own Initialize over it.
    // ------------------------------------------------------------------

    public void Create(MMC_CONTROL_TYPE nType, IntPtr pExtendControlbar, out IntPtr ppUnknown)
    {
        SnapInDiagnostics.Trace($"{nameof(IControlbar)}.{nameof(Create)}({nType})");
        ppUnknown = nType == MMC_CONTROL_TYPE.TOOLBAR
            ? Marshal.GetComInterfaceForObject(this, typeof(IToolbar))
            : IntPtr.Zero;
    }

    public void Attach(MMC_CONTROL_TYPE nType, IntPtr lpUnknown)
    {
    }

    public void Detach(IntPtr lpUnknown)
    {
    }

    public void AddBitmap(int nImages, IntPtr hbmp, int cxSize, int cySize, int crMask)
    {
    }

    public void AddButtons(int nButtons, IntPtr lpButtons)
    {
    }

    public void InsertButton(int nIndex, IntPtr lpButton)
    {
    }

    public void DeleteButton(int nIndex)
    {
    }

    public void GetButtonState(int idCommand, int nState, out bool pState)
    {
        pState = true;
    }

    public void SetButtonState(int idCommand, int nState, bool bState)
    {
    }

    // ------------------------------------------------------------------
    // IPropertySheetProvider
    //
    // Obtained by a snap-in via a direct QueryInterface on the IConsole
    // pointer (same story as IControlbar above) - "Services" and "Component
    // Services" query for this during IComponent.Initialize and fail their
    // own Initialize with E_NOINTERFACE when it's missing entirely, since
    // this host didn't implement it at all before. A snap-in can also use
    // this on its own initiative (independent of the verb-triggered
    // Properties path this host otherwise drives directly via
    // IExtendPropertySheet.CreatePropertyPages) to pop up a sheet.
    //
    // Real modal property sheet UI - hosting the HPROPSHEETPAGE handles a
    // snap-in adds through IPropertySheetCallback.AddPage via the actual
    // Win32 PropertySheet() API - is not implemented yet; these are no-op
    // stubs purely so QueryInterface for this interface succeeds and
    // doesn't derail a snap-in's own Initialize.
    // ------------------------------------------------------------------

    public void CreatePropertySheet(string title, bool type, IntPtr cookie, object? pDataObject, uint dwOptions)
    {
        SnapInDiagnostics.Trace($"{nameof(IPropertySheetProvider)}.{nameof(CreatePropertySheet)}(\"{title}\")");
    }

    public int FindPropertySheet(IntPtr hItem, object? lpComponent, object? lpDataObject)
    {
        SnapInDiagnostics.Trace($"{nameof(IPropertySheetProvider)}.{nameof(FindPropertySheet)}");
        return unchecked((int)0x80004005); // E_FAIL - no sheet is ever already open, since none are ever created yet.
    }

    public void AddPrimaryPages(object? lpUnknown, bool bCreateHandle, IntPtr hNotifyWindow, bool bScopePane)
    {
        SnapInDiagnostics.Trace($"{nameof(IPropertySheetProvider)}.{nameof(AddPrimaryPages)}");
    }

    public void AddExtensionPages()
    {
        SnapInDiagnostics.Trace($"{nameof(IPropertySheetProvider)}.{nameof(AddExtensionPages)}");
    }

    public void Show(IntPtr window, int page)
    {
        SnapInDiagnostics.Trace($"{nameof(IPropertySheetProvider)}.{nameof(Show)}");
    }

    // ------------------------------------------------------------------
    // IImageList
    //
    // Obtained by a snap-in via a direct QueryInterface on the IConsole
    // pointer itself (confirmed via live debugging into filemgmt.dll's
    // CComponent::Initialize - "Services" and "Shared Folders", both
    // implemented by that DLL, QueryInterface the console object directly
    // for IID_IImageList, separately from the QueryScopeImageList/
    // QueryResultImageList methods on IConsole, and failed their own
    // Initialize with E_NOINTERFACE when this host didn't implement it -
    // real mmc.exe's console object answers this directly too). Delegates
    // to the scope image list, matching the convention several other
    // snap-ins already rely on QueryScopeImageList/QueryResultImageList for.
    // ------------------------------------------------------------------

    public void ImageListSetIcon(IntPtr pIcon, int nLoc) => ScopeImages.ImageListSetIcon(pIcon, nLoc);

    public void ImageListSetStrip(IntPtr pBMapSm, IntPtr pBMapLg, int nStartLoc, int cMask) =>
        ScopeImages.ImageListSetStrip(pBMapSm, pBMapLg, nStartLoc, cMask);

    // ------------------------------------------------------------------
    // IColumnData
    //
    // Obtained by a snap-in via a direct QueryInterface on the IConsole
    // pointer (same story as IControlbar/IPropertySheetProvider above) -
    // persists per-column width/order/sort customizations across sessions.
    // This host never saves any, so the Get* methods always report "no
    // saved config" (a normal, expected outcome, not an error).
    // ------------------------------------------------------------------

    public void SetColumnConfigData(IntPtr pColID, IntPtr pColSetData)
    {
        SnapInDiagnostics.Trace($"{nameof(IColumnData)}.{nameof(SetColumnConfigData)}");
    }

    public int GetColumnConfigData(IntPtr pColID, out IntPtr ppColSetData)
    {
        SnapInDiagnostics.Trace($"{nameof(IColumnData)}.{nameof(GetColumnConfigData)}");
        ppColSetData = IntPtr.Zero;
        return unchecked((int)0x80070490); // HRESULT_FROM_WIN32(ERROR_NOT_FOUND)
    }

    public void SetColumnSortData(IntPtr pColID, IntPtr pColSortData)
    {
        SnapInDiagnostics.Trace($"{nameof(IColumnData)}.{nameof(SetColumnSortData)}");
    }

    public int GetColumnSortData(IntPtr pColID, out IntPtr ppColSortData)
    {
        SnapInDiagnostics.Trace($"{nameof(IColumnData)}.{nameof(GetColumnSortData)}");
        ppColSortData = IntPtr.Zero;
        return unchecked((int)0x80070490); // HRESULT_FROM_WIN32(ERROR_NOT_FOUND)
    }

    // ------------------------------------------------------------------
    // Helpers used by SnapInSession / MainForm
    // ------------------------------------------------------------------

    public string GetResultColumnText(ResultRow row, int column)
    {
        if (row.ColumnCache.TryGetValue(column, out var cached))
        {
            return cached;
        }

        // RDI_PARAM/lParam must be resupplied here even though itemID alone
        // identifies the row to *this host* - confirmed via live debugging
        // that "Local Users and Groups" (localsec.dll) looks its own
        // per-item data up via lParam (ComponentData::GetInstanceFromCookie),
        // not itemID. Omitting it left lParam at its default zero, which
        // resolved to "the root item" for every single row - every row
        // showed the snap-in's own static-node description instead of its
        // own text.
        var item = new RESULTDATAITEM
        {
            mask = MmcConsts.RDI_STR | MmcConsts.RDI_PARAM,
            itemID = row.ItemId,
            nCol = column,
            lParam = row.Cookie,
        };

        RunWithSession(row.Session, () => row.Session.GetResultDisplayInfo(ref item));

        var text = BorrowedStringToManaged(item.str) ?? string.Empty;
        row.ColumnCache[column] = text;
        return text;
    }
}
