using System.Runtime.InteropServices;

namespace Solarpunk.Launcher;

internal enum WindowsIntegrityLevel
{
    Unknown = 0,
    Low,
    Medium,
    High,
    System,
}

internal sealed record WindowsProcessAccessInfo(
    int Pid,
    string ImageName,
    bool FridaQueryAllowed,
    int? FridaQueryError,
    WindowsIntegrityLevel IntegrityLevel)
{
    public string Describe()
    {
        var access = FridaQueryAllowed ? "frida-query=allowed" : $"frida-query=denied({FridaQueryError})";
        return $"{ImageName} pid={Pid} {access} integrity={IntegrityLevel}";
    }
}

internal static class WindowsProcessInspector
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint TOKEN_QUERY = 0x0008;
    private const int ERROR_ACCESS_DENIED = 5;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static bool IsCurrentProcessElevated()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            return TryGetIntegrityLevel(GetCurrentProcess()) is
                WindowsIntegrityLevel.High or WindowsIntegrityLevel.System;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsProcessRunningByImageName(string imageName) =>
        FindByImageName(imageName).Count > 0;

    public static IReadOnlyList<WindowsProcessAccessInfo> FindByImageName(string imageName)
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<WindowsProcessAccessInfo>();

        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidHandleValue)
            return Array.Empty<WindowsProcessAccessInfo>();

        try
        {
            var matches = new List<WindowsProcessAccessInfo>();
            var entry = new PROCESSENTRY32
            {
                dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>(),
            };

            if (!Process32First(snapshot, ref entry))
                return matches;

            do
            {
                if (ImageNameEquals(entry.szExeFile, imageName))
                    matches.Add(Inspect(entry.th32ProcessID, entry.szExeFile));

                entry.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>();
            }
            while (Process32Next(snapshot, ref entry));

            return matches;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    public static bool TryFindElevationProblem(
        string imageName,
        out WindowsProcessAccessInfo? process,
        out string reason)
    {
        process = null;
        reason = "";

        if (!OperatingSystem.IsWindows() || IsCurrentProcessElevated())
            return false;

        foreach (var candidate in FindByImageName(imageName))
        {
            var accessDenied = !candidate.FridaQueryAllowed && candidate.FridaQueryError == ERROR_ACCESS_DENIED;
            var elevatedIntegrity = candidate.IntegrityLevel is WindowsIntegrityLevel.High or WindowsIntegrityLevel.System;
            if (!accessDenied && !elevatedIntegrity)
                continue;

            process = candidate;
            reason = accessDenied
                ? $"{candidate.ImageName} is denying the same PROCESS_QUERY_INFORMATION access Frida needs"
                : $"{candidate.ImageName} is running at {candidate.IntegrityLevel} integrity";
            return true;
        }

        return false;
    }

    private static WindowsProcessAccessInfo Inspect(uint pid, string imageName)
    {
        var process = OpenProcess(PROCESS_QUERY_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
        {
            return new WindowsProcessAccessInfo(
                checked((int)pid),
                imageName,
                FridaQueryAllowed: false,
                FridaQueryError: Marshal.GetLastWin32Error(),
                IntegrityLevel: WindowsIntegrityLevel.Unknown);
        }

        try
        {
            return new WindowsProcessAccessInfo(
                checked((int)pid),
                imageName,
                FridaQueryAllowed: true,
                FridaQueryError: null,
                IntegrityLevel: TryGetIntegrityLevel(process));
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static WindowsIntegrityLevel TryGetIntegrityLevel(IntPtr process)
    {
        if (!OpenProcessToken(process, TOKEN_QUERY, out var token))
            return WindowsIntegrityLevel.Unknown;

        try
        {
            _ = GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, IntPtr.Zero, 0, out var needed);
            if (needed == 0)
                return WindowsIntegrityLevel.Unknown;

            var buffer = Marshal.AllocHGlobal(checked((int)needed));
            try
            {
                if (!GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, buffer, needed, out _))
                    return WindowsIntegrityLevel.Unknown;

                var label = Marshal.PtrToStructure<TOKEN_MANDATORY_LABEL>(buffer);
                var sid = label.Label.Sid;
                var countPtr = GetSidSubAuthorityCount(sid);
                if (countPtr == IntPtr.Zero)
                    return WindowsIntegrityLevel.Unknown;

                var count = Marshal.ReadByte(countPtr);
                if (count == 0)
                    return WindowsIntegrityLevel.Unknown;

                var ridPtr = GetSidSubAuthority(sid, count - 1);
                if (ridPtr == IntPtr.Zero)
                    return WindowsIntegrityLevel.Unknown;

                var rid = (uint)Marshal.ReadInt32(ridPtr);
                return rid switch
                {
                    >= 0x4000 => WindowsIntegrityLevel.System,
                    >= 0x3000 => WindowsIntegrityLevel.High,
                    >= 0x2000 => WindowsIntegrityLevel.Medium,
                    >= 0x1000 => WindowsIntegrityLevel.Low,
                    _ => WindowsIntegrityLevel.Unknown,
                };
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static bool ImageNameEquals(string actual, string expected)
    {
        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            return true;

        var actualNoExt = Path.GetFileNameWithoutExtension(actual);
        var expectedNoExt = Path.GetFileNameWithoutExtension(expected);
        return string.Equals(actualNoExt, expectedNoExt, StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    // CharSet=Unicode forces the loader to dispatch to Process32FirstW /
    // Process32NextW. Without it .NET defaults to the ANSI variants which
    // populate szExeFile with ANSI bytes — but the PROCESSENTRY32 struct
    // is marshaled as Unicode, so the field comes back as garbage and
    // every image-name comparison silently fails. Symptom: launcher's
    // _gameRunning stayed false even with the game alive in the
    // process list; Close the game button never appeared.
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr TokenHandle,
        TOKEN_INFORMATION_CLASS TokenInformationClass,
        IntPtr TokenInformation,
        uint TokenInformationLength,
        out uint ReturnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr pSid, int nSubAuthority);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr pSid);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SID_AND_ATTRIBUTES
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_MANDATORY_LABEL
    {
        public SID_AND_ATTRIBUTES Label;
    }

    private enum TOKEN_INFORMATION_CLASS
    {
        TokenIntegrityLevel = 25,
    }
}
