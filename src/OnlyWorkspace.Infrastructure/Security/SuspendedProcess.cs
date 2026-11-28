using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OnlyWorkspace.Infrastructure.Security;

/// <summary>Owns a suspended Windows child and its three redirected handles. No user code runs before job assignment.</summary>
internal sealed class SuspendedProcess : IDisposable
{
    private SafeFileHandle? _process;
    private SafeFileHandle? _thread;
    private JobObject? _job;
    public Process Process { get; private set; } = null!;
    public StreamReader Output { get; private set; } = null!;
    public StreamReader Error { get; private set; } = null!;

    public static SuspendedProcess Start(string exe, string arguments, bool lowPriority, long memoryLimitBytes)
    {
        var child = new SuspendedProcess();
        IntPtr attributes = IntPtr.Zero, handles = IntPtr.Zero;
        bool attributesInitialized = false;
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), InheritHandle = true };
            Check(CreatePipe(out var outputRead, out var outputWrite, ref security, 0));
            using (outputRead)
            using (outputWrite)
            {
                Check(CreatePipe(out var errorRead, out var errorWrite, ref security, 0));
                using (errorRead)
                using (errorWrite)
                {
                    Check(CreatePipe(out var inputRead, out var inputWrite, ref security, 0));
                    using (inputRead)
                    using (inputWrite)
                    {
                        Check(SetHandleInformation(outputRead, 1, 0));
                        Check(SetHandleInformation(errorRead, 1, 0));
                        Check(SetHandleInformation(inputWrite, 1, 0));
                        IntPtr size = IntPtr.Zero;
                        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                        attributes = Marshal.AllocHGlobal(size);
                        Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
                        attributesInitialized = true;
                        handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
                        Marshal.Copy(new[] { inputRead.DangerousGetHandle(), outputWrite.DangerousGetHandle(), errorWrite.DangerousGetHandle() }, 0, handles, 3);
                        Check(UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x20002), handles, new IntPtr(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero));
                        var startup = new StartupInfoEx { Attributes = attributes };
                        startup.Startup.Size = Marshal.SizeOf(typeof(StartupInfoEx));
                        startup.Startup.Flags = 0x100;
                        startup.Startup.Input = inputRead.DangerousGetHandle();
                        startup.Startup.Output = outputWrite.DangerousGetHandle();
                        startup.Startup.Error = errorWrite.DangerousGetHandle();
                        string application = ResolveExecutable(exe);
                        Check(CreateProcess(application, new StringBuilder("\"" + application + "\" " + arguments), IntPtr.Zero, IntPtr.Zero,
                            true, 0x08080004u | (lowPriority ? 0x4000u : 0), IntPtr.Zero, null, ref startup, out var info));
                        child._process = new SafeFileHandle(info.Process, true);
                        child._thread = new SafeFileHandle(info.Thread, true);
                        child._job = JobObject.Assign(info.Process, memoryLimitBytes);
                        child.Process = Process.GetProcessById((int)info.ProcessId);
                        // Readers take ownership of duplicated parent handles; the local handles are always closed.
                        child.Output = Reader(outputRead, Encoding.UTF8);
                        child.Error = Reader(errorRead, Console.OutputEncoding);
                    }
                }
            }
            return child;
        }
        catch { child.Dispose(); throw; }
        finally
        {
            if (attributes != IntPtr.Zero) { if (attributesInitialized) DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
        }
    }

    public void Resume()
    {
        if (ResumeThread(_thread!) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
        _thread!.Dispose();
    }

    public void Terminate()
    {
        // Closing the job kills the whole tree, including a descendant holding stdout after the parent's exit.
        _job?.Dispose();
        if (_process != null && !_process.IsInvalid && !_process.IsClosed) TerminateProcess(_process, 1);
    }

    public void Dispose()
    {
        Terminate();
        Output?.Dispose();
        Error?.Dispose();
        Process?.Dispose();
        _thread?.Dispose();
        _process?.Dispose();
    }

    private static StreamReader Reader(SafeFileHandle source, Encoding encoding)
    {
        Check(DuplicateHandle(GetCurrentProcess(), source, GetCurrentProcess(), out var copy, 0, false, 2));
        FileStream? stream = null;
        try
        {
            stream = new FileStream(copy, FileAccess.Read, 4096, false);
            return new StreamReader(stream, encoding, true);
        }
        catch { if (stream != null) stream.Dispose(); else copy.Dispose(); throw; }
    }

    private static string ResolveExecutable(string exe)
    {
        if (Path.IsPathRooted(exe) || exe.IndexOfAny(new[] { '\\', '/' }) >= 0) return Path.GetFullPath(exe);
        var path = new StringBuilder(32768);
        uint length = SearchPath(null, exe, ".exe", path.Capacity, path, IntPtr.Zero);
        if (length == 0 || length >= path.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        return path.ToString();
    }

    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, int flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW", SetLastError = true)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeFileHandle process, uint code);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeFileHandle source, IntPtr targetProcess, out SafeFileHandle copy, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "SearchPathW", SetLastError = true)] private static extern uint SearchPath(string? path, string file, string extension, int size, StringBuilder buffer, IntPtr filePart);
}
