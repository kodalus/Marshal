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
public sealed class DbQueue : IDbQueue
{
    /// <summary>
    /// Ile wolno czekać na bramę, zanim czekanie uznamy za zacięcie.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Brama bez sufitu zamienia jedną zaciętą pracę w <b>martwą aplikację</b>, i to bez
    /// śladu: wszystko, co przychodzi potem, czeka w kolejce, ekrany nie wczytują się
    /// nigdy, a polecenia, które je wczytują, zostają wyłączone na zawsze razem ze swoimi
    /// przyciskami. Z zewnątrz wygląda to jak wyszarzona zakładka i jak dotknięcia, które
    /// nie łapią — czyli jak wiele różnych usterek naraz, z których żadna nie jest tą
    /// prawdziwą.
    /// </para>
    /// <para>
    /// Pół minuty, bo to już daleko poza wszystkim, co wolno nazwać wolnym odczytem:
    /// najcięższe wczytanie siatki idzie ułamkami sekundy. Przekroczenie jest więc
    /// zaciętej pracy zgłoszeniem, a nie karą za powolność.
    /// </para>
    /// <para>
    /// <b>Wyjątek zamiast czekania w nieskończoność.</b> Wyjątek ma dokąd trafić —
    /// wołający opakowują pracę i zapisują ją w dzienniku — a cisza nie ma dokąd.
    /// To jest ta sama zasada, co przy każdym przycisku w tym programie: odmowa, której
    /// nie widać, jest gorsza od odmowy.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Czy ten przepływ wywołania jest już w środku bramy.</summary>
    private readonly AsyncLocal<bool> _inside = new();

    public async Task RunAsync(Func<Task> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (_inside.Value)
        {
            await work();
            return;
        }

        await EnterAsync(ct);
        _inside.Value = true;

        try
        {
            await Task.Run(work, ct);
        }
        finally
        {
            _inside.Value = false;
            _gate.Release();
        }
    }

    public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (_inside.Value)
        {
            return await work();
        }

        await EnterAsync(ct);
        _inside.Value = true;

        try
        {
            return await Task.Run(work, ct);
        }
        finally
        {
            _inside.Value = false;
            _gate.Release();
        }
    }

    /// <summary>Wejście przez bramę albo zgłoszenie zacięcia — zob. <see cref="Ceiling"/>.</summary>
    private async Task EnterAsync(CancellationToken ct)
    {
        if (await _gate.WaitAsync(Ceiling, ct))
        {
            return;
        }

        throw new TimeoutException(
            $"Baza nie odpowiedziała przez {Ceiling.TotalSeconds:0} sekund — poprzednia praca "
            + "trzyma bramę. Kolejne odczyty czekałyby bez końca, więc ten się poddaje.");
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
public sealed class DirectQueue : IDbQueue
{
    public Task RunAsync(Func<Task> work, CancellationToken ct = default) =>
        work is null ? throw new ArgumentNullException(nameof(work)) : work();

    public Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct = default) =>
        work is null ? throw new ArgumentNullException(nameof(work)) : work();
}
