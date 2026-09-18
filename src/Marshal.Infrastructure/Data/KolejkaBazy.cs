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
/// </remarks>
public sealed class KolejkaBazy : IKolejkaBazy
{
    private readonly SemaphoreSlim _brama = new(1, 1);

    /// <summary>Czy ten przepływ wywołania jest już w środku bramy.</summary>
    private readonly AsyncLocal<bool> _wSrodku = new();

    public async Task WykonajAsync(Func<Task> praca, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(praca);

        if (_wSrodku.Value)
        {
            await praca();
            return;
        }

        await _brama.WaitAsync(ct);
        _wSrodku.Value = true;

        try
        {
            await praca();
        }
        finally
        {
            _wSrodku.Value = false;
            _brama.Release();
        }
    }

    public async Task<T> WykonajAsync<T>(Func<Task<T>> praca, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(praca);

        if (_wSrodku.Value)
        {
            return await praca();
        }

        await _brama.WaitAsync(ct);
        _wSrodku.Value = true;

        try
        {
            return await praca();
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
public sealed class KolejkaWprost : IKolejkaBazy
{
    public Task WykonajAsync(Func<Task> praca, CancellationToken ct = default) =>
        praca is null ? throw new ArgumentNullException(nameof(praca)) : praca();

    public Task<T> WykonajAsync<T>(Func<Task<T>> praca, CancellationToken ct = default) =>
        praca is null ? throw new ArgumentNullException(nameof(praca)) : praca();
}
