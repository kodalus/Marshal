namespace Marshal.Domain.Sync;

/// <summary>
/// Ustawienie tego urządzenia — identyfikator, ostatni znacznik zegara.
/// Lokalne, niesynchronizowane: każde urządzenie ma własne.
/// </summary>
public sealed class LocalSetting
{
    public const string DeviceIdKey = "device-id";
    public const string LastHlcKey = "last-hlc";

    private LocalSetting()
    {
        Key = string.Empty;
        Value = string.Empty;
    }

    public LocalSetting(string key, string value)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; private set; }

    public string Value { get; private set; }

    public void Set(string value) => Value = value;
}
