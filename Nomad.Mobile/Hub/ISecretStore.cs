namespace Nomad.Mobile.Hub;

/// <summary>Small values kept on the phone: on Android, encrypted under a key in the Android Keystore.</summary>
public interface ISecretStore
{
    string? Get(string key);

    void Set(string key, string value);

    void Remove(string key);
}

/// <summary>Keeps values in memory only (tests, and a platform with no store of its own).</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Get(string key) => _values.GetValueOrDefault(key);

    public void Set(string key, string value) => _values[key] = value;

    public void Remove(string key) => _values.Remove(key);
}

/// <summary>This phone's enrollment: which hub, its device id and name, and the secret it signs in with.</summary>
public sealed record Enrollment(Uri Hub, Guid DeviceId, string DeviceName, string Secret)
{
    private const string HubKey = "hub", IdKey = "deviceId", NameKey = "deviceName", SecretKey = "secret";

    public static Enrollment? Load(ISecretStore store) =>
        Uri.TryCreate(store.Get(HubKey), UriKind.Absolute, out var hub) && Guid.TryParse(store.Get(IdKey), out var id)
            && store.Get(SecretKey) is { Length: > 0 } secret
            ? new Enrollment(hub, id, store.Get(NameKey) ?? "Phone", secret)
            : null;

    public void Save(ISecretStore store)
    {
        store.Set(HubKey, Hub.ToString());
        store.Set(IdKey, DeviceId.ToString());
        store.Set(NameKey, DeviceName);
        store.Set(SecretKey, Secret);
    }

    public static void Clear(ISecretStore store)
    {
        foreach (var key in new[] { HubKey, IdKey, NameKey, SecretKey })
        {
            store.Remove(key);
        }
    }
}
