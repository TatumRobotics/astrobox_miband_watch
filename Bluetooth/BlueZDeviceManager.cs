using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus;

namespace XiaomiAstroBoxCSharp.Bluetooth;

/// Manages BlueZ device operations including discovery, trust, and pairing
public class BlueZDeviceManager(ILogger<BlueZDeviceManager> logger) : IDisposable
{
    private Connection _dbus;
    private bool _disposed;

    public async Task InitializeAsync()
    {
        if (_dbus != null)
        {
            return;
        }

        _dbus = new Connection(Address.System);
        await _dbus.ConnectAsync();
    }

    /// <summary>
    /// Ensures a device is discovered, trusted, and paired.
    /// Returns when the device is ready for connection.
    /// </summary>
    public async Task EnsureDeviceReadyAsync(string macAddress, CancellationToken ct = default)
    {
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
            logger.LogInformation("Device already known to BlueZ");
        }
        catch
        {
            // logger.LogInformation("Device not found, starting discovery...");
            // await adapter.StartDiscoveryAsync();

            // for (int i = 0; i < 20; i++)
            // {
            //     await Task.Delay(500, ct);
            //     try
            //     {
            //         device = _dbus.CreateProxy<IDevice1>("org.bluez", devicePath);
            //         await device.GetAsync<string>("Address");
            //         logger.LogInformation("Device discovered after {Milliseconds}ms", (i + 1) * 500);
            //         deviceFound = true;
            //         break;
            //     }
            //     catch { }
            // }

            // try { await adapter.StopDiscoveryAsync(); } catch { }
        }
        
        if (!deviceFound || device == null)
            throw new InvalidOperationException($"Device {macAddress} not found after discovery");

        // Trust the device
        try
        {
            await device.SetAsync("Trusted", true);
            logger.LogInformation("Device marked as trusted");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not set trusted");
        }

        // Check if paired
        bool paired = false;
        try { paired = await device.GetAsync<bool>("Paired"); } catch { }

        if (!paired)
        {
            logger.LogInformation("Device not paired, registering agent and pairing...");

            // Register our auto-accept agent
            var agent = new AutoAcceptAgent();
            await _dbus.RegisterObjectAsync(agent);

            try
            {
                await agentManager.RegisterAgentAsync(AutoAcceptAgent.Path, "NoInputNoOutput");
                await agentManager.RequestDefaultAgentAsync(AutoAcceptAgent.Path);
                logger.LogInformation("Agent registered successfully");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Agent registration warning");
            }

            try
            {
                logger.LogInformation("Initiating pairing... Please accept on your watch!");
                await device.PairAsync();
                logger.LogInformation("Pairing successful!");
            }
            catch (DBusException ex) when (ex.ErrorName == "org.bluez.Error.AlreadyExists")
            {
                logger.LogInformation("Already paired");
            }
            catch (DBusException ex) when (ex.ErrorName == "org.bluez.Error.AuthenticationFailed")
            {
                throw new InvalidOperationException("Pairing was rejected or timed out. Please try again and accept the pairing on your watch.");
            }

            // Wait and verify pairing actually completed
            logger.LogInformation("Verifying pairing status...");
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(200, ct);
                try
                {
                    paired = await device.GetAsync<bool>("Paired");
                    if (paired)
                    {
                        logger.LogInformation("Pairing confirmed!");
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
            logger.LogInformation("Device already paired");
        }

        logger.LogInformation("Waiting briefly before RFCOMM connection...");
        // await Task.Delay(500, ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _dbus?.Dispose();
    }
}
