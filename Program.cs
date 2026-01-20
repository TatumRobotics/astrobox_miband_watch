using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Collections.Concurrent;
using Protocol;
using XiaomiAstroBoxCSharp.Bluetooth;
using XiaomiAstroBoxCSharp.Device;

namespace XiaomiAstroBoxCSharp;

// Entry point for the Raspberry Pi app that connects to a Xiaomi Smart Band 10,
// authenticates, and offers a simple console-driven test loop.
class Program()
{
    private static ILogger<Program> logger;
    private static readonly ConcurrentQueue<byte> PendingAckQueue = new();
    private static int AckSuppressCount = 0;
    // Single global input channel so disconnected sessions don't eat user input.
    private static readonly Channel<string> InputChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = true
    });

    // Simple console REPL so you can manually exercise band commands/patterns.
    private static async Task RunTestingLoopAsync(CancellationTokenSource cts, XiaomiBand10 device, Config config)
    {
        while (!cts.Token.IsCancellationRequested)
        {
            string input;
            try
            {
                // Read from a single, global console pump. This makes the read cancellable
                // and prevents a canceled session from consuming the first command after reconnect.
                input = (await InputChannel.Reader.ReadAsync(cts.Token)).Trim();
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (cts.Token.IsCancellationRequested)
                break;

            if (string.IsNullOrEmpty(input))
                continue;

            using (BeginOutputScope())
            {
                if (config.Patterns.TryGetValue(input, out var pattern))
                {
                    logger.LogInformation("Playing pattern: {Pattern}", input);
                    await device.VibrateAsync(pattern, cts.Token);
                    continue;
                }

                switch (input) {
                    case "ping":
                        var btStatus = device.GetBluetoothConnectionStatus();
                        var protocolOk = await device.PingAsync(protocolPing: true, timeoutSeconds: 2, ct: cts.Token);
                        logger.LogInformation(
                            "Ping: bt_connected={Connected}, protocol_ok={ProtocolOk}, since_rx={SinceRx}, since_tx={SinceTx}",
                            btStatus.IsConnected,
                            protocolOk,
                            btStatus.TimeSinceLastRxUtc?.ToString() ?? "n/a",
                            btStatus.TimeSinceLastTxUtc?.ToString() ?? "n/a");
                        break;
                    case "battery":
                        var status = await device.RequestBatteryStatusAsync(cts.Token, timeoutSeconds: 10);
                        logger.LogInformation("Battery: {Percent}% (status: {Status})", status.Percent, status.ChargeStatusText);
                        break;
                    case "wearing":
                        var isWearingWatch = await device.RequestIsWearingWatchAsync(cts.Token);
                        logger.LogInformation("Wearing the watch? {Wearing}.", isWearingWatch);
                        break;
                    case "clock":
                        logger.LogInformation("Set clock on watch to local system time");
                        var now = DateTime.Now;
                        var tz = TimeZoneInfo.Local;
                        var is12h = !System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains("H");
                        await device.SetWatchTimeAsync(now, tz, is12h, cts.Token);
                        break;
                    default:
                        logger.LogWarning("Command not found: {Input}", input);
                        break;
                }
            }
        }
    }

    private sealed class OutputScope : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (Interlocked.Decrement(ref AckSuppressCount) == 0)
            {
                FlushPendingAcks();
            }
        }
    }

    private static IDisposable BeginOutputScope()
    {
        Interlocked.Increment(ref AckSuppressCount);
        return new OutputScope();
    }

    private static void FlushPendingAcks()
    {
        while (PendingAckQueue.TryDequeue(out var seq))
        {
            Console.WriteLine($"ACK received for sequence {seq}!");
        }
    }

    // Handles the initial auth handshake and sets up helpers (ACK handler, clock sync).
    private static async Task AuthenticateAsync(CancellationTokenSource cts, XiaomiBand10 device, Config config)
    {

        logger.LogInformation("Authenticating with device...");
        var authenticated = await device.AuthenticateAsync();

        if (!authenticated)
        {
            logger.LogError("Failed to authenticate with device!");
            Environment.Exit(1);
            return;
        }

        logger.LogInformation("Device authenticated successfully!");

        // Setup vibration acknoledgement handler
        device.OnAckReceived(sequence =>
        {
            if (Interlocked.CompareExchange(ref AckSuppressCount, 0, 0) != 0)
            {
                PendingAckQueue.Enqueue(sequence);
                return;
            }
            Console.WriteLine($"ACK received for sequence {sequence}!");
        });

        device.OnVibratorErrorReceived(error =>
        {
            if (error.Code == VibratorError.Types.Code.Ok)
            {
                return;
            }
            using (BeginOutputScope())
            {
                logger.LogWarning("Vibrator error reported: {Code}", error.Code);
            }
        });

        logger.LogInformation("Available patterns: {Patterns}", string.Join(", ", config.Patterns.Keys));

        // Update watch time to match RPi local time/zone after auth
        var now = DateTime.Now;
        var tz = TimeZoneInfo.Local;
        var is12h = !System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains("H");
        await device.SetWatchTimeAsync(now, tz, is12h, cts.Token);
        // it might also be good to periodically update the watch time (e.g., daily)
    }

    private static Task StartBatteryMonitorAsync(ILoggerFactory loggerFactory, CancellationTokenSource cts, XiaomiBand10 device, Config config)
    {
        var monitorLogger = loggerFactory.CreateLogger<BatteryMonitor>();
        var bmCfg = config.BatteryMonitoring;

        var monitor = new BatteryMonitor(
            monitorLogger,
            device,
            bmCfg,
            tryResolvePatternName: name => config.Patterns.ContainsKey(name),
            playPattern: async name =>
            {
                // We already validated the name exists.
                await device.VibrateAsync(config.Patterns[name], cts.Token);
            },
            beginOutputScope: BeginOutputScope);

        return Task.Run(() => monitor.RunAsync(cts.Token), cts.Token);
    }

    // Main boot sequence: load config, build logging, connect over Bluetooth RFCOMM,
    // authenticate, and keep trying to reconnect if the band goes out of range.
    async static Task Main(string[] args)
    {
        // Config stores device info and vibration patterns
        Console.WriteLine("Loading configuration...");
        var filePath = args.Length > 0 ? args[0] : "config.yml";
        var doesFileExist = System.IO.File.Exists(filePath);
        if (!doesFileExist)
        {
            Console.WriteLine($"Configuration file not found at {filePath}");
            return;
        }
        var config = Config.Load(filePath);
        Console.WriteLine("Loaded configuration");

        var defaultLevel = LoggingConfig.ParseLoggingLevel(config.Logging.Default);
        var filters = config.Logging.Filters;

        var rootNamespace = typeof(Program).Namespace;
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .AddConsole()
                .SetMinimumLevel(defaultLevel);
            
            foreach (var filter in filters)
            {
                var level = LoggingConfig.ParseLoggingLevel(filter.Value);
                builder.AddFilter($"{rootNamespace}.{filter.Key}", level);
            }
        });

        logger = loggerFactory.CreateLogger<Program>();

        // Start a single input pump that feeds the channel for all sessions.
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var line = Console.ReadLine();
                if (line == null)
                    break;
                await InputChannel.Writer.WriteAsync(line);
            }
        });

        try
        {
            var deviceAddr = config.Device.MacAddress;
            var reconnectDelay = TimeSpan.FromSeconds(5);

            // Quit on Ctrl+C
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            // Outer loop keeps the app alive: after any disconnect, dispose the old Bluetooth
            // client (closing sockets/read loop) and create a fresh one before retrying.
            while (!cts.IsCancellationRequested)
            {
                var bluetoothLogger = loggerFactory.CreateLogger<BluetoothSppClient>();
                using var bluetooth = new BluetoothSppClient(bluetoothLogger, loggerFactory);

                var connectionTcs = new TaskCompletionSource();
                CancellationTokenSource sessionCts = null;

                // When the socket connects, create a device instance scoped to this session.
                bluetooth.OnConnect(async () =>
                {
                    try
                    {
                        sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                        logger.LogInformation("Device is now connected");
                        var deviceLogger = loggerFactory.CreateLogger<XiaomiBand10>();
                        using var device = new XiaomiBand10(
                            deviceLogger,
                            bluetooth,
                            config.Device.AuthKey,
                            loggerFactory,
                            diagnosticLogging: config.Logging.DiagnosticMode);
                        await AuthenticateAsync(sessionCts, device, config);
                        _ = StartBatteryMonitorAsync(loggerFactory, sessionCts, device, config);
                        await RunTestingLoopAsync(sessionCts, device, config);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error in connection handler");
                        connectionTcs.TrySetException(ex);
                    }
                    finally
                    {
                        connectionTcs.TrySetResult();
                        sessionCts?.Cancel();
                        sessionCts?.Dispose();
                    }
                });

                // If the band drops (out of range, etc.), end the session and trigger a retry.
                bluetooth.OnDisconnect((string disconnectionReason) =>
                {
                    logger.LogError("Device disconnected because: {DisconnectionReason}!", disconnectionReason);
                    sessionCts?.Cancel();
                    connectionTcs.TrySetResult();
                });

                try
                {
                    logger.LogInformation("Connecting to {Device} at {Address}",
                        config.Device.Name, deviceAddr);

                    await bluetooth.ConnectAsync(deviceAddr, channel: 5, ct: cts.Token);
                    await connectionTcs.Task;
                }
                catch (OperationCanceledException)
                {
                    logger.LogInformation("Application shutting down");
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Connection attempt failed");
                }

                // Ensure per-session CTS is cancelled before we loop again.
                sessionCts?.Cancel();

                if (!cts.IsCancellationRequested)
                {
                    logger.LogInformation("Reconnecting in {Seconds} seconds...", reconnectDelay.TotalSeconds);
                    try
                    {
                        await Task.Delay(reconnectDelay, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // exiting
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fatal error");
            Environment.Exit(1);
        }
    }
}
