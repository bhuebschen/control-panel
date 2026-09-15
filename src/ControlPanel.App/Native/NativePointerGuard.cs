using System.Runtime.InteropServices;

namespace ControlPanel.App.Native;

/// <summary>
/// Validates a raw pointer is backed by real, appropriately-protected memory
/// before this project's raw-vtable COM calls (see SnapInSession.RawNotify/
/// RawInitializeComponent) dereference or call through it.
///
/// This exists because .NET Core/5+ (unlike .NET Framework) never delivers
/// AccessViolationException to managed code, with or without
/// [HandleProcessCorruptedStateExceptions] - a bad pointer read always
/// terminates the process immediately, with no way to catch it. The only
/// way to avoid that crash is to check the memory *before* touching it, via
/// the same VirtualQuery-based technique the OS itself effectively uses
/// internally - never by trying to catch the fault afterward.
/// </summary>
internal static class NativePointerGuard
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, nuint dwLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    /// <summary>
    /// True if <paramref name="address"/> falls within the loaded module
    /// <paramref name="moduleName"/>'s mapped image (matched via the
    /// allocation base VirtualQuery reports, which for a loaded PE image is
    /// the module's own base address - the same technique
    /// GetModuleHandle/GetModuleFileName use internally). Used by
    /// MfcCompatibilityShim to confirm a faulting instruction genuinely
    /// lives inside a specific system DLL before "fixing up" the fault.
    /// </summary>
    public static bool IsFromModule(IntPtr address, string moduleName)
    {
        IntPtr moduleBase = GetModuleHandle(moduleName);
        if (moduleBase == IntPtr.Zero)
        {
            return false;
        }

        return TryQuery(address, out var mbi) && mbi.AllocationBase == moduleBase;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_GUARD = 0x100;
    private const uint PAGE_EXECUTE = 0x10;
    private const uint PAGE_EXECUTE_READ = 0x20;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint PAGE_EXECUTE_WRITECOPY = 0x80;
    private const uint PAGE_READONLY = 0x02;
    private const uint PAGE_READWRITE = 0x04;
    private const uint PAGE_WRITECOPY = 0x08;

    private static bool TryQuery(IntPtr address, out MEMORY_BASIC_INFORMATION mbi)
    {
        mbi = default;
        if (address == IntPtr.Zero)
        {
            return false;
        }

        nuint size = (nuint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        return VirtualQuery(address, out mbi, size) == size;
    }

    /// <summary>
    /// True if <paramref name="address"/> points at committed memory this
    /// process can safely read (e.g. a vtable itself, or the target of an
    /// [out] pointer parameter) - not necessarily executable.
    /// </summary>
    public static bool IsReadable(IntPtr address)
    {
        if (!TryQuery(address, out var mbi))
        {
            return false;
        }

        if (mbi.State != MEM_COMMIT || (mbi.Protect & PAGE_GUARD) != 0 || (mbi.Protect & PAGE_NOACCESS) != 0)
        {
            return false;
        }

        const uint readableMask = PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY |
                                   PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
        return (mbi.Protect & readableMask) != 0;
    }

    /// <summary>
    /// True if <paramref name="address"/> points at committed, executable
    /// memory - what a function pointer read out of a vtable slot must
    /// point at before this project calls through it.
    /// </summary>
    public static bool IsExecutable(IntPtr address)
    {
        if (!TryQuery(address, out var mbi))
        {
            return false;
        }

        if (mbi.State != MEM_COMMIT || (mbi.Protect & PAGE_GUARD) != 0 || (mbi.Protect & PAGE_NOACCESS) != 0)
        {
            return false;
        }

        const uint executableMask = PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
        return (mbi.Protect & executableMask) != 0;
    }

    /// <summary>Throws a clean, catchable exception instead of letting a bad read crash the process.</summary>
    public static void EnsureReadable(IntPtr address, string what)
    {
        if (!IsReadable(address))
        {
            throw new InvalidOperationException(
                $"Refusing to read {what} at 0x{address:X} - not committed, readable memory. " +
                "This would have crashed the process (AccessViolationException) instead of throwing this.");
        }
    }

    /// <summary>Throws a clean, catchable exception instead of letting a bad call crash the process.</summary>
    public static void EnsureExecutable(IntPtr address, string what)
    {
        if (!IsExecutable(address))
        {
            throw new InvalidOperationException(
                $"Refusing to call {what} at 0x{address:X} - not committed, executable memory. " +
                "This would have crashed the process (AccessViolationException) instead of throwing this.");
        }
    }
}
