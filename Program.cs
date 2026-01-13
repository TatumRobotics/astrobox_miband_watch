using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using XiaomiAstroBoxCSharp.Bluetooth;
using XiaomiAstroBoxCSharp.Device;

namespace XiaomiAstroBoxCSharp;

// Entry point for the Raspberry Pi app that connects to a Xiaomi Smart Band 10,
// authenticates, and offers a simple console-driven test loop.
class Program()
{
    private static ILogger<Program> logger;

    // Simple console REPL so you can manually exercise band commands/patterns.
    private static async Task RunTestingLoopAsync(CancellationTokenSource cts, XiaomiBand10 device, Config config)
    {
        while (!cts.Token.IsCancellationRequested)
        {
            string input;
            try
            {
                // Make console reads cancellable so a disconnect doesn't leave an old
                // session consuming the next user command.
                input = (await Task.Run(() => Console.ReadLine(), cts.Token))?.Trim();
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (cts.Token.IsCancellationRequested)
                break;

            if (string.IsNullOrEmpty(input))
                continue;

            if (config.Patterns.TryGetValue(input, out var pattern))
            {
                logger.LogInformation("Playing pattern: {Pattern}", input);
                await device.VibrateAsync(pattern, cts.Token);
                continue;
            }

            switch (input) {
                case "battery":
                    var batteryPercent = await device.RequestBatteryPercentAsync(cts.Token);
                    logger.LogInformation("Battery: {Percent}%", batteryPercent);
                    break;
                case "wearing":
                    var isWearingWatch = await device.RequestIsWearingWatchAsync(cts.Token);
                    logger.LogInformation("Wearing the watch? {Wearing}.", isWearingWatch);
                    break;
                case "clock":
                    logger.LogInformation("Set clock on watch");
                    var fakeTime = new DateTime(2025, 3, 16, 15, 16, 23, DateTimeKind.Utc);
                    await device.SetWatchTimeAsync(fakeTime, cts.Token);
                    break;
                default:
                    logger.LogWarning("Command not found: {Input}", input);
                    break;
            }
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
            Console.WriteLine($"ACK received for sequence {sequence}!");
        });

        logger.LogInformation("Available patterns: {Patterns}", string.Join(", ", config.Patterns.Keys));

        // update watch time (otherwise it goes out of sync)
        // TODO: use real time zone of the robot
        string timeZoneId = "Eastern Standard Time";
        TimeZoneInfo targetZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        await device.SetWatchTimeAsync(TimeZoneInfo.ConvertTime(DateTime.UtcNow, targetZone), cts.Token);
        // it might also be good to periodically update the watch time
        // like maybe once per day?
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
                        using var device = new XiaomiBand10(deviceLogger, bluetooth, config.Device.AuthKey, loggerFactory);
                        await AuthenticateAsync(sessionCts, device, config);
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
