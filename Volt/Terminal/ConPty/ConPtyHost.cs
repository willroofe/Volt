using System;
using System.Collections.Generic;
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
        Process? process = null;

        // Temporarily strip DOTNET_* variables from Volt's own process environment so
        // the child inherits a clean env. Without this, pwsh.exe (built against .NET 8)
        // tries to load Volt's .NET 10 runtime via the inherited DOTNET_ROOT and dies
        // with STATUS_DLL_INIT_FAILED (0xC0000142).
        var savedDotnetVars = StripDotnetEnvVars();

        // Inject Windows Terminal identification env vars so PSReadLine and other
        // tools use their "modern terminal" rendering path. Without WT_SESSION,
        // PSReadLine falls back to legacy rendering that has buggy multi-line
        // prompt cursor tracking.
        var savedWtVars = SetWtEnvVars();

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
            // Despite lpValue being documented as PVOID (pointer), for this specific
            // attribute the kernel expects the HPCON value itself in lpValue, NOT a
            // pointer to it. This matches Microsoft's official C++ and C# ConPTY
            // samples. Passing a pointer-to-hpcon causes 0xC0000142 in the child
            // because Windows associates the child's pseudoconsole with the pointer
            // value (a heap address), and the child's CRT fails console init.
            if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    hpcon, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.lpAttributeList = attrList;

            string cmdLine = string.IsNullOrEmpty(args) ? $"\"{shellExe}\"" : $"\"{shellExe}\" {args}";
            // lpEnvironment = IntPtr.Zero → child inherits Volt's process env, which we
            // just stripped of DOTNET_* vars.
            // IMPORTANT: do NOT set CREATE_UNICODE_ENVIRONMENT here. That flag describes
            // the encoding of lpEnvironment if we pass one; combined with a NULL env it
            // can cause Windows' loader to fail DllMain in the child (0xC0000142). Match
            // Microsoft's official ConPTY sample which uses EXTENDED_STARTUPINFO_PRESENT only.
            if (!CreateProcess(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false,
                EXTENDED_STARTUPINFO_PRESENT,
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

            // Clean up attr list; handles now belong to result
            DeleteProcThreadAttributeList(attrList);
            LocalFree(attrList);
            attrList = IntPtr.Zero;
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
            try { if (process != null && !process.HasExited) process.Kill(); } catch { }
            throw;
        }
        finally
        {
            // Always restore Volt's own env vars, even on exception paths.
            RestoreDotnetEnvVars(savedDotnetVars);
            RestoreEnvVars(savedWtVars);
        }
    }

    private static List<(string Name, string? Value)> SetWtEnvVars()
    {
        // Pretend to be Windows Terminal so PSReadLine uses its modern rendering path.
        var vars = new (string Name, string Value)[]
        {
            ("WT_SESSION", Guid.NewGuid().ToString()),
            ("WT_PROFILE_ID", Guid.NewGuid().ToString()),
            ("TERM_PROGRAM", "Volt"),
            ("COLORTERM", "truecolor"),
            ("TERM", "xterm-256color"),
        };
        var saved = new List<(string, string?)>(vars.Length);
        foreach (var (name, value) in vars)
        {
            saved.Add((name, Environment.GetEnvironmentVariable(name)));
            Environment.SetEnvironmentVariable(name, value);
        }
        return saved;
    }

    private static void RestoreEnvVars(List<(string Name, string? Value)> saved)
    {
        foreach (var (name, value) in saved)
            Environment.SetEnvironmentVariable(name, value);
    }

    private static readonly string[] DotnetVarsToStrip =
    {
        "DOTNET_ROOT",
        "DOTNET_ROOT(x86)",
        "DOTNET_ROOT_X64",
        "DOTNET_ROOT_X86",
        "DOTNET_BUNDLE_EXTRACT_BASE_DIR",
    };

    private static List<(string Name, string? Value)> StripDotnetEnvVars()
    {
        var saved = new List<(string, string?)>(DotnetVarsToStrip.Length);
        foreach (var name in DotnetVarsToStrip)
        {
            var value = Environment.GetEnvironmentVariable(name);
            saved.Add((name, value));
            if (value != null)
                Environment.SetEnvironmentVariable(name, null);
        }
        return saved;
    }

    private static void RestoreDotnetEnvVars(List<(string Name, string? Value)> saved)
    {
        foreach (var (name, value) in saved)
        {
            if (value != null)
                Environment.SetEnvironmentVariable(name, value);
        }
    }
}
