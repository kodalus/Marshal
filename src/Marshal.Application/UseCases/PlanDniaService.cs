using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Areas;
using Marshal.Domain.Projects;
using Marshal.Domain.Tasks;

namespace Marshal.Application.UseCases;

/// <summary>Jedna pozycja planu dnia — gotowa do narysowania, bez wiedzy o platformie.</summary>
/// <param name="Id">Zadanie, którego dotyczy — do odhaczenia z widgetu.</param>
/// <param name="Tytul">Nazwa zadania.</param>
/// <param name="Podpis">Druga linijka: godziny i przynależność.</param>
/// <param name="Barwa">Zapis barwy paska albo puste, gdy nic nie ustawiono.</param>
public sealed record PozycjaPlanu(Guid Id, string Tytul, string Podpis, string? Barwa);

/// <summary>
/// Plan dzisiejszego dnia: co jest umówione, co zaległe i co wzięte na dziś.
/// </summary>
/// <remarks>
/// <para>
/// Powstało dla widgetu, ale nie jest „usługą widgetu". Widget rozwija swoje wiersze
/// w procesie ekranu domowego i nie da się go uruchomić w teście; wszystko, co da się
/// z niego wyjąć, ma stąd być wyjęte — inaczej jedyną drogą sprawdzenia „czy plan ma
/// właściwą treść i kolejność" byłoby patrzenie na telefon.
/// </para>
/// <para>
/// Plan to zadania z dzisiejszym dniem wykonania i zaległe — ten sam zbiór, co ekran
/// „Dzisiaj" — <b>plus</b> wzięte na dziś. Zadanie potrafi być jednym i drugim naraz,
/// stąd odsiew po identyfikatorze.
/// </para>
/// <para>
/// <b>Bez odhaczonych.</b> Plan odpowiada na pytanie „co jeszcze przede mną", a nie
/// „co dziś było". Odhaczone na liście, po którą sięga się w biegu, zajmuje miejsce
/// rzeczy, która czeka — i to jest cała różnica między planem a dziennikiem.
/// Na siatce kalendarza zostają, bo tam pytanie brzmi inaczej: co się z dniem stało.
/// </para>
/// </remarks>
public sealed class PlanDniaService(
    ITaskRepository tasks,
    IProjectRepository projects,
    IAreaRepository areas,
    IClock clock)
{
    public async Task<IReadOnlyList<PozycjaPlanu>> DzisAsync(CancellationToken ct = default)
    {
        var dzis = clock.Today;

        var umowione = await tasks.TodayAsync(dzis, ct);
        var wybrane = await tasks.ByFocusDateAsync(dzis, ct);

        var razem = umowione
            .Concat(wybrane.Where(w => umowione.All(u => u.Id != w.Id)))
            .Where(z => z.State != TaskState.Done)
            .OrderBy(z => Pora(z, dzis) is null)
            .ThenBy(z => Pora(z, dzis))
            .ThenBy(z => z.Title, StringComparer.CurrentCulture)
            .ToList();

        if (razem.Count == 0)
        {
            return [];
        }

        var projektyWg = (await projects.AllAsync(ct)).ToDictionary(p => p.Id);
        var obszaryWg = (await areas.AllAsync(ct)).ToDictionary(o => o.Id);

        return razem
            .Select(z => new PozycjaPlanu(
                z.Id,
                z.Title,
                Podpis(z, dzis, Nalezy(z, projektyWg, obszaryWg)),
                Barwa(z, projektyWg, obszaryWg)))
            .ToList();
    }

    /// <summary>Godzina, o której to stoi w dzisiejszym planie. Pusta, gdy bez godziny.</summary>
    /// <remarks>
    /// Wyłącznie dla dnia dzisiejszego. Zadanie zaległe ma godzinę sprzed paru dni
    /// i wstawiona między dzisiejsze udawałaby, że jest na nią umówione dziś.
    /// </remarks>
    private static TimeOnly? Pora(TaskItem zadanie, DateOnly dzis) =>
        zadanie.DoDate == dzis ? zadanie.DoTime : null;

    private static string Podpis(TaskItem zadanie, DateOnly dzis, string? gdzie)
    {
        var czesci = new List<string>();

        if (Pora(zadanie, dzis) is { } pora)
        {
            // Koniec liczony z oszacowania, gdy jest. „16:00 – 16:30" mówi, ile dnia
            // to zajmie; samo „16:00" zostawia to do policzenia w głowie.
            czesci.Add(zadanie.EstimatedMinutes is { } minut && minut > 0
                ? $"{Godzina(pora)} – {Godzina(pora.AddMinutes(minut))}"
                : Godzina(pora));
        }
        else if (zadanie.DoDate is { } dzien && dzien < dzis)
        {
            czesci.Add($"zaległe z {dzien:d.MM}");
        }
        else if (zadanie.FocusDate == dzis)
        {
            czesci.Add("wzięte na dziś");
        }

        if (gdzie is not null)
        {
            czesci.Add(gdzie);
        }

        return string.Join(" / ", czesci);
    }

    /// <summary>Godzina jako „16:00".</summary>
    /// <remarks>
    /// Niezmiennicza, nie lokalna: dwukropek jest tu <b>znakiem</b>, a nie separatorem
    /// do podmiany. Kultura systemowa potrafi wstawić w to miejsce kropkę albo
    /// dwunastkę z „PM", a plan ma wyglądać tak samo jak siatka kalendarza obok.
    /// </remarks>
    private static string Godzina(TimeOnly pora) =>
        $"{pora.Hour:D2}:{pora.Minute:D2}";

    private static string? Nalezy(
        TaskItem zadanie,
        IReadOnlyDictionary<Guid, Project> projekty,
        IReadOnlyDictionary<Guid, Area> obszary)
    {
        if (zadanie.ProjectId is { } projekt && projekty.TryGetValue(projekt, out var p))
        {
            return p.Outcome;
        }

        return zadanie.AreaId is { } obszar && obszary.TryGetValue(obszar, out var o)
            ? o.Name
            : null;
    }

    /// <summary>
    /// Barwa paska: zadania, a gdy go nie ma — projektu, a gdy i tego nie ma — obszaru.
    /// Ta sama zasada, co na siatce kalendarza.
    /// </summary>
    private static string? Barwa(
        TaskItem zadanie,
        IReadOnlyDictionary<Guid, Project> projekty,
        IReadOnlyDictionary<Guid, Area> obszary)
    {
        if (!string.IsNullOrWhiteSpace(zadanie.Color))
        {
            return zadanie.Color;
        }

        if (zadanie.ProjectId is { } projekt
            && projekty.TryGetValue(projekt, out var p)
            && !string.IsNullOrWhiteSpace(p.Color))
        {
            return p.Color;
        }

        return zadanie.AreaId is { } obszar
            && obszary.TryGetValue(obszar, out var o)
            && !string.IsNullOrWhiteSpace(o.Color)
                ? o.Color
                : null;
    }
}
