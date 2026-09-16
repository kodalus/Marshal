using Marshal.Application.Abstractions;
using Marshal.Domain.Areas;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Data;

/// <summary>
/// Dziesięć obszarów odpowiedzialności z tabeli w spec 5.2, zakładanych przy pierwszym
/// uruchomieniu i od tego momentu edytowalnych.
/// </summary>
/// <remarks>
/// Dane początkowe nie idą przez mechanizm <c>HasData</c> EF Core celowo: tamte rekordy
/// są częścią migracji i każda ich zmiana wymagałaby nowej migracji, także wtedy, gdy
/// użytkownik zmieni sobie nazwę obszaru. Tutaj to zwykłe rekordy, zakładane raz.
/// </remarks>
public static class AreaSeed
{
    /// <summary>Nazwa, próg ciszy (N10), domyślny próg ponaglenia „Oczekiwanych".</summary>
    private static readonly (string Name, int QuietDays, int NudgeDays)[] Defaults =
    [
        ("Praca", 14, 5),
        ("Dzieci", 14, 5),
        ("Zdrowie", 30, 7),
        ("Dom", 30, 7),
        ("Finanse", 30, 7),
        ("Związek", 21, 3),
        ("Rozwój własny", 45, 7),
        ("Twórczość", 45, 7),
        ("Sprawy urzędowe", 60, 21),
        ("Relacje", 45, 7),
    ];

    public static async Task EnsureAsync(
        MarshalDbContext db, IClock clock, IHlcSource hlc, CancellationToken ct = default)
    {
        if (await db.Areas.AnyAsync(ct))
        {
            return;
        }

        var now = clock.Now;
        var order = 0.0;

        foreach (var (name, quietDays, nudgeDays) in Defaults)
        {
            db.Areas.Add(new Area(
                Guid.CreateVersion7(), now, hlc.Next(), name, order += 1.0, quietDays, nudgeDays));
        }

        await db.SaveChangesAsync(ct);
    }
}
