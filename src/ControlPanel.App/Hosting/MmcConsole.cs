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
internal sealed class MmcConsole : IConsole2, IConsoleNameSpace2, IHeaderCtrl2, IResultData, IDisplayHelp, IConsoleVerb
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

    public event Action<string>? StatusTextChanged;

    public MmcConsole(TreeView tree, ListView list, TreeNode consoleRoot, ImageList scopeImageList, ImageList resultImageList, Form ownerForm)
    {
        _tree = tree;
        _list = list;
        _consoleRoot = consoleRoot;
        _ownerForm = ownerForm;
        ScopeImages = new ImageListAdapter(scopeImageList);
        ResultImages = new ImageListAdapter(resultImageList);
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

    // ------------------------------------------------------------------
    // IConsole / IConsole2
    // ------------------------------------------------------------------

    public void SetHeader(IHeaderCtrl pHeader)
    {
        // Only relevant to custom (OCX/web) result views, which we don't support.
    }

    public void SetToolbar(IntPtr pToolbar)
    {
    }

    public void QueryResultView(out object pUnknown)
    {
        Diagnostics.Log("QueryResultView: custom OCX/web result views are not supported by this host.");
        pUnknown = null!;
        throw new NotImplementedException("This host only supports the default list/report result view, not a custom OCX or web view.");
    }

    public void QueryScopeImageList(out IImageList ppImageList) => ppImageList = ScopeImages;

    public void QueryResultImageList(out IImageList ppImageList) => ppImageList = ResultImages;

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

    public void QueryConsoleVerb(out IConsoleVerb ppConsoleVerb) => ppConsoleVerb = this;

    public void SelectScopeItem(IntPtr hScopeItem)
    {
        if (_scopeNodesByHandle.TryGetValue(hScopeItem, out var node) && node.UiNode is not null)
        {
            _tree.SelectedNode = node.UiNode;
        }
    }

    public void GetMainWindow(out IntPtr phwnd) => phwnd = _ownerForm.Handle;

    public void NewWindow(IntPtr hScopeItem, uint lOptions)
    {
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
    public int IsTaskpadViewPreferred() => 1; // S_FALSE - classic (list) view only

    public void SetStatusText(string pszStatusText) => StatusTextChanged?.Invoke(pszStatusText);

    // ------------------------------------------------------------------
    // IConsoleNameSpace / IConsoleNameSpace2
    // ------------------------------------------------------------------

    public void InsertItem(ref SCOPEDATAITEM item)
    {
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
            parentHandle = item.relativeID;
            parentUiNode = _consoleRoot;
            if (parentHandle != IntPtr.Zero && _scopeNodesByHandle.TryGetValue(parentHandle, out var parentNode) && parentNode.UiNode is not null)
            {
                parentUiNode = parentNode.UiNode;
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
        if ((item.mask & MmcConsts.SDI_CHILDREN) != 0)
        {
            node.HasChildren = item.cChildren > 0;
        }

        var uiNode = new TreeNode(node.DisplayName)
        {
            ImageIndex = node.ImageIndex,
            SelectedImageIndex = node.OpenImageIndex >= 0 ? node.OpenImageIndex : node.ImageIndex,
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
    }

    private static string ResolveDisplayName(IntPtr displayNamePtr, Func<IntPtr> resolveViaCallback)
    {
        if (displayNamePtr == MmcConsts.MMC_CALLBACK)
        {
            var resolved = resolveViaCallback();
            return CoTaskMemToString(resolved) ?? string.Empty;
        }

        return Marshal.PtrToStringUni(displayNamePtr) ?? string.Empty;
    }

    private static string? CoTaskMemToString(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(ptr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
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
            node.UiNode.ImageIndex = item.nImage;
        }

        if ((item.mask & MmcConsts.SDI_OPENIMAGE) != 0 && node.UiNode is not null)
        {
            node.OpenImageIndex = item.nOpenImage;
            node.UiNode.SelectedImageIndex = item.nOpenImage;
        }

        if ((item.mask & MmcConsts.SDI_CHILDREN) != 0)
        {
            node.HasChildren = item.cChildren > 0;
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
    // Many snap-ins call QueryConsoleVerb defensively during Initialize to
    // enable/disable standard verbs (Rename, Delete, Refresh, ...) for the
    // node type they're showing. We don't render a verb-driven toolbar/menu
    // ourselves, but we still track the requested state so a snap-in gets
    // real (if inert) answers back instead of every call failing with
    // E_NOTIMPL, which is what happened before this was added and which
    // some snap-ins may not handle as gracefully as the HRESULT contract
    // technically allows.
    // ------------------------------------------------------------------

    private readonly Dictionary<(MMC_CONSOLE_VERB, MMC_BUTTON_STATE), bool> _verbState = new();
    private MMC_CONSOLE_VERB _defaultVerb = MMC_CONSOLE_VERB.MMC_VERB_PROPERTIES;

    public void GetVerbState(MMC_CONSOLE_VERB eCmdID, MMC_BUTTON_STATE nState, out bool pState) =>
        pState = _verbState.TryGetValue((eCmdID, nState), out var value) ? value : nState == MMC_BUTTON_STATE.ENABLED;

    public void SetVerbState(MMC_CONSOLE_VERB eCmdID, MMC_BUTTON_STATE nState, bool bState) =>
        _verbState[(eCmdID, nState)] = bState;

    public void SetDefaultVerb(MMC_CONSOLE_VERB eCmdID) => _defaultVerb = eCmdID;

    public void GetDefaultVerb(out MMC_CONSOLE_VERB peCmdID) => peCmdID = _defaultVerb;

    // ------------------------------------------------------------------
    // Helpers used by SnapInSession / MainForm
    // ------------------------------------------------------------------

    public string GetResultColumnText(ResultRow row, int column)
    {
        if (row.ColumnCache.TryGetValue(column, out var cached))
        {
            return cached;
        }

        var item = new RESULTDATAITEM
        {
            mask = MmcConsts.RDI_STR,
            itemID = row.ItemId,
            nCol = column,
        };

        RunWithSession(row.Session, () => row.Session.Component.GetDisplayInfo(ref item));

        var text = CoTaskMemToString(item.str) ?? string.Empty;
        row.ColumnCache[column] = text;
        return text;
    }
}
