using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Solarpunk.Launcher;

namespace Solarpunk.Launcher.Services;

// Manually injects UE4SS.dll into the running Solarpunk process via
// CreateRemoteThread(LoadLibraryW). This is the PROVEN injection path the
// Solarpunk headless host already uses (solarpunk/tools/inject-plugin.ps1):
// the dwmapi proxy works on the current Solarpunk build, but proxy imports are
// fragile across game updates (G2 build 0.41.0 silently stopped loading its
// proxy), so we also inject post-boot and re-inject until UE4SS confirms it
// loaded — a second LoadLibraryW on an already-loaded module is a no-op.
//
// Notes:
//  - We find the real game process BY NAME (SolarpunkSteam-Win64-Shipping etc.),
//    not the launched PID — a SteamStub exe re-launches the real game as a new PID.
//  - "Loaded" is confirmed by the game's UE4SS.log advancing past inject time.
[SupportedOSPlatform("windows")]
public static class Ue4ssInjector
{
    private const uint PROCESS_CREATE_THREAD     = 0x0002;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_OPERATION      = 0x0008;
    private const uint PROCESS_VM_WRITE          = 0x0020;
    private const uint PROCESS_VM_READ           = 0x0010;
    private const uint INJECT_RIGHTS = PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION
                                     | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ;

    private const uint MEM_COMMIT  = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;

    // The Solarpunk process names to look for (Steam infix first, like the locator).
    private static readonly string[] GameProcessNames =
    {
        "SolarpunkSteam-Win64-Shipping", "Solarpunk-Win64-Shipping",
        "SolarpunkSteam-WinGDK-Shipping", "Solarpunk-WinGDK-Shipping",
    };

    public sealed record Result(bool Ok, string Detail);

    // Wait for the game process to appear, inject UE4SS.dll, and re-inject until the
    // UE4SS log confirms it loaded (mirrors the server's 3x retry-until-confirmed).
    public static Result InjectIntoGame(string ue4ssDllPath, string ue4ssLogPath, DateTime launchAtUtc)
    {
        if (!File.Exists(ue4ssDllPath))
            return new Result(false, $"UE4SS.dll not found at {ue4ssDllPath}");

        // 1. Wait for the real game process (Steam re-launch can take a while).
        var procWait = Stopwatch.StartNew();
        int pid = 0;
        while (procWait.Elapsed < TimeSpan.FromSeconds(60))
        {
            pid = FindGameProcessId();
            if (pid != 0) break;
            Thread.Sleep(1000);
        }
        if (pid == 0)
            return new Result(false, "Solarpunk process never appeared (could not inject UE4SS)");

        SolarpunkLog.Info($"Ue4ssInjector: game process pid={pid}; giving it a moment to initialize before injecting");
        // Let the game get past early init so LoadLibraryW + UE4SS hooking land cleanly.
        Thread.Sleep(5000);

        // 2. Inject + verify, up to 3 attempts (re-inject if UE4SS didn't come up).
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            // Re-resolve the pid each attempt — the process may have recycled.
            var curPid = FindGameProcessId();
            if (curPid == 0)
                return new Result(false, "Solarpunk process exited before UE4SS could be injected");
            pid = curPid;

            var injectAtUtc = DateTime.UtcNow;
            var (injected, detail) = InjectOnce(pid, ue4ssDllPath);
            SolarpunkLog.Info($"Ue4ssInjector: attempt {attempt} pid={pid} injected={injected} {detail}");
            if (!injected)
            {
                // OpenProcess access-denied usually means the game is elevated and the
                // launcher isn't — surface that clearly; otherwise retry.
                if (detail.Contains("access", StringComparison.OrdinalIgnoreCase))
                    return new Result(false, $"Injection blocked ({detail}). Run Solarpunk as administrator and reconnect.");
                Thread.Sleep(2000);
                continue;
            }

            // 3. Confirm UE4SS actually loaded: its log must advance past inject time.
            if (WaitForUe4ssLoaded(ue4ssLogPath, injectAtUtc, TimeSpan.FromSeconds(12)))
            {
                SolarpunkLog.Info($"Ue4ssInjector: UE4SS confirmed loaded (attempt {attempt})");
                return new Result(true, $"UE4SS injected + confirmed (attempt {attempt}, pid {pid})");
            }
            SolarpunkLog.Warn($"Ue4ssInjector: attempt {attempt} injected but UE4SS log did not advance — re-injecting");
        }

        return new Result(false, "UE4SS did not load after 3 inject attempts");
    }

    private static int FindGameProcessId()
    {
        foreach (var name in GameProcessNames)
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                if (procs.Length > 0) return procs[0].Id;
            }
            catch { /* ignore and continue */ }
        }
        return 0;
    }

    private static (bool ok, string detail) InjectOnce(int pid, string dllPath)
    {
        var hProcess = OpenProcess(INJECT_RIGHTS, false, pid);
        if (hProcess == IntPtr.Zero)
            return (false, $"OpenProcess failed (err {Marshal.GetLastWin32Error()}; likely access denied)");

        IntPtr remoteMem = IntPtr.Zero;
        try
        {
            var pathBytes = Encoding.Unicode.GetBytes(dllPath + "\0");
            remoteMem = VirtualAllocEx(hProcess, IntPtr.Zero, (uint)pathBytes.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remoteMem == IntPtr.Zero)
                return (false, $"VirtualAllocEx failed (err {Marshal.GetLastWin32Error()})");

            if (!WriteProcessMemory(hProcess, remoteMem, pathBytes, (uint)pathBytes.Length, out _))
                return (false, $"WriteProcessMemory failed (err {Marshal.GetLastWin32Error()})");

            var hKernel = GetModuleHandle("kernel32.dll");
            var loadLibraryW = GetProcAddress(hKernel, "LoadLibraryW");
            if (loadLibraryW == IntPtr.Zero)
                return (false, "GetProcAddress(LoadLibraryW) failed");

            var hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, loadLibraryW, remoteMem, 0, out var tid);
            if (hThread == IntPtr.Zero)
                return (false, $"CreateRemoteThread failed (err {Marshal.GetLastWin32Error()})");

            // Wait for LoadLibraryW to return (the DLL is loaded by the time it does).
            WaitForSingleObject(hThread, 10000);
            CloseHandle(hThread);
            return (true, $"LoadLibraryW thread {tid} issued");
        }
        finally
        {
            if (remoteMem != IntPtr.Zero) VirtualFreeEx(hProcess, remoteMem, 0, MEM_RELEASE);
            CloseHandle(hProcess);
        }
    }

    private static bool WaitForUe4ssLoaded(string ue4ssLogPath, DateTime injectAtUtc, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(ue4ssLogPath) && File.GetLastWriteTimeUtc(ue4ssLogPath) > injectAtUtc)
                    return true;
            }
            catch { /* file locked mid-write; try again */ }
            Thread.Sleep(500);
        }
        return false;
    }

    // ---- P/Invoke ----
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out int lpThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
