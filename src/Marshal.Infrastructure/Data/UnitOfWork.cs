using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;

namespace Marshal.Infrastructure.Data;

/// <remarks>
/// <para>
/// Zapis idzie przez bramę, bo kontekst bazy jest pojedynczy na cały proces i nie
/// znosi dwóch rzeczy naraz. Tędy przechodzi <b>każdy</b> zapis z okna, więc jedno
/// miejsce wystarcza, żeby okno i synchronizacja nie weszły sobie w drogę.
/// </para>
/// <para>
/// I z tego samego powodu tędy podnoszony jest znak zapisu: skoro przechodzi tu każda
/// zmiana z okna, to jest jedyne miejsce, w którym da się ją zauważyć raz, a nie
/// w kilkunastu usługach osobno.
/// </para>
/// </remarks>
public sealed class UnitOfWork(
    MarshalDbContext db,
    IDbQueue? queue = null,
    IWriteSignal? signal = null) : IUnitOfWork
{
    private readonly IDbQueue _queue = queue ?? new DirectQueue();

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        var rows = await _queue.RunAsync(() => db.SaveChangesAsync(ct), ct);

        // Tylko gdy naprawdę coś poszło do bazy. Zapis bez zmian zdarza się często —
        // odświeżenia, zapisy „na wszelki wypadek" — i prosiłby o przebieg bez treści.
        if (rows > 0)
        {
            signal?.Report();
        }

        return rows;
    }
}
