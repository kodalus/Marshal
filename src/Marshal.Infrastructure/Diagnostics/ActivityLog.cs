using Marshal.Application.Abstractions;
using Marshal.Domain.Diagnostics;
using Marshal.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Marshal.Infrastructure.Diagnostics;

/// <summary>
/// Dziennik zapisywany do bazy (spec 12).
/// </summary>
/// <remarks>
/// <para>
/// <b>Własny kontekst na każdy zapis, nie ten wspólny.</b> Aplikacja ma jeden kontekst
/// na wszystko, więc zapis dziennika w środku operacji zatwierdziłby przy okazji
/// **cudze** niezapisane zmiany — dokładnie te, które wywołujący trzyma jeszcze
/// w śledzeniu. Byłby to zapis w połowie operacji, wywołany przez samo jej opisanie.
/// Kontekst z tych samych opcji celuje w tę samą bazę, a w testach, gdzie opcje niosą
/// otwarte połączenie do bazy w pamięci, w tę samą bazę w pamięci.
/// </para>
/// <para>
/// <b>Zapis nie rzuca.</b> Dwa połączenia do jednego pliku SQLite potrafią się zderzyć
/// — na przykład gdy wczytywanie kopii zapasowej trzyma transakcję. Awaria dziennika
/// nie może być gorsza od braku dziennika, więc wyjątek zostaje tutaj. Nie znika za to
/// bez śladu: nieudane zapisy są liczone, a licznik widać na ekranie dziennika.
/// </para>
/// </remarks>
public sealed class ActivityLog(DbContextOptions<MarshalDbContext> options, IClock clock)
    : IActivityLog
{
    /// <summary>Ile wpisów trzymamy. Starsze są kasowane przy zapisie.</summary>
    private const int Limit = 500;

    /// <summary>Ile ponad limit wolno uzbierać, zanim ruszy sprzątanie.</summary>
    /// <remarks>Zapas po to, żeby kasowanie szło raz na sto wpisów, a nie przy każdym.</remarks>
    private const int Slack = 100;

    private int _lost;

    public int Dropped => Volatile.Read(ref _lost);

    public async Task RecordAsync(
        string operation,
        string outcome,
        ActivityLevel level = ActivityLevel.Ok,
        string? detail = null,
        CancellationToken ct = default)
    {
        try
        {
            await using var db = new MarshalDbContext(options);

            db.ActivityEntries.Add(new ActivityEntry(
                Guid.CreateVersion7(), clock.Now, operation, outcome, level, detail));

            await db.SaveChangesAsync(ct);
            await TrimAsync(db, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Interlocked.Increment(ref _lost);
        }
    }

    public async Task<IReadOnlyList<ActivityEntry>> RecentAsync(
        int count = 200, CancellationToken ct = default)
    {
        await using var db = new MarshalDbContext(options);

        return await db.ActivityEntries
            .AsNoTracking()
            .OrderByDescending(w => w.At)
            .Take(Math.Max(1, count))
            .ToListAsync(ct);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var db = new MarshalDbContext(options);

        await db.ActivityEntries.ExecuteDeleteAsync(ct);
    }

    private static async Task TrimAsync(MarshalDbContext db, CancellationToken ct)
    {
        if (await db.ActivityEntries.CountAsync(ct) <= Limit + Slack)
        {
            return;
        }

        // Po identyfikatory, nie po datę graniczną: sto wpisów zapisanych w tej samej
        // milisekundzie ma jedną datę, a kasowanie „wszystkiego od granicy w dół"
        // zabrałoby wtedy cały dziennik naraz. Lista ma najwyżej sto pozycji, bo
        // sprzątanie rusza dopiero po przekroczeniu limitu o tyle.
        var toDelete = await db.ActivityEntries
            .OrderByDescending(w => w.At)
            .ThenByDescending(w => w.Id)
            .Skip(Limit)
            .Select(w => w.Id)
            .ToListAsync(ct);

        await db.ActivityEntries
            .Where(w => toDelete.Contains(w.Id))
            .ExecuteDeleteAsync(ct);
    }
}
