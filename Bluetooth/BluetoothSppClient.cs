using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus;

namespace XiaomiAstroBoxCSharp.Bluetooth;

public class BluetoothSppClient : IDisposable
{
    private const int AF_BLUETOOTH = 31;
    private const int BTPROTO_RFCOMM = 3;
    private const int SOCK_STREAM = 1;

    private Connection _dbus;
    private int _rfcommFd = -1; // File descriptor for RFCOMM socket
    private CancellationTokenSource _readCts;
    private bool _disposed;
    private readonly object _fdLock = new object(); // Lock for fd access

    private Func<byte[], string, Task> _dataListener;
    private Action _onConnect;
    private Action<string> _onDisconnect;
    private readonly ILogger<BluetoothSppClient> _logger;

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

    public void SetDataListener(Func<byte[], string, Task> listener) => _dataListener = listener;
    public BluetoothSppClient(ILogger<BluetoothSppClient> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void OnConnect(Action callback) => _onConnect = callback;
    public void OnDisconnect(Action<string> callback) => _onDisconnect = callback;

    public async Task ConnectAsync(string macAddress, byte channel = 1, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BluetoothSppClient));

        // Connect to system D-Bus
        _dbus = new Connection(Address.System);
        await _dbus.ConnectAsync();

        var macFormatted = macAddress.ToUpperInvariant().Replace(':', '_');
        var devicePath = new ObjectPath($"/org/bluez/hci0/dev_{macFormatted}");
        var adapterPath = new ObjectPath("/org/bluez/hci0");

        // Get proxies
        var adapter = _dbus.CreateProxy<IAdapter1>("org.bluez", adapterPath);
        var agentManager = _dbus.CreateProxy<IAgentManager1>("org.bluez", new ObjectPath("/org/bluez"));

        // Check if device exists, if not start discovery
        IDevice1 device = null;
        bool deviceFound = false;
        
        try
        {
            device = _dbus.CreateProxy<IDevice1>("org.bluez", devicePath);
            await device.GetAsync<string>("Address");
            deviceFound = true;
            _logger.LogInformation("Device already known to BlueZ");
        }
        catch
        {
            _logger.LogInformation("Device not found, starting discovery...");
            await adapter.StartDiscoveryAsync();

            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(500, ct);
                try
                {
                    device = _dbus.CreateProxy<IDevice1>("org.bluez", devicePath);
                    await device.GetAsync<string>("Address");
                    _logger.LogInformation("Device discovered after {Milliseconds}ms", (i + 1) * 500);
                    deviceFound = true;
                    break;
                }
                catch { }
            }

            try { await adapter.StopDiscoveryAsync(); } catch { }
        }
        
        if (!deviceFound || device == null)
            throw new InvalidOperationException($"Device {macAddress} not found after discovery");

        // Trust the device (do this BEFORE checking pairing, like Rust does)
        try
        {
            await device.SetAsync("Trusted", true);
            _logger.LogInformation("Device marked as trusted");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not set trusted");
        }

        // Check if paired
        bool paired = false;
        try { paired = await device.GetAsync<bool>("Paired"); } catch { }

        if (!paired)
        {
            _logger.LogInformation("Device not paired, registering agent and pairing...");

            // Register our auto-accept agent
            var agent = new AutoAcceptAgent();
            await _dbus.RegisterObjectAsync(agent);

            try
            {
                await agentManager.RegisterAgentAsync(AutoAcceptAgent.Path, "NoInputNoOutput");
                await agentManager.RequestDefaultAgentAsync(AutoAcceptAgent.Path);
                _logger.LogInformation("Agent registered successfully");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agent registration warning");
            }

            // Pair - this should trigger the pairing prompt on the watch
            try
            {
                _logger.LogInformation("Initiating pairing... Please accept on your watch!");
                await device.PairAsync();
                _logger.LogInformation("Pairing successful!");
            }
            catch (DBusException ex) when (ex.ErrorName == "org.bluez.Error.AlreadyExists")
            {
                _logger.LogInformation("Already paired");
            }
            catch (DBusException ex) when (ex.ErrorName == "org.bluez.Error.AuthenticationFailed")
            {
                throw new InvalidOperationException("Pairing was rejected or timed out. Please try again and accept the pairing on your watch.");
            }

            // Wait and verify pairing actually completed
            _logger.LogInformation("Verifying pairing status...");
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(500, ct);
                try
                {
                    paired = await device.GetAsync<bool>("Paired");
                    if (paired)
                    {
                        _logger.LogInformation("Pairing confirmed!");
                        break;
                    }
                }
                catch { }
            }

            if (!paired)
            {
                throw new InvalidOperationException("Pairing did not complete. Please ensure you accepted the pairing request on your watch.");
            }
        }
        else
        {
            _logger.LogInformation("Device already paired");
        }

        _logger.LogInformation("Waiting briefly before RFCOMM connection...");
        await Task.Delay(500, ct);

        // Connect RFCOMM socket with retries - try multiple channels like Rust does
        byte[] channelsToTry = channel == 5 ? [5, 1] : [channel, 5, 1];
        channelsToTry = [.. channelsToTry.Distinct()];

        Exception lastException = null;
        bool connected = false;

        foreach (byte ch in channelsToTry)
        {
            _logger.LogInformation("Attempting RFCOMM connection on channel {Channel}...", ch);

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await ConnectRfcommAsync(macAddress, ch, ct);
                    _logger.LogInformation("RFCOMM connected successfully on channel {Channel}!", ch);
                    // If ConnectRfcommAsync didn't throw, the connection succeeded
                    connected = true;
                    break;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    lastException = ex;
                    _logger.LogWarning("Attempt {Attempt}/3 on channel {Channel} refused", attempt, ch);
                    await Task.Delay(500, ct);
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    _logger.LogWarning(ex, "Attempt {Attempt}/3 on channel {Channel} failed", attempt, ch);
                    await Task.Delay(500, ct);
                }
            }

            if (connected) break;
        }

        if (!connected)
        {
            throw new InvalidOperationException($"Failed to connect RFCOMM on all channels", lastException);
        }

        _logger.LogInformation("RFCOMM connection established!");
        _onConnect?.Invoke();

        // Start read loop with a new cancellation token source
        // If ct can be cancelled, link them; otherwise create a new one
        _readCts = ct.CanBeCanceled 
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : new CancellationTokenSource();
        _ = ReadLoopAsync(_readCts.Token);
    }

    public async Task SendAsync(byte[] data, CancellationToken ct = default)
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
            while (totalWritten < data.Length)
            {
                ct.ThrowIfCancellationRequested();
                
                int bytesWritten = write(fd, data, data.Length);
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
        }, ct);
    }

    private async Task ConnectRfcommAsync(string macAddress, byte channel, CancellationToken ct)
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
            
            _logger.LogDebug("RFCOMM socket connected successfully, fd={FileDescriptor}", fd);
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

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[1024];

        try
        {
            _logger.LogDebug("Starting read loop...");
            
            // Run blocking read operations on background thread
            await Task.Run(() =>
            {
                while (!ct.IsCancellationRequested)
                {
                    int fd;
                    lock (_fdLock)
                    {
                        if (_rfcommFd < 0)
                        {
                            _logger.LogInformation("Socket closed, exiting read loop");
                            break;
                        }
                        fd = _rfcommFd;
                    }

                    // Synchronous read with syscall
                    int bytesRead = read(fd, buffer, buffer.Length);
                    
                    if (bytesRead < 0)
                    {
                        var errno = Marshal.GetLastWin32Error();
                        // EINTR (4) means interrupted by signal, retry
                        if (errno == 4)
                            continue;
                        
                        _logger.LogError("Read error: errno={ErrorNumber}", errno);
                        throw new IOException($"Failed to read from RFCOMM socket: errno={errno}");
                    }
                    
                    if (bytesRead == 0)
                    {
                        _logger.LogInformation("Connection closed by remote device");
                        break;
                    }
                    
                    
                    _logger.LogDebug("Read {BytesRead} bytes", bytesRead);

                    var data = new byte[bytesRead];
                    Array.Copy(buffer, data, bytesRead);

                    // Invoke data listener (using .Wait() since we're on background thread)
                    if (_dataListener != null)
                        _dataListener(data, null).Wait();
                }
            }, ct);
        }
        catch (OperationCanceledException) 
        {
            _logger.LogInformation("Read loop cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Read loop error");
            _dataListener?.Invoke([], ex.Message);
        }
        finally
        {
            _onDisconnect?.Invoke("Connection closed");
        }
    }

    private static byte[] MacToBytes(string mac)
    {
        var parts = mac.Split(':');
        var bytes = new byte[6];
        for (int i = 0; i < 6; i++)
            bytes[5 - i] = Convert.ToByte(parts[i], 16);
        return bytes;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _readCts?.Cancel();
        _readCts?.Dispose();
        
        lock (_fdLock)
        {
            if (_rfcommFd >= 0)
            {
                close(_rfcommFd);
                _rfcommFd = -1;
            }
        }
        
        _dbus?.Dispose();
    }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct SockAddrRc
    {
        public ushort Family;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] Address;
        public byte Channel;
    }
}
