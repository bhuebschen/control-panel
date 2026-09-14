using System.Runtime.InteropServices;

namespace ControlPanel.App.Native;

internal enum PeMachine : ushort
{
    Unknown = 0,
    I386 = 0x014c,
    Amd64 = 0x8664,
    Arm64 = 0xAA64,
}

/// <summary>
/// Reads just enough of a PE file's header to tell what CPU architecture a
/// native DLL was built for - used to catch "this snap-in is 32-bit only"
/// before attempting CoCreateInstance, which would otherwise fail with the
/// same generic "class not registered" error as a snap-in that simply
/// isn't installed, leaving the real cause impossible to tell from the
/// error message alone.
/// </summary>
internal static class PeImage
{
    public static PeMachine GetMachineType(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);

        // DOS header: e_lfanew (offset of the PE header) sits at offset 0x3C.
        stream.Seek(0x3C, SeekOrigin.Begin);
        int peHeaderOffset = reader.ReadInt32();

        stream.Seek(peHeaderOffset, SeekOrigin.Begin);
        uint peSignature = reader.ReadUInt32(); // should be "PE\0\0"
        if (peSignature != 0x00004550)
        {
            return PeMachine.Unknown;
        }

        ushort machine = reader.ReadUInt16(); // IMAGE_FILE_HEADER.Machine
        return Enum.IsDefined(typeof(PeMachine), machine) ? (PeMachine)machine : PeMachine.Unknown;
    }

    public static PeMachine CurrentProcessMachine =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => PeMachine.I386,
            Architecture.X64 => PeMachine.Amd64,
            Architecture.Arm64 => PeMachine.Arm64,
            _ => PeMachine.Unknown,
        };

    /// <summary>
    /// Best-effort check: returns false only when we could positively read
    /// both machine types and they disagree. Any I/O or parse failure (odd
    /// registration, packed/rundll-hosted server, etc.) returns true rather
    /// than block loading on a check that itself might be wrong.
    /// </summary>
    public static bool IsLikelyCompatibleWithCurrentProcess(string dllPath, out PeMachine dllMachine)
    {
        dllMachine = PeMachine.Unknown;
        try
        {
            if (!File.Exists(dllPath))
            {
                return true;
            }

            dllMachine = GetMachineType(dllPath);
            return dllMachine == PeMachine.Unknown || dllMachine == CurrentProcessMachine;
        }
        catch
        {
            return true;
        }
    }
}
