using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace XiaomiAstroBoxCSharp.Bluetooth;

/// <summary>
/// Manages low-level RFCOMM socket operations. RFCOMM emulates serial port connections over Bluetooth.
/// </summary>
public class RfcommSocketManager(ILogger<RfcommSocketManager> logger) : IDisposable
{
    // This is the ID for bluetooth communication on Linux
    // Other IDs are for IPv4, IPv6, etc.
    private const int AF_BLUETOOTH = 31;

    // This is the ID for RFCOMM protocol
    // Other IDs are for L2CAP, SCO, or other Bluetooth protocols
    private const int BTPROTO_RFCOMM = 3;
    
    // This is the ID for stream sockets
    // Stream sockets are used for reliable two way connections (like TCP)
    private const int SOCK_STREAM = 1;

    // id for the RFCOMM socket file descriptor on Linux
    private int _rfcommFd = -1;
    private readonly object _fdLock = new object();
    private bool _disposed;

    public bool IsConnected
    {
        get
        {
            lock (_fdLock)
            {
                return _rfcommFd >= 0;
            }
        }
    }

    public async Task ConnectAsync(string macAddress, byte channel, CancellationToken ct)
    {
        // Clean up any existing socket
        lock (_fdLock)
        {
            if (_rfcommFd >= 0)
            {
                _ = close(_rfcommFd);
                _rfcommFd = -1;
            }
        }
        
        int fd = socket(AF_BLUETOOTH, SOCK_STREAM, BTPROTO_RFCOMM);
        if (fd < 0)
        {
            throw new SocketException(Marshal.GetLastWin32Error());
        }

        var addr = new SockAddrRc
        {
            Family = AF_BLUETOOTH,
            Channel = channel,
            Address = MacToBytes(macAddress)
        };

        var addrPtr = Marshal.AllocHGlobal(Marshal.SizeOf<SockAddrRc>());
        try
        {
            Marshal.StructureToPtr(addr, addrPtr, false);

            // Connect synchronously (blocking call) - run on thread pool
            await Task.Run(() =>
            {
                int result = connect(fd, addrPtr, Marshal.SizeOf<SockAddrRc>());
                if (result < 0)
                {
                    var errno = Marshal.GetLastWin32Error();
                    _ = close(fd);
                    throw new SocketException(errno);
                }
            }, ct);
            
            // Store file descriptor
            lock (_fdLock)
            {
                _rfcommFd = fd;
            }
            
            logger.LogDebug("RFCOMM socket connected successfully, fd={FileDescriptor}", fd);
        }
        catch
        {
            if (fd >= 0)
            {
                _ = close(fd);
            }
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(addrPtr);
        }
    }

    public async Task WriteAsync(byte[] data, CancellationToken ct)
    {
        int fd;
        lock (_fdLock)
        {
            if (_rfcommFd < 0)
                throw new InvalidOperationException("Not connected");
            fd = _rfcommFd;
        }

        // Run synchronous write on thread pool to avoid blocking
        await Task.Run(() =>
        {
            int totalWritten = 0;
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (data.Length == 0)
                return;

            // Pin once and write slices to correctly handle partial writes.
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var basePtr = handle.AddrOfPinnedObject();
            while (totalWritten < data.Length)
            {
                ct.ThrowIfCancellationRequested();
                
                    var remaining = data.Length - totalWritten;
                    int bytesWritten = write_ptr(fd, IntPtr.Add(basePtr, totalWritten), remaining);
                if (bytesWritten < 0)
                {
                    var errno = Marshal.GetLastWin32Error();
                    throw new IOException($"Failed to write to RFCOMM socket: errno={errno}");
                }
                if (bytesWritten == 0)
                {
                    throw new IOException("Socket closed during write");
                }
                totalWritten += bytesWritten;
            }
            }
            finally
            {
                handle.Free();
            }
        }, ct);
    }

    /// Reads data from the RFCOMM socket. Returns number of bytes read.
    /// Returns 0 if connection closed, negative on error.
    public int Read(byte[] buffer)
    {
        int fd;
        lock (_fdLock)
        {
            if (_rfcommFd < 0)
            {
                return -1;
            }
            fd = _rfcommFd;
        }

        return read(fd, buffer, buffer.Length);
    }

    public void Close()
    {
        lock (_fdLock)
        {
            if (_rfcommFd >= 0)
            {
                close(_rfcommFd);
                _rfcommFd = -1;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        Close();
    }

    private static byte[] MacToBytes(string mac)
    {
        var parts = mac.Split(':');
        var bytes = new byte[6];
        for (int i = 0; i < 6; i++)
            bytes[5 - i] = Convert.ToByte(parts[i], 16);
        return bytes;
    }

    #region P/Invoke Declarations

    [DllImport("libc", SetLastError = true)]
    private static extern int socket(int domain, int type, int protocol);

    [DllImport("libc", SetLastError = true)]
    private static extern int connect(int sockfd, nint addr, int addrlen);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int read(int fd, byte[] buf, int count);

    [DllImport("libc", SetLastError = true)]
    private static extern int write(int fd, byte[] buf, int count);

    // Pointer-based write to support offsets (partial writes are common for sockets).
    [DllImport("libc", SetLastError = true, EntryPoint = "write")]
    private static extern int write_ptr(int fd, IntPtr buf, int count);

    [StructLayout(LayoutKind.Sequential)]
    private struct SockAddrRc
    {
        public ushort Family;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] Address;
        public byte Channel;
    }

    #endregion
}
