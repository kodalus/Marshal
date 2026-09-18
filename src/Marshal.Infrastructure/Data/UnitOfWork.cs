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
    IKolejkaBazy? kolejka = null,
    ISygnalZapisu? sygnal = null) : IUnitOfWork
{
    private readonly IKolejkaBazy _kolejka = kolejka ?? new KolejkaWprost();

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        var wierszy = await _kolejka.WykonajAsync(() => db.SaveChangesAsync(ct), ct);

        // Tylko gdy naprawdę coś poszło do bazy. Zapis bez zmian zdarza się często —
        // odświeżenia, zapisy „na wszelki wypadek" — i prosiłby o przebieg bez treści.
        if (wierszy > 0)
        {
            sygnal?.Zglos();
        }

        return wierszy;
    }
}
