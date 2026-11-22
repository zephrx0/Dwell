using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Dwell.Services;

internal static class ProcessWorkingDirectory
{
    public static string? Read(uint pid)
    {
        // Windows exposes the other process's parameters through its PEB. Read only the current-directory string.
        var process = NativeMethods.OpenProcess(0x1010, false, pid);
        if (process == 0) return null;
        try
        {
            var wow64 = NtQueryInformationProcess(process, 26, out var peb, IntPtr.Size, out _) == 0 && peb != 0;
            if (!wow64)
            {
                var basic = new nint[6];
                if (NtQueryBasicInformation(process, 0, basic, basic.Length * IntPtr.Size, out _) != 0) return null;
                peb = basic[1];
            }
            var x86 = wow64 || IntPtr.Size == 4;
            var pointer = new byte[x86 ? 4 : 8];
            if (!Read(process, peb + (x86 ? 0x10 : 0x20), pointer)) return null;
            var parameters = ReadPointer(pointer, x86);
            var directory = new byte[x86 ? 8 : 16];
            if (!Read(process, parameters + (x86 ? 0x24 : 0x38), directory)) return null;
            var size = BinaryPrimitives.ReadUInt16LittleEndian(directory);
            if (size < 2 || size > 4096 || size % 2 != 0) return null;
            var path = new byte[size];
            var address = ReadPointer(directory.AsSpan(x86 ? 4 : 8), x86);
            if (!Read(process, address, path)) return null;
            var value = Encoding.Unicode.GetString(path);
            return Path.IsPathFullyQualified(value) && !value.Contains('\0') ? value : null;
        }
        finally { NativeMethods.CloseHandle(process); }
    }

    private static nint ReadPointer(ReadOnlySpan<byte> data, bool x86) =>
        (nint)(x86 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : BinaryPrimitives.ReadInt64LittleEndian(data));

    private static bool Read(nint process, nint address, byte[] buffer) =>
        ReadProcessMemory(process, address, buffer, (nuint)buffer.Length, out var read) && read == (nuint)buffer.Length;

    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(nint process, int kind,
        out nint info, int size, out int written);
    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")] private static extern int NtQueryBasicInformation(nint process,
        int kind, [Out] nint[] info, int size, out int written);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadProcessMemory(nint process,
        nint address, [Out] byte[] buffer, nuint size, out nuint read);
}
