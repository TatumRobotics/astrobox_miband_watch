using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using XiaomiAstroBoxCSharp.Bluetooth;
using XiaomiAstroBoxCSharp.Device;

namespace XiaomiAstroBoxCSharp;

class Program()
{
    private static ILogger<Program> logger;

    // Lets users input commands to test the watch
    private static async Task RunTestingLoopAsync(CancellationTokenSource cts, XiaomiBand10 device, Config config)
    {
        while (!cts.Token.IsCancellationRequested)
        {
            var input = Console.ReadLine()?.Trim();

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
                default:
                    logger.LogWarning("Command not found: {Input}", input);
                    break;
            }
        }
    }

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

        // request battery to make sure its paired
        var batteryPercent = await device.RequestBatteryPercentAsync(cts.Token);
        logger.LogInformation("Battery percent: {batteryPercent}", batteryPercent);
    }

    async static Task Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .AddConsole()
                .SetMinimumLevel(LogLevel.Debug)
                .AddFilter("XiaomiAstroBoxCSharp.Device.XiaomiBand10", LogLevel.Warning)
                .AddFilter("XiaomiAstroBoxCSharp.Protocol.PacketProcessor", LogLevel.Warning)
                .AddFilter("XiaomiAstroBoxCSharp.Authentication.AuthenticationHandler", LogLevel.Warning)
                .AddFilter("XiaomiAstroBoxCSharp.Bluetooth.BluetoothSppClient", LogLevel.Warning)
                .AddFilter("Program", LogLevel.Information);
        });

        logger = loggerFactory.CreateLogger<Program>();

        try
        {
            // Config stores device info and vibration patterns
            logger.LogInformation("Loading configuration...");
            var config = Config.Load(args.Length > 0 ? args[0] : "config.yml");
            logger.LogInformation("Loaded configuration");

            var deviceAddr = config.Device.MacAddress;

            // Quit on Ctrl+C
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            var bluetoothLogger = loggerFactory.CreateLogger<BluetoothSppClient>();
            using var bluetooth = new BluetoothSppClient(bluetoothLogger);

            var connectionTcs = new TaskCompletionSource();

            bluetooth.OnConnect(async () =>
            {
                try
                {
                    logger.LogInformation("Device is now connected");
                    var deviceLogger = loggerFactory.CreateLogger<XiaomiBand10>();
                    using var device = new XiaomiBand10(deviceLogger, bluetooth, config.Device.AuthKey, loggerFactory);
                    await AuthenticateAsync(cts, device, config);
                    await RunTestingLoopAsync(cts, device, config);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in connection handler");
                    connectionTcs.TrySetException(ex);
                }
                finally
                {
                    connectionTcs.TrySetResult();
                }
            });

            var disconnected = false;
            bluetooth.OnDisconnect((string disconnectionReason) =>
            {
                if (!disconnected)
                {
                    disconnected = true;
                    logger.LogError("Device disconnected because: {DisconnectionReason}!", disconnectionReason);
                    connectionTcs.TrySetResult();
                    Environment.Exit(1);
                }
            });

            logger.LogInformation("Connecting to {Device} at {Address}",
                config.Device.Name, deviceAddr);

            await bluetooth.ConnectAsync(deviceAddr, channel: 5);

            await connectionTcs.Task;
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Application shutting down");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fatal error");
            Environment.Exit(1);
        }
    }
}
