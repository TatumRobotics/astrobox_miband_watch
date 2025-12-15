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

            // Quit on Ctrl+C
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            var bluetoothLogger = loggerFactory.CreateLogger<BluetoothSppClient>();
            // "using" ensures bluetooth.Dispose is called before quitting the program
            using var bluetooth = new BluetoothSppClient(bluetoothLogger, loggerFactory);

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
