using System.Drawing;
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
            Line($"Root scope nodes created: {consoleRoot.Nodes.Count}");
            foreach (TreeNode node in consoleRoot.Nodes)
            {
                Line($"  - '{node.Text}' (children: {node.Nodes.Count})");
            }

            try
            {
                session.ComponentData.Destroy();
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
