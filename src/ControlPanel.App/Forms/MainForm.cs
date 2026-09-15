using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ControlPanel.App.Hosting;
using ControlPanel.App.Interop;
using ControlPanel.App.Native;

namespace ControlPanel.App.Forms;

internal sealed class MainForm : Form
{
    private readonly TreeView _tree;
    private readonly ListView _list;
    private readonly ImageList _scopeImages;
    private readonly ImageList _resultImages;
    private readonly TreeNode _consoleRootNode;
    private readonly StatusStrip _statusStrip;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly ContextMenuStrip _treeContextMenu;
    private readonly ContextMenuStrip _listContextMenu;
    private readonly ToolStripMenuItem _removeSnapInMenuItem;
    private readonly ToolStripMenuItem _propertiesMenuItem;
    private readonly ToolStripMenuItem _refreshMenuItem;
    private readonly ToolStripButton _propertiesToolButton;
    private readonly ToolStripButton _refreshToolButton;

    private readonly MmcConsole _console;
    private readonly List<SnapInSession> _sessions = new();
    private ScopeNode? _currentNode;

    private enum ActivePane { Scope, Result }

    /// <summary>
    /// Which pane the user was last actually in, for the one ambiguous
    /// action - the shared Properties menu item/toolbar button - that isn't
    /// tied to a specific right-click target. Set via Control.Enter rather
    /// than checked via Control.Focused at click time: Enter fires once
    /// when real focus moves into a pane and isn't disturbed by a menu or
    /// toolbar transiently taking input focus while open (so choosing
    /// Properties from the menu bar via the keyboard still targets whatever
    /// pane was actually active before opening the menu).
    /// </summary>
    private ActivePane _activePane = ActivePane.Scope;

    public MainForm()
    {
        Text = "MMC Snap-in Host";
        Width = 1000;
        Height = 700;
        StartPosition = FormStartPosition.CenterScreen;

        _scopeImages = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
        _resultImages = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };

        _tree = new TreeView
        {
            Dock = DockStyle.Fill,
            ImageList = _scopeImages,
            HideSelection = false,
        };
        _consoleRootNode = new TreeNode("Console Root") { ImageIndex = -1 };
        _tree.Nodes.Add(_consoleRootNode);
        _tree.BeforeExpand += Tree_BeforeExpand;
        _tree.AfterSelect += Tree_AfterSelect;
        _tree.NodeMouseClick += Tree_NodeMouseClick;
        _tree.Enter += (_, _) => _activePane = ActivePane.Scope;

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            SmallImageList = _resultImages,
            VirtualMode = true,
        };
        _list.RetrieveVirtualItem += List_RetrieveVirtualItem;
        _list.MouseDoubleClick += List_MouseDoubleClick;
        _list.MouseClick += List_MouseClick;
        _list.ItemSelectionChanged += List_ItemSelectionChanged;
        _list.Enter += (_, _) => _activePane = ActivePane.Result;

        var splitContainer = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Panel1MinSize = 100,
            Panel2MinSize = 100,
        };
        splitContainer.Panel1.Controls.Add(_tree);
        splitContainer.Panel2.Controls.Add(_list);

        _statusLabel = new ToolStripStatusLabel { Text = "Ready" };
        _statusStrip = new StatusStrip();
        _statusStrip.Items.Add(_statusLabel);

        var menuStrip = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("&File");
        var addSnapInItem = new ToolStripMenuItem("&Add/Remove Snap-in...", null, (_, _) => AddSnapIn());
        var exitItem = new ToolStripMenuItem("E&xit", null, (_, _) => Close());
        fileMenu.DropDownItems.Add(addSnapInItem);
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(exitItem);

        var actionMenu = new ToolStripMenuItem("&Action");
        _propertiesMenuItem = new ToolStripMenuItem("&Properties", null, (_, _) => ShowPropertiesForCurrentSelection());
        _refreshMenuItem = new ToolStripMenuItem("&Refresh", null, (_, _) => RefreshCurrentView());
        actionMenu.DropDownItems.Add(_propertiesMenuItem);
        actionMenu.DropDownItems.Add(_refreshMenuItem);

        menuStrip.Items.Add(fileMenu);
        menuStrip.Items.Add(actionMenu);

        var toolStrip = new ToolStrip();
        toolStrip.Items.Add(new ToolStripButton("Add Snap-in...", null, (_, _) => AddSnapIn()));
        _propertiesToolButton = new ToolStripButton("Properties", null, (_, _) => ShowPropertiesForCurrentSelection());
        _refreshToolButton = new ToolStripButton("Refresh", null, (_, _) => RefreshCurrentView());
        toolStrip.Items.Add(_propertiesToolButton);
        toolStrip.Items.Add(_refreshToolButton);

        _treeContextMenu = new ContextMenuStrip();
        _treeContextMenu.Items.Add("Properties", null, (_, _) => ShowScopePropertiesForSelection());
        _removeSnapInMenuItem = new ToolStripMenuItem("Remove Snap-in", null, (_, _) => RemoveSelectedSnapIn());
        _treeContextMenu.Items.Add(_removeSnapInMenuItem);

        _listContextMenu = new ContextMenuStrip();
        _listContextMenu.Items.Add("Properties", null, (_, _) => ShowResultPropertiesForFocusedItem());

        Controls.Add(splitContainer);
        Controls.Add(_statusStrip);
        Controls.Add(toolStrip);
        Controls.Add(menuStrip);
        MainMenuStrip = menuStrip;

        _console = new MmcConsole(_tree, _list, _consoleRootNode, _scopeImages, _resultImages, this);
        _console.StatusTextChanged += text => _statusLabel.Text = text;

        _consoleRootNode.Expand();
        UpdateVerbBasedUiState();

        // SplitterDistance can only be set once the container has its real,
        // final size - setting it any earlier (e.g. in an object initializer,
        // before the control is parented and laid out) throws.
        Load += (_, _) => splitContainer.SplitterDistance = 280;
    }

    private void AddSnapIn()
    {
        List<SnapInInfo> snapIns;
        try
        {
            snapIns = SnapInRegistry.EnumerateSnapIns();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not read registered snap-ins:\n{ex.Message}", "Add Snap-in", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using var picker = new SnapInPickerDialog(snapIns);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.SelectedSnapIn is null)
        {
            return;
        }

        try
        {
            var session = SnapInSession.Load(picker.SelectedSnapIn, _console);
            _sessions.Add(session);
            _statusLabel.Text = $"Loaded '{picker.SelectedSnapIn.Name}'.";
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"Failed to load '{picker.SelectedSnapIn.Name}' ({picker.SelectedSnapIn.Clsid}): {ex}");
            MessageBox.Show(
                this,
                $"Could not load '{picker.SelectedSnapIn.Name}':\n{ex.Message}\n\n" +
                "Some snap-ins rely on behavior specific to mmc.exe's own console engine and may not run in a third-party host.",
                "Add Snap-in",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void RemoveSelectedSnapIn()
    {
        if (_tree.SelectedNode is null || !_console.NodesByUiNode.TryGetValue(_tree.SelectedNode, out var node) || node.ParentHandle != IntPtr.Zero)
        {
            return;
        }

        var session = _sessions.FirstOrDefault(s => s == node.Session);
        try
        {
            session?.Destroy();
        }
        catch
        {
            // Best effort - still remove it from our own UI/state below.
        }

        if (session is not null)
        {
            _sessions.Remove(session);
        }

        if (ReferenceEquals(_currentNode, node))
        {
            _currentNode = null;
            _console.DeleteAllRsltItems();
            _list.Columns.Clear();
        }

        // Explicit-interface call: cleans up our internal scope-node
        // dictionaries (recursively, for the whole subtree) as well as the
        // visible TreeNode, matching what a snap-in itself would trigger.
        ((IConsoleNameSpace)_console).DeleteItem(node.Handle, 1);
        UpdateVerbBasedUiState();
    }

    private void Tree_BeforeExpand(object? sender, TreeViewCancelEventArgs e)
    {
        if (_console.NodesByUiNode.TryGetValue(e.Node!, out var node))
        {
            node.Session.ExpandNode(_console, node);
        }
    }

    private void Tree_AfterSelect(object? sender, TreeViewEventArgs e)
    {
        if (!_console.NodesByUiNode.TryGetValue(e.Node!, out var node))
        {
            return;
        }

        if (_currentNode is not null && _currentNode != node)
        {
            _currentNode.Session.HideResults(_console, _currentNode);
        }

        _console.DeleteAllRsltItems();
        _list.Columns.Clear();

        _currentNode = node;
        node.Session.ShowResults(_console, node);
        _statusLabel.Text = node.DisplayName;
        UpdateVerbBasedUiState();
    }

    private void List_ItemSelectionChanged(object? sender, ListViewItemSelectionChangedEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _console.ResultRows.Count)
        {
            return;
        }

        var row = _console.ResultRows[e.ItemIndex];
        row.Session.NotifyResultSelect(_console, row, e.IsSelected);
        UpdateVerbBasedUiState();
    }

    /// <summary>
    /// Reflects whatever the currently active snap-in last told our
    /// IConsoleVerb (via SetVerbState, typically from inside its
    /// MMCN_SELECT handler) onto the actual UI - a verb a snap-in disabled
    /// stayed clickable until this was wired up.
    /// </summary>
    private void UpdateVerbBasedUiState()
    {
        bool hasSelection = _currentNode is not null;
        _console.GetVerbState(MMC_CONSOLE_VERB.MMC_VERB_PROPERTIES, MMC_BUTTON_STATE.ENABLED, out bool propertiesEnabled);
        _console.GetVerbState(MMC_CONSOLE_VERB.MMC_VERB_REFRESH, MMC_BUTTON_STATE.ENABLED, out bool refreshEnabled);

        _propertiesMenuItem.Enabled = hasSelection && propertiesEnabled;
        _propertiesToolButton.Enabled = hasSelection && propertiesEnabled;
        _refreshMenuItem.Enabled = hasSelection && refreshEnabled;
        _refreshToolButton.Enabled = hasSelection && refreshEnabled;
    }

    private void Tree_NodeMouseClick(object? sender, TreeNodeMouseClickEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
        {
            return;
        }

        _tree.SelectedNode = e.Node;
        if (_console.NodesByUiNode.TryGetValue(e.Node, out var node))
        {
            _removeSnapInMenuItem.Visible = node.ParentHandle == IntPtr.Zero;
            _treeContextMenu.Show(_tree, e.Location);
        }
    }

    private void List_RetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _console.ResultRows.Count)
        {
            e.Item = new ListViewItem();
            return;
        }

        var row = _console.ResultRows[e.ItemIndex];
        var text = _list.Columns.Count > 0 ? _console.GetResultColumnText(row, 0) : string.Empty;
        var item = new ListViewItem(text)
        {
            ImageIndex = MmcConsole.OffsetImageIndex(row.ImageIndex, row.Session.ResultImageBase),
            Tag = row,
        };
        for (int col = 1; col < _list.Columns.Count; col++)
        {
            item.SubItems.Add(_console.GetResultColumnText(row, col));
        }

        e.Item = item;
    }

    private void List_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
        {
            return;
        }

        var hit = _list.HitTest(e.Location);
        if (hit.Item is not null)
        {
            hit.Item.Selected = true;
            _list.FocusedItem = hit.Item;
            _listContextMenu.Show(_list, e.Location);
        }
    }

    private void List_MouseDoubleClick(object? sender, MouseEventArgs e) => ShowResultPropertiesForFocusedItem();

    /// <summary>
    /// The single "Properties" action on the menu bar/toolbar has to guess
    /// which pane the user means, since - unlike the tree's and list's own
    /// context menus - it isn't tied to a specific right-click target.
    /// ToolStrip items don't take focus away from whichever pane the user
    /// was last in (clicking a toolbar button doesn't move focus in
    /// WinForms), so checking which pane currently has focus reflects the
    /// user's last interaction correctly.
    /// </summary>
    private void ShowPropertiesForCurrentSelection()
    {
        // Falls back to the scope node whenever the result pane was the
        // active one but doesn't actually have a usable selection right
        // now (empty list, or focus landed there without an item picked) -
        // "nothing to show here" is a worse outcome than showing the tree
        // selection that does exist.
        if (_activePane == ActivePane.Result && _list.FocusedItem?.Tag is ResultRow)
        {
            ShowResultPropertiesForFocusedItem();
        }
        else
        {
            ShowScopePropertiesForSelection();
        }
    }

    private void ShowScopePropertiesForSelection()
    {
        if (_tree.SelectedNode is null || !_console.NodesByUiNode.TryGetValue(_tree.SelectedNode, out var node))
        {
            return;
        }

        var dataObject = node.Session.TryGetScopeDataObject(node);
        PropertySheetHost.ShowFor(this, node.DisplayName, dataObject, node.Session.ComponentData, node.Session.Component);
    }

    private void ShowResultPropertiesForFocusedItem()
    {
        if (_list.FocusedItem?.Tag is not ResultRow row)
        {
            return;
        }

        var dataObject = row.Session.TryGetResultDataObject(row);
        PropertySheetHost.ShowFor(this, _list.FocusedItem.Text, dataObject, row.Session.Component, row.Session.ComponentData);
    }

    private void RefreshCurrentView()
    {
        if (_currentNode is null)
        {
            return;
        }

        _console.DeleteAllRsltItems();
        _list.Columns.Clear();
        _currentNode.ResultsLoaded = false;
        _currentNode.Session.ShowResults(_console, _currentNode);
        UpdateVerbBasedUiState();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        foreach (var session in _sessions)
        {
            try
            {
                session.Destroy();
            }
            catch
            {
                // Best effort during shutdown.
            }
        }

        base.OnFormClosing(e);
    }
}
