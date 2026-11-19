using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace RigControl.Core;

/// <summary>Kill-on-close job: the programs in it end with RigControl, even if it crashes.</summary>
sealed class ChildJob : IDisposable
{
    readonly IntPtr _handle = CreateJobObject(IntPtr.Zero, null);

    public ChildJob()
    {
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        int size = Marshal.SizeOf(info);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(_handle, 9, ptr, (uint)size)) throw new Win32Exception();
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public void Add(Process p) => Add(p.Handle, p.ProcessName);

    public void Add(IntPtr process, string what)
    {
        if (!AssignProcessToJobObject(_handle, process)) AppConfig.Log?.Invoke($"couldn't tie {what} to RigControl ({Marshal.GetLastWin32Error()})");
    }

    public void Dispose() => CloseHandle(_handle);

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr a, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}

/// <summary>A helper program RigControl started, with its standard input, output and error.</summary>
sealed class ChildProcess(Process process, StreamWriter input, StreamReader output, StreamReader error) : IDisposable
{
    public Process Process { get; } = process;
    public StreamWriter Input { get; } = input;
    public StreamReader Output { get; } = output;
    public StreamReader Error { get; } = error;

    public void Dispose()
    {
        try { if (!Process.HasExited) Process.Kill(); } catch { }
        foreach (var s in new IDisposable[] { Input, Output, Error, Process }) try { s.Dispose(); } catch { }
    }

    /// <summary>Starts a program with RigControl's own rights (administrator), tied to <paramref name="job"/>.</summary>
    public static ChildProcess StartAsAdmin(string exe, IEnumerable<string> args, string folder, ChildJob job)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = folder,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        job.Add(p);
        return new ChildProcess(p, p.StandardInput, p.StandardOutput, p.StandardError);
    }

    /// <summary>
    /// Starts a program with an ordinary user's rights, though RigControl runs as administrator: its token is
    /// RigControl's own with the administrator group switched off (LUA_TOKEN, the way Windows makes a standard
    /// user's token), every privilege removed, at medium integrity. It inherits only its three pipe ends, and is tied
    /// to <paramref name="job"/> before it runs a single instruction (it starts suspended).
    /// </summary>
    public static ChildProcess StartWithUserRights(string exe, IEnumerable<string> args, string folder, ChildJob job)
    {
        var stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        IntPtr own = IntPtr.Zero, user = IntPtr.Zero, medium = IntPtr.Zero, attributes = IntPtr.Zero, handles = IntPtr.Zero;
        var pi = default(PROCESS_INFORMATION);
        try
        {
            // TOKEN_ADJUST_DEFAULT: the restricted token gets this handle's rights, and its integrity is set below
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_DEFAULT, out own)) throw new Win32Exception();
            if (!CreateRestrictedToken(own, DISABLE_MAX_PRIVILEGE | LUA_TOKEN, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, out user)) throw new Win32Exception();
            if (!ConvertStringSidToSid("S-1-16-8192", out medium)) throw new Win32Exception(); // Medium Mandatory Level
            var label = new TOKEN_MANDATORY_LABEL { Label = new SID_AND_ATTRIBUTES { Sid = medium, Attributes = SE_GROUP_INTEGRITY } };
            if (!SetTokenInformation(user, TokenIntegrityLevel, ref label, (uint)(Marshal.SizeOf<TOKEN_MANDATORY_LABEL>() + GetLengthSid(medium)))) throw new Win32Exception();

            // Only these handles are inherited, whatever else in RigControl happens to be inheritable at this moment.
            IntPtr[] inherit = [stdin.ClientSafePipeHandle.DangerousGetHandle(), stdout.ClientSafePipeHandle.DangerousGetHandle(), stderr.ClientSafePipeHandle.DangerousGetHandle()];
            handles = Marshal.AllocHGlobal(IntPtr.Size * inherit.Length);
            Marshal.Copy(inherit, 0, handles, inherit.Length);
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, (IntPtr)(IntPtr.Size * inherit.Length), IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();

            var si = new STARTUPINFOEX { lpAttributeList = attributes };
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            (si.StartupInfo.hStdInput, si.StartupInfo.hStdOutput, si.StartupInfo.hStdError) = (inherit[0], inherit[1], inherit[2]);
            var line = new StringBuilder(Quote(exe));
            foreach (var a in args) line.Append(' ').Append(Quote(a));
            if (!CreateProcessAsUser(user, exe, line, IntPtr.Zero, IntPtr.Zero, true, EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW | CREATE_SUSPENDED,
                    IntPtr.Zero, folder, ref si, out pi)) throw new Win32Exception();

            job.Add(pi.hProcess, Path.GetFileName(exe));
            var process = Process.GetProcessById(pi.dwProcessId); // still suspended: it can't have exited yet
            _ = process.Handle; // held from now on, so its exit code can be read however soon it ends
            ResumeThread(pi.hThread);
            stdin.DisposeLocalCopyOfClientHandle();
            stdout.DisposeLocalCopyOfClientHandle();
            stderr.DisposeLocalCopyOfClientHandle();
            return new ChildProcess(process,
                new StreamWriter(stdin, new UTF8Encoding(false)) { AutoFlush = true },
                new StreamReader(stdout, Encoding.UTF8), new StreamReader(stderr, Encoding.UTF8));
        }
        catch
        {
            if (pi.hProcess != IntPtr.Zero) TerminateProcess(pi.hProcess, 1);
            stdin.Dispose(); stdout.Dispose(); stderr.Dispose();
            throw;
        }
        finally
        {
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if (medium != IntPtr.Zero) LocalFree(medium);
            if (user != IntPtr.Zero) CloseHandle(user);
            if (own != IntPtr.Zero) CloseHandle(own);
        }
    }

    /// <summary>A process's integrity level (0x2000 medium, 0x3000 high: elevated), or null when it can't be read.</summary>
    public static int? IntegrityLevel(Process p)
    {
        if (!OpenProcessToken(p.Handle, TOKEN_QUERY, out var token)) return null;
        try
        {
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var length);
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out _)) return null;
                var sid = Marshal.PtrToStructure<TOKEN_MANDATORY_LABEL>(buffer).Label.Sid;
                return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(Marshal.ReadByte(GetSidSubAuthorityCount(sid)) - 1)));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(token); }
    }

    /// <summary>One argument as Windows' command-line parser reads it back.</summary>
    static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) return arg;
        var sb = new StringBuilder("\"");
        for (int i = 0; i < arg.Length; i++)
        {
            int slashes = 0;
            while (i < arg.Length && arg[i] == '\\') { slashes++; i++; }
            if (i == arg.Length) { sb.Append('\\', slashes * 2); break; }
            if (arg[i] == '"') sb.Append('\\', slashes * 2 + 1).Append('"');
            else sb.Append('\\', slashes).Append(arg[i]);
        }
        return sb.Append('"').ToString();
    }

    const uint TOKEN_ASSIGN_PRIMARY = 0x1, TOKEN_DUPLICATE = 0x2, TOKEN_QUERY = 0x8, TOKEN_ADJUST_DEFAULT = 0x80;
    const uint DISABLE_MAX_PRIVILEGE = 0x1, LUA_TOKEN = 0x4;
    const int TokenIntegrityLevel = 25;
    const uint SE_GROUP_INTEGRITY = 0x20;
    const int PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x20002;
    const uint EXTENDED_STARTUPINFO_PRESENT = 0x80000, CREATE_NO_WINDOW = 0x08000000, CREATE_SUSPENDED = 0x4;
    const int STARTF_USESTDHANDLES = 0x100;

    [StructLayout(LayoutKind.Sequential)] struct SID_AND_ATTRIBUTES { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct TOKEN_MANDATORY_LABEL { public SID_AND_ATTRIBUTES Label; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)] struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool CreateRestrictedToken(IntPtr existing, uint flags, uint disableCount, IntPtr disable, uint deleteCount, IntPtr delete, uint restrictCount, IntPtr restrict, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool ConvertStringSidToSid(string sid, out IntPtr psid);
    [DllImport("advapi32.dll")] static extern int GetLengthSid(IntPtr sid);
    [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr token, int cls, ref TOKEN_MANDATORY_LABEL info, uint length);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int length, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUser(IntPtr token, string app, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inherit,
        uint flags, IntPtr environment, string folder, ref STARTUPINFOEX startup, out PROCESS_INFORMATION info);
    [DllImport("kernel32.dll")] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr mem);
}
