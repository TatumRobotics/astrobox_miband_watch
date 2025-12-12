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
    private readonly BlueZDeviceManager _deviceManager;
    private readonly RfcommSocketManager _socketManager;
    private CancellationTokenSource _readCts;
    private bool _disposed;

    private Func<byte[], string, Task> _dataListener;
    private Action _onConnect;
    private Action<string> _onDisconnect;
    private readonly ILogger<BluetoothSppClient> _logger;

    public bool IsConnected => _socketManager.IsConnected;

    public void SetDataListener(Func<byte[], string, Task> listener) => _dataListener = listener;
    
    public BluetoothSppClient(ILogger<BluetoothSppClient> logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        var socketLogger = loggerFactory.CreateLogger<RfcommSocketManager>();
        _socketManager = new RfcommSocketManager(socketLogger);
        var deviceManagerLogger = loggerFactory.CreateLogger<BlueZDeviceManager>();
        _deviceManager = new BlueZDeviceManager(deviceManagerLogger);
    }

    public void OnConnect(Action callback) => _onConnect = callback;
    public void OnDisconnect(Action<string> callback) => _onDisconnect = callback;

    public async Task ConnectAsync(string macAddress, byte channel = 1, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BluetoothSppClient));

        // Initialize D-Bus and ensure device is ready (discovered, trusted, and paired)
        await _deviceManager.InitializeAsync();
        await _deviceManager.EnsureDeviceReadyAsync(macAddress, ct);

        // Connect RFCOMM socket with retries - try multiple channels like Rust does
        // byte[] channelsToTry = channel == 5 ? [5, 1] : [channel, 5, 1];
        // channelsToTry = [.. channelsToTry.Distinct()];

        // Exception lastException = null;
        // bool connected = false;

        // foreach (byte ch in channelsToTry)
        // {
        //     _logger.LogInformation("Attempting RFCOMM connection on channel {Channel}...", ch);

        //     for (int attempt = 1; attempt <= 3; attempt++)
        //     {
        //         try
        //         {
        //             await _socketManager.ConnectAsync(macAddress, ch, ct);
        //             _logger.LogInformation("RFCOMM connected successfully on channel {Channel}!", ch);
        //             connected = true;
        //             break;
        //         }
        //         catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        //         {
        //             lastException = ex;
        //             _logger.LogWarning("Attempt {Attempt}/3 on channel {Channel} refused", attempt, ch);
        //             await Task.Delay(500, ct);
        //         }
        //         catch (Exception ex)
        //         {
        //             lastException = ex;
        //             _logger.LogWarning(ex, "Attempt {Attempt}/3 on channel {Channel} failed", attempt, ch);
        //             await Task.Delay(500, ct);
        //         }
        //     }

        //     if (connected) break;
        // }

        // if (!connected)
        // {
        //     throw new InvalidOperationException($"Failed to connect RFCOMM on all channels", lastException);
        // }

        // _logger.LogInformation("RFCOMM connection established!");
        
        // Connect RFCOMM socket on specified channel
        _logger.LogInformation("Attempting RFCOMM connection on channel {Channel}...", channel);
        await _socketManager.ConnectAsync(macAddress, channel, ct);
        _logger.LogInformation("RFCOMM connection established on channel {Channel}!", channel);
        _onConnect?.Invoke();

        _readCts = ct.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : new CancellationTokenSource();
        _ = ReadLoopAsync(_readCts.Token);
    }

    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        await _socketManager.WriteAsync(data, ct);
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
                    if (!_socketManager.IsConnected)
                    {
                        _logger.LogInformation("Socket closed, exiting read loop");
                        break;
                    }

                    // Synchronous read with syscall
                    int bytesRead = _socketManager.Read(buffer);
                    
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _readCts?.Cancel();
        _readCts?.Dispose();
        
        _socketManager?.Dispose();
        _deviceManager?.Dispose();
    }
}
