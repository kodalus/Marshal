namespace Marshal.Domain.Sync;

/// <summary>
/// Dokąd doczytaliśmy plik zmian danego urządzenia (spec 9.4).
/// </summary>
/// <remarks>
/// Kursor jest **przyspieszeniem, nie warunkiem poprawności**. Ponowne zastosowanie
/// tego samego wpisu nic nie zmienia, bo jego znacznik nie jest już nowszy od
/// zapisanego. Gdyby kursor przepadł, synchronizacja przeczyta plik od początku
/// i dojdzie do tego samego stanu — tylko wolniej.
/// </remarks>
public sealed class SyncCursor
{
    private SyncCursor()
    {
        RemoteDeviceId = string.Empty;
    }

    public SyncCursor(string remoteDeviceId, long offset)
    {
        RemoteDeviceId = remoteDeviceId;
        Offset = offset;
    }

    public string RemoteDeviceId { get; private set; }

    /// <summary>Przesunięcie w bajtach. Plik jest tylko dopisywany, więc raz przeczytane nie zmienia się.</summary>
    public long Offset { get; private set; }

    public void MoveTo(long offset) => Offset = offset;
}
