using Marshal.Application.Abstractions;

namespace Marshal.Infrastructure.Data;

/// <summary>Brama na bazę oparta o semafor. Jedna praca naraz, w kolejności zgłoszeń.</summary>
/// <remarks>
/// <para>
/// Brama przepuszcza ponownie tego, kto już jest w środku. Bez tego nie dałoby się
/// przez nią przeprowadzić odczytów: synchronizacja bierze bramę na całą porcję
/// i woła w środku repozytoria, więc druga próba wejścia czekałaby na zwolnienie
/// przez samą siebie — czyli w nieskończoność. Semafor nie jest wznawialny sam
/// z siebie, stąd znacznik idący z przepływem wywołania.
/// </para>
/// <para>
/// Znacznik <b>schodzi w dół</b>: praca odpalona w tle ze środka bramy dziedziczy go
/// i ominie kolejkę. Dlatego roboty w tle zaczynają się poza bramą — a te, które
/// muszą ruszyć ze środka, mają własną kolejkę (odbicie do kalendarza).
/// </para>
/// <para>
/// <b>Praca idzie wątkiem z puli, nie wątkiem, który o nią poprosił.</b> To jest tutaj
/// drugie zadanie tej klasy i wzięło się z okienka „Marshal nie odpowiada" przy starcie.
/// Odczyt z bazy wygląda na czynność asynchroniczną i nią nie jest: SQLite nie ma
/// prawdziwego odczytu asynchronicznego, a budowanie modelu Entity Framework na telefonie
/// idzie sekundami. Każde „await" wracało więc natychmiast na ten sam wątek i trzymało
/// go do końca — a wątkiem było okno, bo stamtąd woła się wczytanie ekranu.
/// </para>
/// <para>
/// Przeniesienie tego do jednego miejsca zamiast do kilkunastu wywołujących jest całym
/// powodem, dla którego brama istnieje: przechodzi przez nią <b>każdy</b> odczyt i zapis,
/// więc wystarczy raz. Oczekiwanie wraca tam, skąd wyszło, czyli na wątek okna — a to
/// znaczy, że wołający dalej może zaraz po nim wypełniać kolekcje ekranu.
/// </para>
/// <para>
/// Znacznik ustawiany <b>przed</b> odpaleniem pracy, bo to jego wartość z tej chwili
/// jedzie razem z nią. Ustawiony po — nie dojechałby, a wtedy repozytorium wołane
/// w środku czekałoby na bramę trzymaną przez siebie samego.
/// </para>
/// </remarks>
public sealed class KolejkaBazy : IDbQueue
{
    private readonly SemaphoreSlim _brama = new(1, 1);

    /// <summary>Czy ten przepływ wywołania jest już w środku bramy.</summary>
    private readonly AsyncLocal<bool> _wSrodku = new();

    public async Task RunAsync(Func<Task> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (_wSrodku.Value)
        {
            await work();
            return;
        }

        await _brama.WaitAsync(ct);
        _wSrodku.Value = true;

        try
        {
            await Task.Run(work, ct);
        }
        finally
        {
            _wSrodku.Value = false;
            _brama.Release();
        }
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (_wSrodku.Value)
        {
            return await work();
        }

        await _brama.WaitAsync(ct);
        _wSrodku.Value = true;

        try
        {
            return await Task.Run(work, ct);
        }
        finally
        {
            _wSrodku.Value = false;
            _brama.Release();
        }
    }
}

/// <summary>
/// Brama, która nikogo nie zatrzymuje. Do testów i do dróg bez współbieżności.
/// </summary>
/// <remarks>
/// Testy wołają jedną rzecz naraz z jednego wątku, więc kolejka niczego by tam nie
/// zmieniła poza czasem — a wstawiona domyślnie w konstruktorach oszczędza
/// przepisywania kilkunastu miejsc, które o niej nie muszą wiedzieć.
/// </remarks>
public sealed class KolejkaWprost : IDbQueue
{
    public Task RunAsync(Func<Task> work, CancellationToken ct = default) =>
        work is null ? throw new ArgumentNullException(nameof(work)) : work();

    public Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct = default) =>
        work is null ? throw new ArgumentNullException(nameof(work)) : work();
}
