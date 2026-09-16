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
        LastSegment = string.Empty;
    }

    public SyncCursor(string remoteDeviceId, string lastSegment)
    {
        RemoteDeviceId = remoteDeviceId;
        LastSegment = lastSegment;
    }

    public string RemoteDeviceId { get; private set; }

    /// <summary>
    /// Nazwa ostatniej przeczytanej porcji. Porcja raz zapisana nigdy się nie zmienia,
    /// więc przeczytana zostaje przeczytana. Puste znaczy „jeszcze żadnej".
    /// </summary>
    public string LastSegment { get; private set; } = string.Empty;

    public void MoveTo(string segment) => LastSegment = segment;
}
