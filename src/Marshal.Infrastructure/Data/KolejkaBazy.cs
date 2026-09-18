using Marshal.Application.Abstractions;

namespace Marshal.Infrastructure.Data;

/// <summary>Brama na bazę oparta o semafor. Jedna praca naraz, w kolejności zgłoszeń.</summary>
public sealed class KolejkaBazy : IKolejkaBazy
{
    private readonly SemaphoreSlim _brama = new(1, 1);

    public async Task WykonajAsync(Func<Task> praca, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(praca);

        await _brama.WaitAsync(ct);

        try
        {
            await praca();
        }
        finally
        {
            _brama.Release();
        }
    }

    public async Task<T> WykonajAsync<T>(Func<Task<T>> praca, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(praca);

        await _brama.WaitAsync(ct);

        try
        {
            return await praca();
        }
        finally
        {
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
