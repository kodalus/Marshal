using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Projects;
using Marshal.Domain.Tasks;

namespace Marshal.Application.UseCases;

/// <summary>Zadanie podsunięte przez „Teraz" razem z powodem, dla którego wypłynęło.</summary>
/// <remarks>
/// Powód jest częścią odpowiedzi, nie ozdobnikiem. Lista bez uzasadnienia każe wierzyć
/// na słowo, a wtedy pierwszy raz, gdy podsunie coś nietrafionego, przestaje się jej
/// ufać w ogóle.
/// </remarks>
public sealed record NowPick(TaskItem Task, int Score, string Reason);

/// <summary>
/// Widok „Teraz" (spec 8.1): aplikacja wybiera za Ciebie.
/// </summary>
public sealed class NowService(
    ITaskRepository tasks,
    IProjectRepository projects,
    IClock clock)
{
    /// <summary>Ile pozycji pokazuje ekran.</summary>
    public const int Slots = 5;

    /// <summary>Poniżej tylu kandydatów ekran prosi o oszacowanie zamiast wybierać.</summary>
    public const int TooFewCandidates = 3;

    public async Task<IReadOnlyList<NowPick>> PickAsync(
        int availableMinutes, Energy energy, CancellationToken ct = default)
    {
        var dzis = DateOnly.FromDateTime(clock.Now.DateTime);
        var wszystkie = await tasks.ByStateAsync(TaskState.Next, ct);

        // Projekt wstrzymany („kiedyś") albo zamknięty wyklucza swoje zadania: leżą
        // w bazie poprawnie, ale nie są tym, co można teraz zrobić.
        var zywe = (await projects.AllAsync(ct))
            .Where(p => p.State == ProjectState.Active && !p.Deleted)
            .Select(p => p.Id)
            .ToHashSet();

        var kandydaci = wszystkie
            .Where(t => t.DeferUntil is null || t.DeferUntil <= dzis)
            .Where(t => t.EstimatedMinutes is { } minuty && minuty <= availableMinutes)
            .Where(t => t.Energy == Energy.Unknown || t.Energy <= energy)
            .Where(t => t.ProjectId is null || zywe.Contains(t.ProjectId.Value))
            .ToList();

        // Zadanie, które jest jedyną akcją do przodu w swoim projekcie, odblokowuje go
        // — to jest odwrotna strona N1 i dlatego ma własne punkty.
        var odblokowujace = wszystkie
            .Where(t => t.ProjectId is not null)
            .GroupBy(t => t.ProjectId!.Value)
            .Where(g => g.Count() == 1)
            .Select(g => g.First().Id)
            .ToHashSet();

        return kandydaci
            .Select(t => Score(t, dzis, odblokowujace.Contains(t.Id)))
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.Task.CreatedAt)

            // Najwyżej jedno zadanie z jednego projektu. Pięć kroków tego samego projektu
            // wygląda jak praca, ale zamyka pole widzenia na resztę życia.
            .DistinctBy(p => p.Task.ProjectId ?? p.Task.Id)
            .Take(Slots)
            .ToList();
    }

    /// <summary>Ilu w ogóle jest kandydatów — bez tego nie da się zaproponować oszacowania.</summary>
    public async Task<int> UnestimatedCountAsync(CancellationToken ct = default) =>
        (await tasks.ByStateAsync(TaskState.Next, ct)).Count(t => t.EstimatedMinutes is null);

    /// <summary>
    /// Podpowiedź poziomu energii z pory dnia (spec 13.2 pkt 3).
    /// </summary>
    /// <remarks>
    /// Wersja z historii odhaczeń dopiero wtedy, gdy będzie historia. Na razie sama pora
    /// dnia: prosta, przewidywalna i możliwa do nadpisania jednym kliknięciem — co jest
    /// istotniejsze od trafności, bo podpowiedź, której nie da się odrzucić, przeszkadza.
    /// </remarks>
    public Energy SuggestEnergy() => clock.Now.Hour switch
    {
        >= 6 and < 12 => Energy.High,
        >= 12 and < 17 => Energy.Medium,
        _ => Energy.Low,
    };

    /// <summary>
    /// Punktacja z 8.1. Kolejność wag jest rozstrzygnięciem, nie strojeniem.
    /// </summary>
    /// <remarks>
    /// Termin bije wszystko, bo jest faktem zewnętrznym. Wybór na dziś jest drugi, bo to
    /// Twoja świeża decyzja. Waga ma celowo mało punktów wobec wyboru na dziś: nawet gdyby
    /// z czasem wszystko zrobiło się „wysokie", przesuwa to wynik nieznacznie — aplikacja
    /// słucha przede wszystkim decyzji z dzisiaj, a nie etykiety sprzed pół roku. To
    /// dlatego waga nie potrzebuje limitu (1.6): inflacja przestaje mieć skutki.
    /// </remarks>
    private static NowPick Score(TaskItem task, DateOnly today, bool unblocks)
    {
        var punkty = 0;
        string? powod = null;

        if (task.Deadline is { } termin && termin <= today.AddDays(2))
        {
            punkty += 100;
            powod = termin < today ? "po terminie" : "termin za chwilę";
        }

        if (task.FocusDate == today)
        {
            punkty += 60;
            powod ??= "wybrane na dziś";
        }

        if (task.Deadline is { } blizszy && blizszy <= today.AddDays(7))
        {
            punkty += 40;
            powod ??= "termin w tym tygodniu";
        }

        if (unblocks)
        {
            punkty += 25;
            powod ??= "odblokowuje projekt";
        }

        // Wiek w dniach, **przycięty do dwudziestu punktów**. Przycięcie jest istotne:
        // bez niego jedno zadanie sprzed roku zdominowałoby ekran na zawsze.
        var wiek = Math.Max(0, today.DayNumber - DateOnly.FromDateTime(task.CreatedAt.UtcDateTime).DayNumber);
        punkty += Math.Min(wiek, 20);

        if (task.Priority == Priority.High)
        {
            punkty += 15;
            powod ??= "wysoka waga";
        }

        if (task.EstimatedMinutes is <= 15)
        {
            punkty += 10;
            powod ??= "krótkie — łatwo zacząć";
        }

        // Powód podaje wiek **prawdziwy**, nie przycięty: przycięcie jest sposobem
        // liczenia punktów, a nie faktem o zadaniu. „Czeka 20 dni" przy zadaniu sprzed
        // roku byłoby zwyczajną nieprawdą na ekranie.
        return new NowPick(task, punkty, powod ?? $"czeka {wiek} dni");
    }
}
