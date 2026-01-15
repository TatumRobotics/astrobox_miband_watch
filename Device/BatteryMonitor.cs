using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using XiaomiAstroBoxCSharp;

namespace XiaomiAstroBoxCSharp.Device;

public sealed class BatteryMonitor
{
    private readonly ILogger<BatteryMonitor> _logger;
    private readonly XiaomiBand10 _device;
    private readonly BatteryMonitoringConfig _cfg;
    private readonly Func<string, bool> _tryResolvePatternName;
    private readonly Func<string, Task> _playPattern;
    private readonly Func<IDisposable> _beginOutputScope;

    private int _lowBatteryLoopRunning;

    public BatteryMonitor(
        ILogger<BatteryMonitor> logger,
        XiaomiBand10 device,
        BatteryMonitoringConfig cfg,
        Func<string, bool> tryResolvePatternName,
        Func<string, Task> playPattern,
        Func<IDisposable> beginOutputScope)
    {
        _logger = logger;
        _device = device;
        _cfg = cfg;
        _tryResolvePatternName = tryResolvePatternName;
        _playPattern = playPattern;
        _beginOutputScope = beginOutputScope ?? throw new ArgumentNullException(nameof(beginOutputScope));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!_cfg.Enabled)
        {
            _logger.LogInformation("Battery monitoring disabled by config.");
            return;
        }

        var checkInterval = TimeSpan.FromHours(Math.Max(1, _cfg.CheckIntervalHours));
        _logger.LogInformation(
            "Battery monitor starting (threshold={Threshold}%, check_every={CheckEvery})",
            _cfg.LowBatteryThreshold,
            checkInterval);

        // Kick off an immediate check, then run periodically.
        await CheckOnceAndMaybeNotifyAsync(ct);

        using var timer = new PeriodicTimer(checkInterval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            await CheckOnceAndMaybeNotifyAsync(ct);
        }
    }

    private async Task CheckOnceAndMaybeNotifyAsync(CancellationToken ct)
    {
        using var outputScope = _beginOutputScope();
        XiaomiBand10.BatteryStatus status;
        try
        {
            status = await _device.RequestBatteryStatusAsync(ct, timeoutSeconds: 10);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Battery check failed");
            return;
        }

        _logger.LogInformation(
            "Battery status: {Percent}% (charging={Charging})",
            status.Percent,
            status.IsCharging);

        if (status.IsCharging)
        {
            // Never notify while charging.
            return;
        }

        if (status.Percent >= _cfg.LowBatteryThreshold)
        {
            return;
        }

        // Below threshold and not charging: start the repeating notification loop if not already running.
        if (Interlocked.CompareExchange(ref _lowBatteryLoopRunning, 1, 0) != 0)
        {
            _logger.LogDebug("Low-battery notification loop already running.");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunLowBatteryLoopAsync(ct);
            }
            finally
            {
                Interlocked.Exchange(ref _lowBatteryLoopRunning, 0);
            }
        }, ct);
    }

    private async Task RunLowBatteryLoopAsync(CancellationToken ct)
    {
        var notifyInterval = TimeSpan.FromMinutes(Math.Max(1, _cfg.NotifyIntervalMinutes));
        var pollInterval = TimeSpan.FromSeconds(Math.Max(5, _cfg.ChargingPollSeconds));

        var patternName = _cfg.LowBatteryPattern ?? string.Empty;
        if (string.IsNullOrWhiteSpace(patternName) || !_tryResolvePatternName(patternName))
        {
            _logger.LogWarning(
                "Low-battery pattern '{Pattern}' not found in config patterns; skipping vibration notifications.",
                patternName);
            return;
        }

        _logger.LogWarning(
            "Battery is below {Threshold}%. Starting low-battery notification loop (every {NotifyEvery}).",
            _cfg.LowBatteryThreshold,
            notifyInterval);

        while (!ct.IsCancellationRequested)
        {
            // Before sending, check if we've started charging.
            XiaomiBand10.BatteryStatus status;
            using (var outputScope = _beginOutputScope())
            {
                status = await _device.RequestBatteryStatusAsync(ct, timeoutSeconds: 10);
            }
            if (status.IsCharging)
            {
                _logger.LogInformation("Watch is charging; stopping low-battery notification loop immediately.");
                return;
            }

            using (var outputScope = _beginOutputScope())
            {
                await _playPattern(patternName);
            }

            // Wait notifyInterval, but poll charging state frequently so we can stop quickly.
            var started = DateTimeOffset.UtcNow;
            while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow - started < notifyInterval)
            {
                await Task.Delay(pollInterval, ct);
                using (var outputScope = _beginOutputScope())
                {
                    status = await _device.RequestBatteryStatusAsync(ct, timeoutSeconds: 10);
                }
                if (status.IsCharging)
                {
                    _logger.LogInformation("Watch is charging; stopping low-battery notification loop immediately.");
                    return;
                }
            }
        }
    }
}

