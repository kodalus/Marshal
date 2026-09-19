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

    /// <param name="includeSomeday">
    /// Czy sięgnąć także do „kiedyś-może".
    /// </param>
    /// <remarks>
    /// Domyślnie nie, i to jest rozstrzygnięcie, nie zaniedbanie: „kiedyś-może" jest
    /// z założenia **poza** systemem rzeczy do zrobienia i przegląda się je raz
    /// w tygodniu. Wpuszczone tu na stałe zrobiłyby z „Teraz" drugą listę wszystkiego,
    /// czyli dokładnie to, czym ten ekran nie ma być.
    ///
    /// Ale zadanie, któremu ktoś dopisał długość i poziom sił, jest już opisane tak,
    /// jak opisuje się rzeczy do zrobienia — i odmowa pokazania go, kiedy pyta się
    /// wprost, byłaby upieraniem się przy metodzie wbrew człowiekowi, który jej używa.
    /// Stąd przełącznik: granica zostaje, ale da się ją przekroczyć świadomie.
    /// </remarks>
    public async Task<IReadOnlyList<NowPick>> PickAsync(
        int availableMinutes, Energy energy, bool includeSomeday = false,
        CancellationToken ct = default)
    {
        var today = clock.Today;
        // Następne akcje **i** to, co zaplanowane na dziś albo wcześniej (spec 8.6).
        // Do dziś brane były wyłącznie „Następne", więc zadanie zaplanowane na dziś —
        // czyli to, na które się właśnie napisałaś — nie mogło się tu pojawić w ogóle.
        // Ekran „co teraz" bez rzeczy umówionych na dziś odpowiada na inne pytanie.
        var all = (await tasks.ByStateAsync(TaskState.Next, ct))
            .Concat((await tasks.ByStateAsync(TaskState.Scheduled, ct))
                .Where(t => t.DoDate is { } day && day <= today))
            .ToList();

        if (includeSomeday)
        {
            all.AddRange(await tasks.ByStateAsync(TaskState.Someday, ct));
        }

        // Projekt wstrzymany („kiedyś") albo zamknięty wyklucza swoje zadania: leżą
        // w bazie poprawnie, ale nie są tym, co można teraz zrobić.
        var live = (await projects.AllAsync(ct))
            .Where(p => p.State == ProjectState.Active && !p.Deleted)
            .Select(p => p.Id)
            .ToHashSet();

        var candidates = all
            .Where(t => t.DeferUntil is null || t.DeferUntil <= today)
            .Where(t => t.EstimatedMinutes is { } minutes && minutes <= availableMinutes)
            .Where(t => t.Energy == Energy.Unknown || t.Energy <= energy)
            .Where(t => t.ProjectId is null || live.Contains(t.ProjectId.Value))
            .ToList();

        // Zadanie, które jest jedyną akcją do przodu w swoim projekcie, odblokowuje go
        // — to jest odwrotna strona N1 i dlatego ma własne punkty.
        var unblocking = all
            .Where(t => t.ProjectId is not null)
            .GroupBy(t => t.ProjectId!.Value)
            .Where(g => g.Count() == 1)
            .Select(g => g.First().Id)
            .ToHashSet();

        return candidates
            .Select(t => Score(t, today, unblocking.Contains(t.Id)))
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.Task.CreatedAt)

            // Najwyżej jedno zadanie z jednego projektu. Pięć kroków tego samego projektu
            // wygląda jak praca, ale zamyka pole widzenia na resztę życia.
            .DistinctBy(p => p.Task.ProjectId ?? p.Task.Id)
            .Take(Slots)
            .ToList();
    }

    /// <summary>Ilu w ogóle jest kandydatów — bez tego nie da się zaproponować oszacowania.</summary>
    public async Task<int> UnestimatedCountAsync(CancellationToken ct = default)
    {
        var today = clock.Today;

        return (await tasks.ByStateAsync(TaskState.Next, ct))
            .Concat((await tasks.ByStateAsync(TaskState.Scheduled, ct))
                .Where(t => t.DoDate is { } day && day <= today))
            .Count(t => t.EstimatedMinutes is null);
    }

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
        var score = 0;
        string? reason = null;

        if (task.Deadline is { } deadline && deadline <= today.AddDays(2))
        {
            score += 100;
            reason = deadline < today ? "po terminie" : "termin za chwilę";
        }

        if (task.FocusDate == today)
        {
            score += 60;
            reason ??= "wybrane na dziś";
        }

        if (task.Deadline is { } closer && closer <= today.AddDays(7))
        {
            score += 40;
            reason ??= "termin w tym tygodniu";
        }

        if (unblocks)
        {
            score += 25;
            reason ??= "odblokowuje projekt";
        }

        // Wiek w dniach, **przycięty do dwudziestu punktów**. Przycięcie jest istotne:
        // bez niego jedno zadanie sprzed roku zdominowałoby ekran na zawsze.
        var age = Math.Max(0, today.DayNumber - DateOnly.FromDateTime(task.CreatedAt.UtcDateTime).DayNumber);
        score += Math.Min(age, 20);

        if (task.Priority == Priority.High)
        {
            score += 15;
            reason ??= "wysoka waga";
        }

        if (task.EstimatedMinutes is <= 15)
        {
            score += 10;
            reason ??= "krótkie — łatwo zacząć";
        }

        // Powód podaje wiek **prawdziwy**, nie przycięty: przycięcie jest sposobem
        // liczenia punktów, a nie faktem o zadaniu. „Czeka 20 dni" przy zadaniu sprzed
        // roku byłoby zwyczajną nieprawdą na ekranie.
        return new NowPick(task, score, reason ?? $"czeka {age} dni");
    }
}
