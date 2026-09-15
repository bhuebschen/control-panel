using System.Runtime.InteropServices;

namespace ControlPanel.App.Native;

/// <summary>
/// Legacy MFC (Microsoft Foundation Classes) extension DLLs - "Services"
/// and "Shared Folders" are both implemented by one, filemgmt.dll, from the
/// Visual C++ 6 / MFC 4.2 era - assume they run inside a process that has
/// already created an MFC CWinApp instance (real mmc.exe apparently
/// provides, or is, one). This host is a plain WinForms/.NET process and
/// never creates one, so AfxGetApp() resolves to NULL inside such a DLL,
/// and any code path that calls a virtual method through it (confirmed via
/// live native debugging: mfc42u.dll!CCmdTarget::BeginWaitCursor, reached
/// while filemgmt.dll populates a result list) crashes the whole process
/// with an unrecoverable AccessViolationException - .NET/CoreCLR cannot
/// catch this in managed code under any circumstances, with or without
/// [HandleProcessCorruptedStateExceptions] (that attribute is a .NET
/// Framework-only mechanism, ignored entirely since .NET Core).
///
/// There is no clean, external way to give such a DLL a real CWinApp:
/// AfxGetApp/AfxWinInit/AfxGetModuleState are `inline` functions compiled
/// directly into *each calling module* in classic MFC (confirmed absent
/// from mfc42u.dll's actual export table and from Ghidra's independently
/// curated ordinal database for it) - they read that module's own private,
/// unexported state, which a shim DLL built against any other copy of MFC
/// (there is no matching MFC 4.2 toolchain available at all here) could
/// never reach.
///
/// Instead, this traps the *specific, confirmed* crash via a Vectored
/// Exception Handler (a standard, documented Windows mechanism - not
/// something .NET-specific and not exotic) installed before any snap-in is
/// loaded. When the fault matches exactly (an access violation reading
/// address 0, with RCX also 0, at an instruction inside mfc42u.dll's own
/// mapped range - confirmed via disassembly to be a `mov rax, [rcx]`
/// dereferencing the null CWinApp pointer immediately before an indirect
/// call through its vtable), it redirects RCX to a fake "app object"
/// whose entire (large) vtable points at a single harmless, real,
/// Control-Flow-Guard-valid function (kernel32!GetCurrentThreadId - a
/// genuinely exported, address-taken system function, so CFG's indirect-
/// call check passes) and resumes execution. The CPU retries the exact
/// same instruction, now succeeds, and the eventual indirect call reaches
/// GetCurrentThreadId instead of crashing - its return value and any
/// ignored arguments are harmless, and since this is purely a wait-cursor
/// bookkeeping call whose result nothing downstream consumes, the net
/// effect is simply "no wait cursor", not corrupted state.
///
/// This is a narrow, first-cut heuristic for the one exact crash found so
/// far - it does not attempt to handle every possible null-CWinApp call
/// shape (only RCX-as-`this`, read access, exactly address 0, inside
/// mfc42u.dll). A different call site (different register, different
/// module, a write instead of a read) would not match and would fall
/// through to the normal, fatal crash.
/// </summary>
internal static class MfcCompatibilityShim
{
    private const uint STATUS_ACCESS_VIOLATION = 0xC0000005;
    private const long EXCEPTION_CONTINUE_EXECUTION = -1;
    private const long EXCEPTION_CONTINUE_SEARCH = 0;

    // Offsets into the real Windows AMD64 CONTEXT struct (winnt.h) - only
    // the few fields this handler actually touches.
    private const int ContextRcxOffset = 0x80;
    private const int ContextRipOffset = 0xF8;

    // Offsets into EXCEPTION_RECORD (winnt.h).
    private const int ExceptionCodeOffset = 0x00;
    private const int ExceptionInformationOffset = 0x20;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr AddVectoredExceptionHandler(uint first, VectoredExceptionHandler handler);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    private delegate long VectoredExceptionHandler(IntPtr exceptionPointers);

    // Must be kept alive for the process lifetime - AddVectoredExceptionHandler
    // does not itself root the delegate against the GC.
    private static readonly VectoredExceptionHandler s_handler = HandleException;
    private static IntPtr s_dummyAppObject;
    private static bool s_installed;

    public static void Install()
    {
        if (s_installed)
        {
            return;
        }

        s_installed = true;
        IntPtr handle = AddVectoredExceptionHandler(1, s_handler);
        SnapInDiagnostics.Trace($"MfcCompatibilityShim installed (handle=0x{handle:X})");
    }

    private static long HandleException(IntPtr exceptionPointers)
    {
        try
        {
            IntPtr exceptionRecord = Marshal.ReadIntPtr(exceptionPointers, 0);
            IntPtr contextRecord = Marshal.ReadIntPtr(exceptionPointers, 8);

            uint code = unchecked((uint)Marshal.ReadInt32(exceptionRecord, ExceptionCodeOffset));
            if (code != STATUS_ACCESS_VIOLATION)
            {
                return EXCEPTION_CONTINUE_SEARCH;
            }

            long accessType = Marshal.ReadInt64(exceptionRecord, ExceptionInformationOffset);
            long faultingAddress = Marshal.ReadInt64(exceptionRecord, ExceptionInformationOffset + 8);
            if (accessType != 0 || faultingAddress != 0)
            {
                return EXCEPTION_CONTINUE_SEARCH; // not a null-pointer read
            }

            long rip = Marshal.ReadInt64(contextRecord, ContextRipOffset);
            if (!NativePointerGuard.IsFromModule(new IntPtr(rip), "mfc42u.dll"))
            {
                return EXCEPTION_CONTINUE_SEARCH;
            }

            long rcx = Marshal.ReadInt64(contextRecord, ContextRcxOffset);
            if (rcx != 0)
            {
                return EXCEPTION_CONTINUE_SEARCH; // a different register was null - don't guess
            }

            Marshal.WriteInt64(contextRecord, ContextRcxOffset, GetOrCreateDummyAppObject().ToInt64());
            SnapInDiagnostics.Trace($"MfcCompatibilityShim: recovered null-CWinApp read at RIP=0x{rip:X}, redirected RCX to dummy app object");
            return EXCEPTION_CONTINUE_EXECUTION;
        }
        catch
        {
            // A handler that itself throws is worse than not having one.
            return EXCEPTION_CONTINUE_SEARCH;
        }
    }

    /// <summary>
    /// A fake object whose vtable is one function pointer (kernel32!
    /// GetCurrentThreadId - real, exported, Control-Flow-Guard-valid,
    /// takes no meaningful arguments, returns a harmless DWORD) repeated
    /// many times, so *any* virtual call through it - regardless of which
    /// vtable slot the original code intended - resolves safely instead of
    /// crashing or tripping a CFG violation (which is itself fatal and
    /// uncatchable, unlike a plain access violation).
    /// </summary>
    private static IntPtr GetOrCreateDummyAppObject()
    {
        if (s_dummyAppObject != IntPtr.Zero)
        {
            return s_dummyAppObject;
        }

        IntPtr kernel32 = GetModuleHandle("kernel32.dll");
        IntPtr getCurrentThreadId = GetProcAddress(kernel32, "GetCurrentThreadId");

        const int vtableEntries = 1024; // covers vtable offsets up to 8KB
        IntPtr vtable = Marshal.AllocHGlobal(vtableEntries * IntPtr.Size);
        for (int i = 0; i < vtableEntries; i++)
        {
            Marshal.WriteIntPtr(vtable, i * IntPtr.Size, getCurrentThreadId);
        }

        // The "object" itself must be generously sized and pre-filled, not
        // just a single pointer-sized slot for its vtable pointer: opening
        // and closing a real property page (itself implemented by the same
        // MFC-based DLL) goes further than the one virtual call the
        // wait-cursor crash needed, and can read *or write* CWinApp member
        // fields directly (not through the vtable) at other offsets from
        // `this`. An 8-byte allocation there turned a clean, caught access
        // violation into intermittent native heap corruption
        // (STATUS_HEAP_CORRUPTION / 0xC0000374) once something wrote past
        // it - confirmed via Windows Error Reporting after this was too
        // small. Every 8-byte slot in the object is also pre-filled with
        // the vtable pointer itself, in case some other offset is treated
        // as a secondary vtable pointer (common with C++ multiple
        // inheritance, which CWinApp uses).
        const int objectSize = 4096;
        IntPtr obj = Marshal.AllocHGlobal(objectSize);
        for (int offset = 0; offset < objectSize; offset += IntPtr.Size)
        {
            Marshal.WriteIntPtr(obj, offset, vtable);
        }

        s_dummyAppObject = obj;
        return obj;
    }
}
