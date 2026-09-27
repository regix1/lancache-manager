using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LancacheManager.Infrastructure.Services;

internal static class NginxWriterProbe
{
    private const int SetLease = 1024;
    private const int SetOwner = 8;
    private const int ReadLease = 0;
    private const int UnlockLease = 2;
    private const int BlockSignal = 0;
    private const int IoSignal = 29;

    internal static bool CanCheckOnAction => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    internal static NginxFileIdentity ReadIdentity(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return ReadIdentity(stream.SafeFileHandle);
    }

    internal static bool TryAcquire(
        IReadOnlyList<string> paths,
        out IReadOnlyList<NginxHeldProof> proofs,
        out string? error)
    {
        var acquired = new List<NginxHeldProof>();
        try
        {
            foreach (var path in paths)
            {
                var canonicalPath = Path.GetFullPath(path);
                if (OperatingSystem.IsLinux())
                {
                    var lease = new LinuxLease(canonicalPath);
                    acquired.Add(new NginxHeldProof(
                        canonicalPath,
                        lease.Identity,
                        lease.Validate,
                        lease.Dispose));
                    continue;
                }

                var stream = OperatingSystem.IsWindows()
                    ? new FileStream(canonicalPath, FileMode.Open, FileAccess.Read,
                        FileShare.Read | FileShare.Delete)
                    : throw new PlatformNotSupportedException(
                        "No-writer proof is unavailable on this platform");
                try
                {
                    var identity = ReadIdentity(stream.SafeFileHandle);
                    if (OperatingSystem.IsWindows())
                    {
                        acquired.Add(new NginxHeldProof(
                            canonicalPath,
                            identity,
                            () => !stream.SafeFileHandle.IsClosed && !stream.SafeFileHandle.IsInvalid,
                            stream.Dispose));
                    }
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }

            proofs = acquired;
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            foreach (var proof in acquired)
            {
                proof.Dispose();
            }
            proofs = Array.Empty<NginxHeldProof>();
            error = ex.Message;
            return false;
        }
    }

    private sealed class LinuxLease : IDisposable
    {
        private readonly string _path;
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly Thread _thread;
        private Exception? _error;
        private nuint _nativeThread;
        private int _broken;
        private int _released;

        internal LinuxLease(string path)
        {
            _path = path;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "nginx-log-lease"
            };
            _thread.Start();
            _ready.Wait();
            if (_error is not null)
            {
                _thread.Join();
                throw new IOException(
                    $"Could not acquire a read lease for '{path}'",
                    _error);
            }
        }

        internal NginxFileIdentity Identity { get; private set; } = new(0, 0);

        internal bool Validate() =>
            Volatile.Read(ref _broken) == 0 &&
            Volatile.Read(ref _released) == 0 &&
            _thread.IsAlive;

        private void Run()
        {
            FileStream? stream = null;
            try
            {
                var signals = new LinuxSignalSet();
                CheckSignalResult(SigEmptySet(ref signals), "initialize the lease signal set");
                CheckSignalResult(SigAddSet(ref signals, IoSignal), "add the lease break signal");
                CheckSignalResult(
                    PthreadSigMask(BlockSignal, ref signals, IntPtr.Zero),
                    "block the lease break signal");
                _nativeThread = PthreadSelf();

                stream = new FileStream(
                    _path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var descriptor = stream.SafeFileHandle.DangerousGetHandle().ToInt32();
                if (Fcntl(descriptor, SetOwner, GetThreadId()) != 0)
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
                }
                Identity = ReadIdentity(stream.SafeFileHandle);
                if (Fcntl(descriptor, SetLease, ReadLease) != 0)
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
                }

                _ready.Set();
                CheckSignalResult(SigWait(ref signals, out _), "wait for the lease break signal");
                if (Volatile.Read(ref _released) == 0)
                {
                    Volatile.Write(ref _broken, 1);
                }
                var unlockResult = Fcntl(descriptor, SetLease, UnlockLease);
                GC.KeepAlive(unlockResult);
            }
            catch (Exception error)
            {
                _error = error;
                Volatile.Write(ref _broken, 1);
            }
            finally
            {
                stream?.Dispose();
                _ready.Set();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            if (_thread.IsAlive && _nativeThread != 0)
            {
                var signalResult = PthreadKill(_nativeThread, IoSignal);
                if (signalResult != 0)
                {
                    throw new IOException(
                        "Could not release the lease signal wait",
                        new System.ComponentModel.Win32Exception(signalResult));
                }
            }
            _thread.Join();
            _ready.Dispose();
        }

        private static void CheckSignalResult(int result, string action)
        {
            if (result != 0)
            {
                throw new IOException(
                    $"Could not {action}",
                    new System.ComponentModel.Win32Exception(result));
            }
        }
    }

    private static NginxFileIdentity ReadIdentity(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var information))
            {
                throw new IOException(
                    "Could not read Windows file identity",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
            }

            return new NginxFileIdentity(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
        }

        if (OperatingSystem.IsLinux())
        {
            var descriptor = handle.DangerousGetHandle().ToInt32();
            if (Fstat(descriptor, out var status) != 0)
            {
                throw new IOException(
                    "Could not read Linux file identity",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
            }

            return new NginxFileIdentity(status.Device, status.Inode);
        }

        throw new PlatformNotSupportedException("File identity is unavailable on this platform");
    }

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int descriptor, int command, int argument);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int Fstat(int descriptor, out LinuxStat status);

    [DllImport("libc", EntryPoint = "gettid")]
    private static extern int GetThreadId();

    [DllImport("libc", EntryPoint = "pthread_self")]
    private static extern nuint PthreadSelf();

    [DllImport("libc", EntryPoint = "pthread_kill")]
    private static extern int PthreadKill(nuint thread, int signal);

    [DllImport("libc", EntryPoint = "pthread_sigmask")]
    private static extern int PthreadSigMask(
        int how,
        ref LinuxSignalSet set,
        IntPtr oldSet);

    [DllImport("libc", EntryPoint = "sigemptyset")]
    private static extern int SigEmptySet(ref LinuxSignalSet set);

    [DllImport("libc", EntryPoint = "sigaddset")]
    private static extern int SigAddSet(ref LinuxSignalSet set, int signal);

    [DllImport("libc", EntryPoint = "sigwait")]
    private static extern int SigWait(ref LinuxSignalSet set, out int signal);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal long CreationTime;
        internal long LastAccessTime;
        internal long LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStat
    {
        internal ulong Device;
        internal ulong Inode;
        internal ulong LinkCount;
        internal uint Mode;
        internal uint UserId;
        internal uint GroupId;
        internal int Padding;
        internal ulong RawDevice;
        internal long Size;
        internal long BlockSize;
        internal long Blocks;
        internal LinuxTime AccessTime;
        internal LinuxTime ModificationTime;
        internal LinuxTime ChangeTime;
        internal long Reserved0;
        internal long Reserved1;
        internal long Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxTime
    {
        internal long Seconds;
        internal long Nanoseconds;
    }

    [StructLayout(LayoutKind.Sequential, Size = 128)]
    private struct LinuxSignalSet
    {
    }
}
