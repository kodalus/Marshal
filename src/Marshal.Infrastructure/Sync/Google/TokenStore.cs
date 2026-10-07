using Google.Apis.Util.Store;

namespace Marshal.Infrastructure.Sync.Google;

/// <summary>
/// Składnica żetonów Google — ta sama co biblioteczna, ale po kolei.
/// </summary>
/// <remarks>
/// <para>
/// <b>Objaw:</b> „Nie udało się odświeżyć 4 z 16 kalendarzy — the process cannot access
/// the file […] TokenResponse-marshal-kalendarz-zapis because it is being used by
/// another process". Zawsze cztery, bo tyle kalendarzy odświeża się naraz
/// (<c>MaxDegreeOfParallelism</c>), i zawsze ten sam plik.
/// </para>
/// <para>
/// <b>Przyczyna:</b> żeton dostępu żyje godzinę. Gdy wygaśnie, biblioteka odświeża go
/// przy pierwszym żądaniu — a tych jest cztery naraz, na jednym i tym samym
/// poświadczeniu. Każde z nich odświeża osobno i każde zapisuje wynik <b>do tego samego
/// pliku</b>, bo <c>FileDataStore</c> nie ma nic, co by je rozstawiło w kolejkę: pisze
/// wprost, przez <c>File.WriteAllText</c>. Trzy przegrywają wyścig o uchwyt i wracają
/// jako nieudane odświeżenie kalendarza — czyli objaw w miejscu, które z żetonem nie ma
/// nic wspólnego.
/// </para>
/// <para>
/// To nie jest pojedyncze zdarzenie: powtarza się za każdym razem, gdy żeton wygaśnie
/// i odświeżenie obejmie więcej niż jeden kalendarz. Przy żetonie żyjącym godzinę
/// i odświeżaniu co pięć minut znaczy to raz na godzinę.
/// </para>
/// <para>
/// <b>Rozwiązanie:</b> brama na całą składnicę. Jedna na proces, bo katalog z żetonami
/// jest jeden — a nie jedna na egzemplarz, bo egzemplarzy biblioteka tworzy po kilka
/// i każdy pisałby do tych samych plików. Do tego kilka podejść przy odmowie dostępu:
/// plik potrafi być zajęty także przez coś spoza aplikacji (program antywirusowy,
/// kopia zapasowa), a wtedy jedyną sensowną odpowiedzią jest chwila zwłoki.
/// </para>
/// </remarks>
public sealed class TokenStore(string folder) : IDataStore
{
    /// <summary>
    /// Jedna brama na proces. Statyczna celowo — zob. uwagi do klasy.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Ile razy ponowić i ile czekać między podejściami.</summary>
    /// <remarks>
    /// Odstęp rośnie, bo cudzy uchwyt do pliku bywa trzymany dłużej niż chwilę,
    /// a cztery podejścia po ćwierć sekundy w sumie to wciąż mniej, niż trwa jedna
    /// wyprawa do Google.
    /// </remarks>
    private const int Attempts = 4;

    private readonly FileDataStore _inner = new(folder, fullPath: true);

    public Task StoreAsync<T>(string key, T value) =>
        OnceAsync(() => _inner.StoreAsync(key, value));

    public Task DeleteAsync<T>(string key) =>
        OnceAsync(() => _inner.DeleteAsync<T>(key));

    public Task<T> GetAsync<T>(string key) =>
        OnceAsync(() => _inner.GetAsync<T>(key));

    public Task ClearAsync() => OnceAsync(_inner.ClearAsync);

    private static async Task OnceAsync(Func<Task> work)
    {
        await OnceAsync(async () =>
        {
            await work();
            return true;
        });
    }

    private static async Task<T> OnceAsync<T>(Func<Task<T>> work)
    {
        await Gate.WaitAsync();

        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await work();
                }
                catch (IOException) when (attempt < Attempts)
                {
                    await Task.Delay(50 * attempt);
                }
                catch (UnauthorizedAccessException) when (attempt < Attempts)
                {
                    await Task.Delay(50 * attempt);
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}
