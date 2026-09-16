namespace Marshal.Application.Sync;

/// <summary>Plik zmian jednego urządzenia widziany w składnicy.</summary>
public sealed record RemoteLog(string DeviceId, long Length);

/// <summary>
/// Składnica plików zmian (spec 9.2).
/// </summary>
/// <remarks>
/// Wydzielona, bo Dysk Google jest jedną z możliwości, nie warunkiem. Ta sama
/// mechanika działa na katalogu synchronizowanym przez Dropboksa, OneDrive
/// albo Syncthinga — i właśnie taka implementacja służy do testów, więc nie jest
/// atrapą, tylko drugą drogą.
///
/// Warunek konieczny dla każdej implementacji: **każde urządzenie zapisuje
/// wyłącznie własny plik i wyłącznie przez dopisywanie na koniec**. Stąd brak
/// metody nadpisującej treść.
/// </remarks>
public interface ISyncTransport
{
    Task<IReadOnlyList<RemoteLog>> ListLogsAsync(CancellationToken ct = default);

    /// <summary>
    /// Treść od zadanego przesunięcia w bajtach. Plik jest tylko dopisywany
    /// i każda linia kończy się znakiem nowej linii, więc przesunięcie zawsze
    /// wypada na granicy wiersza.
    /// </summary>
    Task<string> ReadFromAsync(string deviceId, long offset, CancellationToken ct = default);

    /// <summary>Dopisuje na koniec własnego pliku. Nigdy nie dotyka cudzych.</summary>
    Task AppendAsync(string deviceId, string content, CancellationToken ct = default);
}
