using System.Threading.Tasks;
using Tmds.DBus;

namespace XiaomiAstroBoxCSharp.Bluetooth;

[DBusInterface("org.bluez.Adapter1")]
public interface IAdapter1 : IDBusObject
{
    Task StartDiscoveryAsync();
    Task StopDiscoveryAsync();
    Task RemoveDeviceAsync(ObjectPath device);
}

[DBusInterface("org.bluez.Device1")]
public interface IDevice1 : IDBusObject
{
    Task PairAsync();
    Task<T> GetAsync<T>(string prop);
    Task SetAsync(string prop, object val);
}

[DBusInterface("org.bluez.AgentManager1")]
public interface IAgentManager1 : IDBusObject
{
    Task RegisterAgentAsync(ObjectPath agent, string capability);
    Task RequestDefaultAgentAsync(ObjectPath agent);
}

[DBusInterface("org.bluez.Agent1")]
public interface IAgent1 : IDBusObject
{
    Task ReleaseAsync();
    Task<string> RequestPinCodeAsync(ObjectPath device);
    Task DisplayPinCodeAsync(ObjectPath device, string pincode);
    Task<uint> RequestPasskeyAsync(ObjectPath device);
    Task DisplayPasskeyAsync(ObjectPath device, uint passkey, ushort entered);
    Task RequestConfirmationAsync(ObjectPath device, uint passkey);
    Task RequestAuthorizationAsync(ObjectPath device);
    Task AuthorizeServiceAsync(ObjectPath device, string uuid);
    Task CancelAsync();
}

public class AutoAcceptAgent : IAgent1
{
    public static readonly ObjectPath Path = new ObjectPath("/bluetooth/agent");

    public ObjectPath ObjectPath => Path;

    public Task ReleaseAsync() => Task.CompletedTask;

    public Task<string> RequestPinCodeAsync(ObjectPath device) => Task.FromResult("0000");

    public Task DisplayPinCodeAsync(ObjectPath device, string pincode) => Task.CompletedTask;

    public Task<uint> RequestPasskeyAsync(ObjectPath device) => Task.FromResult(0u);

    public Task DisplayPasskeyAsync(ObjectPath device, uint passkey, ushort entered) => Task.CompletedTask;

    public Task RequestConfirmationAsync(ObjectPath device, uint passkey) => Task.CompletedTask;

    public Task RequestAuthorizationAsync(ObjectPath device) => Task.CompletedTask;

    public Task AuthorizeServiceAsync(ObjectPath device, string uuid) => Task.CompletedTask;

    public Task CancelAsync() => Task.CompletedTask;
}
