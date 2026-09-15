using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using ControlPanel.App.Hosting;
using ControlPanel.App.Interop;

namespace ControlPanel.App.Native;

/// <summary>
/// Headless "load this one snap-in and report exactly what happened"
/// diagnostic, invoked via `ControlPanel.App.exe --diag-load-snapin
/// "&lt;name substring or {CLSID}&gt;" [output-file]`. Exists because a
/// crash or exception during Add Snap-in is otherwise only visible through
/// a MessageBox showing `ex.Message` (no stack trace) or by attaching a
/// debugger - this reproduces the exact same load path non-interactively
/// and writes the full exception chain (type, message, stack trace, every
/// InnerException) to a file, so it can be inspected without a debugger
/// and without clicking through the UI each time.
/// </summary>
internal static class SnapInDiagnostics
{
    private static string? _tracePath;

    /// <summary>
    /// Durable, immediate-append entry logging for MmcConsole callbacks -
    /// a no-op outside RunLoadDiagnostic (normal MainForm usage never sets
    /// _tracePath). Distinct from FirstChanceException logging: this fires
    /// on *every* call, not just ones that throw, so it can show the last
    /// successfully-completed callback before a native call fails with no
    /// managed exception at all (e.g. a plain HRESULT failure).
    /// </summary>
    public static void Trace(string message)
    {
        if (_tracePath is null)
        {
            return;
        }

        try
        {
            File.AppendAllText(_tracePath, $"[Trace] {message}{Environment.NewLine}");
        }
        catch
        {
            // Best-effort only.
        }
    }

    public static void RunLoadDiagnostic(string nameOrClsid, string? outputPath)
    {
        outputPath ??= Path.Combine(Path.GetTempPath(), "controlpanel-diag.txt");
        File.WriteAllText(outputPath, string.Empty);
        _tracePath = outputPath;

        void Line(string text)
        {
            Console.WriteLine(text);
            // Appended immediately (not buffered until a `finally` block),
            // so a hard, unrecoverable failure (Internal CLR error,
            // AccessViolationException) that kills the process outright
            // still leaves a complete log on disk up to the last call made.
            File.AppendAllText(outputPath, text + Environment.NewLine);
        }

        // Reentrant native->managed callbacks lose their original managed
        // stack trace once the exception crosses back out through the COM
        // boundary (it resurfaces attributed to the outer interop call site
        // instead). FirstChanceException fires at the true throw site,
        // before any of that happens, so this is the only way to see
        // exactly which of our own callback methods actually threw.
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> firstChance = (_, e) =>
        {
            Line($"[FirstChanceException] {e.Exception.GetType().FullName}: {e.Exception.Message}");
            Line(e.Exception.StackTrace ?? "(no stack trace available)");
        };
        AppDomain.CurrentDomain.FirstChanceException += firstChance;

        try
        {
            Line($"=== SnapIn load diagnostic: '{nameOrClsid}' ===");
            Line($"Time: {DateTime.Now:O}");
            Line($"Process bitness: {(Environment.Is64BitProcess ? "x64" : "x86")}");
            Line("");

            var snapIns = SnapInRegistry.EnumerateSnapIns();
            Line($"Found {snapIns.Count} registered snap-ins.");

            var match = snapIns.FirstOrDefault(s =>
                    string.Equals(s.Clsid.ToString("B"), nameOrClsid, StringComparison.OrdinalIgnoreCase))
                ?? snapIns.FirstOrDefault(s =>
                    s.Name.Contains(nameOrClsid, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                Line($"No registered snap-in matched '{nameOrClsid}'. Known names:");
                foreach (var s in snapIns)
                {
                    Line($"  - {s.Name}  {s.Clsid:B}");
                }
                return;
            }

            Line($"Matched: '{match.Name}'  CLSID={match.Clsid:B}  Provider={match.Provider}  Version={match.Version}");
            Line("");

            using var scopeImages = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
            using var resultImages = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
            using var tree = new TreeView();
            using var list = new ListView { View = View.Details, VirtualMode = true };
            using var ownerForm = new Form();
            var consoleRoot = new TreeNode("Console Root");
            tree.Nodes.Add(consoleRoot);

            var console = new MmcConsole(tree, list, consoleRoot, scopeImages, resultImages, ownerForm);

            // Self-check: does our own CCW actually answer QueryInterface for
            // IControlbar/IToolbar? These were added specifically because
            // "Services"/"Component Services" were suspected to probe for
            // them early - if QI itself fails despite MmcConsole declaring
            // both interfaces, the bug is in how the CCW exposes them, not
            // in whether the snap-in asks for them.
            {
                IntPtr consoleUnk = Marshal.GetIUnknownForObject(console);
                try
                {
                    var controlbarIid = typeof(IControlbar).GUID;
                    int hr1 = Marshal.QueryInterface(consoleUnk, ref controlbarIid, out IntPtr pControlbar);
                    Line($"Self-check QI(IControlbar {controlbarIid:B}) = 0x{hr1:X8}");
                    if (pControlbar != IntPtr.Zero) Marshal.Release(pControlbar);

                    var toolbarIid = typeof(IToolbar).GUID;
                    int hr2 = Marshal.QueryInterface(consoleUnk, ref toolbarIid, out IntPtr pToolbar);
                    Line($"Self-check QI(IToolbar {toolbarIid:B}) = 0x{hr2:X8}");
                    if (pToolbar != IntPtr.Zero) Marshal.Release(pToolbar);
                }
                finally
                {
                    Marshal.Release(consoleUnk);
                }
            }

            Line("Calling SnapInSession.Load()...");
            var session = SnapInSession.Load(match, console);
            Line("SnapInSession.Load() returned successfully.");
            Line("Expanding the static root node...");
            session.ExpandNode(console, session.RootNode);
            Line("Static root expansion returned successfully.");
            Line("Creating and showing the result view...");
            session.ShowResults(console, session.RootNode);
            Line("Result view initialization returned successfully.");
            Line($"Root scope nodes created: {consoleRoot.Nodes.Count}");

            ScopeNode? activeResultNode = session.RootNode;

            // Recursively expands every scope node (not just the root),
            // to reproduce/report structural issues at any depth - e.g. a
            // child that inserts no items of its own, or one that ends up
            // attached under the wrong parent (ParentHandle mismatch).
            void DumpAndExpand(TreeNode uiNode, int depth)
            {
                if (depth > 8)
                {
                    Line($"{new string(' ', depth * 2)}... (depth limit reached, stopping)");
                    return;
                }

                var indent = new string(' ', depth * 2);
                console.NodesByUiNode.TryGetValue(uiNode, out var scopeNode);
                string extra = scopeNode is null
                    ? " (not a tracked scope node)"
                    : $" [handle=0x{scopeNode.Handle:X}, parent=0x{scopeNode.ParentHandle:X}, cookie=0x{scopeNode.Cookie:X}, hasChildren={scopeNode.HasChildren}]";
                Line($"{indent}- '{uiNode.Text}'{extra}");

                if (scopeNode is not null && scopeNode.HasChildren && !scopeNode.ChildrenLoaded)
                {
                    try
                    {
                        session.ExpandNode(console, scopeNode);
                    }
                    catch (Exception ex)
                    {
                        Line($"{indent}  (expand FAILED: {ex.GetType().Name}: {ex.Message})");
                    }
                }

                // Also show the result pane for this node - a snap-in commonly
                // has real result rows on a node it reports zero *scope*
                // children for (e.g. a leaf like "Benutzer"/"Users").
                if (scopeNode is not null)
                {
                    try
                    {
                        if (!ReferenceEquals(activeResultNode, scopeNode))
                        {
                            if (activeResultNode is not null)
                            {
                                activeResultNode.Session.HideResults(console, activeResultNode);
                            }

                            // A real MMC result pane is replaced when the
                            // selected scope node changes. Keeping the old
                            // rows/columns makes the diagnostic accumulate
                            // state that the interactive host never exposes
                            // and can make a healthy snap-in look broken.
                            console.DeleteAllRsltItems();
                            list.Columns.Clear();
                            activeResultNode = null;

                            scopeNode.Session.ShowResults(console, scopeNode);
                            activeResultNode = scopeNode;
                        }

                        Line($"{indent}  (result rows: {console.ResultRows.Count})");
                        int shown = 0;
                        foreach (var row in console.ResultRows)
                        {
                            if (shown++ >= 5)
                            {
                                Line($"{indent}    ... ({console.ResultRows.Count - 5} more)");
                                break;
                            }
                            var col0 = console.GetResultColumnText(row, 0);
                            Line($"{indent}    row itemID=0x{row.ItemId:X} cookie=0x{row.Cookie:X} col0='{col0}'");

                            if (shown == 1)
                            {
                                var rowDataObject = row.Session.TryGetResultDataObject(row);
                                if (rowDataObject is null)
                                {
                                    Line($"{indent}      Properties: no data object available for this row");
                                }
                                else
                                {
                                    bool found = PropertySheetHost.TryFindPages(
                                        rowDataObject,
                                        new object?[] { row.Session.Component, row.Session.ComponentData },
                                        out var pages);
                                    Line($"{indent}      Properties: found={found}, pages={pages.Count}: [{string.Join(", ", pages.Select(p => $"0x{p:X}"))}]");
                                    foreach (var page in pages)
                                    {
                                        Line($"{indent}      Destroying page 0x{page:X}...");
                                        Win32.DestroyPropertySheetPage(page);
                                        Line($"{indent}      Destroyed page 0x{page:X} OK");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Line($"{indent}  (ShowResults FAILED: {ex.GetType().Name}: {ex.Message})");
                    }
                }

                foreach (TreeNode child in uiNode.Nodes)
                {
                    if (child.Text == "...")
                    {
                        continue;
                    }
                    DumpAndExpand(child, depth + 1);
                }
            }

            foreach (TreeNode node in consoleRoot.Nodes)
            {
                DumpAndExpand(node, 1);
            }

            if (activeResultNode is not null)
            {
                activeResultNode.Session.HideResults(console, activeResultNode);
                activeResultNode = null;
            }

            try
            {
                session.Destroy();
            }
            catch (Exception cleanupEx)
            {
                Line($"(non-fatal) Destroy() during cleanup threw: {cleanupEx}");
            }

            Line("");
            Line("=== SUCCESS ===");
        }
        catch (Exception ex)
        {
            Line("");
            Line("=== FAILED ===");
            Line(FormatExceptionChain(ex));
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= firstChance;
            _tracePath = null;
            Console.WriteLine($"(full log written to {outputPath})");
        }
    }

    private static string FormatExceptionChain(Exception ex)
    {
        var sb = new StringBuilder();
        Exception? current = ex;
        int depth = 0;
        while (current is not null)
        {
            sb.AppendLine($"--- Exception depth {depth} : {current.GetType().FullName} ---");
            sb.AppendLine($"Message: {current.Message}");
            sb.AppendLine("StackTrace:");
            sb.AppendLine(current.StackTrace ?? "(none)");
            sb.AppendLine();
            current = current.InnerException;
            depth++;
        }
        return sb.ToString();
    }
}
