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
    public readonly record struct ConnectionStatus(
        bool IsConnected,
        DateTimeOffset? LastRxUtc,
        DateTimeOffset? LastTxUtc)
    {
        public TimeSpan? TimeSinceLastRxUtc =>
            LastRxUtc.HasValue ? DateTimeOffset.UtcNow - LastRxUtc.Value : null;

        public TimeSpan? TimeSinceLastTxUtc =>
            LastTxUtc.HasValue ? DateTimeOffset.UtcNow - LastTxUtc.Value : null;
    }

    private readonly BlueZDeviceManager _deviceManager;
    private readonly RfcommSocketManager _socketManager;
    private CancellationTokenSource _readCts;
    private Task _readLoopTask;
    private bool _disposed;
    private int _disconnectSignaled;
    private long _lastRxTicksUtc;
    private long _lastTxTicksUtc;

    private Func<byte[], string, Task> _dataListener;
    private Action _onConnect;
    private Action<string> _onDisconnect;
    private readonly ILogger<BluetoothSppClient> _logger;

    public bool IsConnected => _socketManager.IsConnected;

    public void SetDataListener(Func<byte[], string, Task> listener) => _dataListener = listener;

    public ConnectionStatus GetConnectionStatus()
    {
        var rx = Interlocked.Read(ref _lastRxTicksUtc);
        var tx = Interlocked.Read(ref _lastTxTicksUtc);
        return new ConnectionStatus(
            IsConnected,
            rx == 0 ? null : new DateTimeOffset(new DateTime(rx, DateTimeKind.Utc)),
            tx == 0 ? null : new DateTimeOffset(new DateTime(tx, DateTimeKind.Utc)));
    }
    
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
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(BluetoothSppClient));
        }

        // New session: allow disconnect to be signaled once.
        Interlocked.Exchange(ref _disconnectSignaled, 0);

        // Initialize D-Bus and ensure device is ready (discovered, trusted, and paired)
        await _deviceManager.InitializeAsync();
        await _deviceManager.EnsureDeviceReadyAsync(macAddress, ct);

        // Connect RFCOMM socket on specified channel
        _logger.LogInformation("Attempting RFCOMM connection on channel {Channel}...", channel);
        await _socketManager.ConnectAsync(macAddress, channel, ct);
        _logger.LogInformation("RFCOMM connection established on channel {Channel}!", channel);

        // Reset timestamps at session start.
        Interlocked.Exchange(ref _lastRxTicksUtc, 0);
        Interlocked.Exchange(ref _lastTxTicksUtc, DateTime.UtcNow.Ticks);

        _onConnect?.Invoke();

        _readCts = ct.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : new CancellationTokenSource();
        _readLoopTask = ReadLoopAsync(_readCts.Token);
        _ = _readLoopTask.ContinueWith(
            t => _logger.LogError(t.Exception, "Read loop task faulted"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        await _socketManager.WriteAsync(data, ct);
        Interlocked.Exchange(ref _lastTxTicksUtc, DateTime.UtcNow.Ticks);
    }

    private Task ReadLoopAsync(CancellationToken ct)
    {
        // Run blocking RFCOMM reads on a background thread, but dispatch data to the listener asynchronously.
        return Task.Run(async () =>
        {
            var buffer = new byte[1024];
            try
            {
                _logger.LogDebug("Starting read loop...");

                while (!ct.IsCancellationRequested)
                {
                    if (!_socketManager.IsConnected)
                    {
                        _logger.LogInformation("Socket closed, exiting read loop");
                        SignalDisconnectOnce("Socket closed");
                        return;
                    }

                    // Synchronous read with syscall.
                    int bytesRead = _socketManager.Read(buffer);

                    if (bytesRead < 0)
                    {
                        var errno = Marshal.GetLastWin32Error();
                        // EINTR (4) means interrupted by signal, retry.
                        if (errno == 4)
                            continue;

                        _logger.LogError("Read error: errno={ErrorNumber}", errno);
                        throw new IOException($"Failed to read from RFCOMM socket: errno={errno}");
                    }

                    if (bytesRead == 0)
                    {
                        _logger.LogInformation("Connection closed by remote device");
                        SignalDisconnectOnce("Connection closed by remote device");
                        return;
                    }

                    _logger.LogDebug("Read {BytesRead} bytes", bytesRead);
                    Interlocked.Exchange(ref _lastRxTicksUtc, DateTime.UtcNow.Ticks);

                    var data = new byte[bytesRead];
                    Array.Copy(buffer, data, bytesRead);

                    await InvokeDataListenerAsync(data, error: null).ConfigureAwait(false);
                }

                _logger.LogInformation("Read loop cancelled");
                SignalDisconnectOnce("Read loop cancelled");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Read loop cancelled");
                SignalDisconnectOnce("Read loop cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Read loop error");
                await InvokeDataListenerAsync(Array.Empty<byte>(), ex.Message).ConfigureAwait(false);
                SignalDisconnectOnce(ex.Message ?? "Read loop error");
            }
        }, ct);
    }

    private void SignalDisconnectOnce(string reason)
    {
        if (Interlocked.Exchange(ref _disconnectSignaled, 1) != 0)
            return;
        _onDisconnect?.Invoke(reason);
    }

    private async Task InvokeDataListenerAsync(byte[] data, string error)
    {
        var listener = _dataListener;
        if (listener == null)
            return;
        try
        {
            await listener(data, error).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Data listener threw an exception");
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
