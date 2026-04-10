using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static Volt.NativeMethods;

namespace Volt;

public sealed class TerminalUnavailableException : Exception
{
    public TerminalUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

public readonly struct PtyHandles : IDisposable
{
    public IntPtr PseudoConsole { get; init; }
    public SafeFileHandle Input { get; init; }
    public SafeFileHandle Output { get; init; }
    public Process Process { get; init; }

    public void Dispose()
    {
        try { if (PseudoConsole != IntPtr.Zero) ClosePseudoConsole(PseudoConsole); } catch { }
        try { Input?.Dispose(); } catch { }
        try { Output?.Dispose(); } catch { }
        try { if (Process != null && !Process.HasExited) Process.Kill(entireProcessTree: true); } catch { }
    }
}

public static class ConPtyHost
{
    public static PtyHandles Create(string shellExe, string? args, string cwd, short rows, short cols)
    {
        SafeFileHandle? inputReadSide = null;
        SafeFileHandle? inputWriteSide = null;
        SafeFileHandle? outputReadSide = null;
        SafeFileHandle? outputWriteSide = null;
        IntPtr hpcon = IntPtr.Zero;
        IntPtr attrList = IntPtr.Zero;
        IntPtr hpconValuePtr = IntPtr.Zero;
        Process? process = null;

        try
        {
            if (!CreatePipe(out inputReadSide, out inputWriteSide, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (input) failed");
            if (!CreatePipe(out outputReadSide, out outputWriteSide, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (output) failed");

            var size = new COORD { X = cols, Y = rows };
            int hr = CreatePseudoConsole(size, inputReadSide, outputWriteSide, 0, out hpcon);
            if (hr != 0)
                throw new TerminalUnavailableException($"CreatePseudoConsole failed (HRESULT 0x{hr:X}). Requires Windows 10 1809 or newer.");

            // Once the pseudoconsole owns these ends, we can release our copies
            inputReadSide.Dispose();
            outputWriteSide.Dispose();
            inputReadSide = null;
            outputWriteSide = null;

            // Set up STARTUPINFOEX with the pseudoconsole attribute
            IntPtr listSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
            attrList = LocalAlloc(0x0040 /*LPTR*/, listSize);
            if (attrList == IntPtr.Zero) throw new OutOfMemoryException("LocalAlloc for attribute list failed");
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref listSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");

            // UpdateProcThreadAttribute for PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE:
            // lpValue must point to the HPCON handle value; cbSize = sizeof(HPCON).
            hpconValuePtr = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(hpconValuePtr, hpcon);
            if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    hpconValuePtr, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.lpAttributeList = attrList;

            string cmdLine = string.IsNullOrEmpty(args) ? $"\"{shellExe}\"" : $"\"{shellExe}\" {args}";
            if (!CreateProcess(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false,
                EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                IntPtr.Zero, cwd, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed");

            CloseHandle(pi.hThread);
            // Keep pi.hProcess open while calling GetProcessById — the open handle keeps
            // the kernel process object alive even if the process has already exited,
            // preventing GetProcessById from throwing ArgumentException for fast-exit processes
            // like `cmd /c echo hello`. Close pi.hProcess after attaching.
            process = Process.GetProcessById(pi.dwProcessId);
            CloseHandle(pi.hProcess);

            var result = new PtyHandles
            {
                PseudoConsole = hpcon,
                Input = inputWriteSide!,
                Output = outputReadSide!,
                Process = process
            };

            // Clean up attr list and hpcon pointer buffer; handles now belong to result
            DeleteProcThreadAttributeList(attrList);
            LocalFree(attrList);
            attrList = IntPtr.Zero;
            Marshal.FreeHGlobal(hpconValuePtr);
            hpconValuePtr = IntPtr.Zero;
            inputWriteSide = null;
            outputReadSide = null;
            hpcon = IntPtr.Zero;
            return result;
        }
        catch
        {
            try { inputReadSide?.Dispose(); } catch { }
            try { inputWriteSide?.Dispose(); } catch { }
            try { outputReadSide?.Dispose(); } catch { }
            try { outputWriteSide?.Dispose(); } catch { }
            if (hpcon != IntPtr.Zero) { try { ClosePseudoConsole(hpcon); } catch { } }
            if (attrList != IntPtr.Zero)
            {
                try { DeleteProcThreadAttributeList(attrList); } catch { }
                try { LocalFree(attrList); } catch { }
            }
            if (hpconValuePtr != IntPtr.Zero) { try { Marshal.FreeHGlobal(hpconValuePtr); } catch { } }
            try { if (process != null && !process.HasExited) process.Kill(); } catch { }
            throw;
        }
    }
}
