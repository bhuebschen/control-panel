using System.Drawing;
using System.Text;
using System.Windows.Forms;
using ControlPanel.App.Hosting;

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
    public static void RunLoadDiagnostic(string nameOrClsid, string? outputPath)
    {
        outputPath ??= Path.Combine(Path.GetTempPath(), "controlpanel-diag.txt");
        File.WriteAllText(outputPath, string.Empty);

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
