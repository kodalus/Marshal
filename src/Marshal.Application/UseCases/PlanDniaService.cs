using Marshal.Application.Abstractions;
using Marshal.Application.Calendar;
using Marshal.Application.Repositories;
using Marshal.Domain.Areas;
using Marshal.Domain.Projects;
using Marshal.Domain.Tasks;

namespace Marshal.Application.UseCases;

/// <summary>Jedna pozycja planu dnia — gotowa do narysowania, bez wiedzy o platformie.</summary>
/// <param name="Zadanie">
/// Zadanie, którego dotyczy — do odhaczenia z widgetu. <b>Puste przy wydarzeniu
/// z kalendarza zewnętrznego</b>: tam nie ma czego odhaczyć jednym dotknięciem, bo
/// ptaszek idzie do cudzego kalendarza przez sieć, a widget nie ma jak poczekać.
/// </param>
/// <param name="Tytul">Nazwa zadania albo wydarzenia.</param>
/// <param name="Podpis">Druga linijka: godziny i przynależność.</param>
/// <param name="Barwa">Zapis barwy paska albo puste, gdy nic nie ustawiono.</param>
public sealed record PozycjaPlanu(Guid? Zadanie, string Tytul, string Podpis, string? Barwa);

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
    IClock clock,
    CalendarSyncService? kalendarz = null)
{
    public Task<IReadOnlyList<PozycjaPlanu>> DzisAsync(CancellationToken ct = default) =>
        DlaDniaAsync(clock.Today, ct);

    /// <summary>
    /// Które dni w podanym zakresie mają cokolwiek w planie.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Na kropki paska tygodnia w widgecie: kafelek ma jednym spojrzeniem mówić, gdzie
    /// w tygodniu jest gęsto, a gdzie pusto. Bez tego pasek pokazywałby same numery
    /// i żeby dowiedzieć się czegokolwiek, trzeba by dotknąć każdego dnia po kolei.
    /// </para>
    /// <para>
    /// <b>Ten sam plan, zapytany siedem razy</b>, a nie osobne, tańsze zapytanie.
    /// Tańsze byłoby drugą definicją tego, co znaczy „dzień zajęty" — a dwie definicje
    /// rozjeżdżają się przy pierwszej zmianie w jednej z nich i kropka zaczyna kłamać
    /// względem listy pod nią. Zakres to tydzień, baza jest lokalna i mała, a kafelek
    /// przerysowuje się po zmianie, nie w pętli.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlySet<DateOnly>> ZajeteAsync(
        DateOnly od, int dni, CancellationToken ct = default)
    {
        var zajete = new HashSet<DateOnly>();

        for (var i = 0; i < dni; i++)
        {
            var dzien = od.AddDays(i);

            if ((await DlaDniaAsync(dzien, ct)).Count > 0)
            {
                zajete.Add(dzien);
            }
        }

        return zajete;
    }

    /// <summary>Plan dowolnego dnia — do przeglądania w przód i wstecz.</summary>
    /// <remarks>
    /// Dziś różni się od pozostałych dni jednym: bierze także <b>zaległe</b>. Zaległe
    /// należą do dzisiejszego dnia, bo to dziś trzeba z nimi coś zrobić; dołożone do
    /// czwartku udawałyby, że ktoś je na czwartek zaplanował.
    /// </remarks>
    public async Task<IReadOnlyList<PozycjaPlanu>> DlaDniaAsync(
        DateOnly dzien, CancellationToken ct = default)
    {
        var dzis = clock.Today;

        var umowione = dzien == dzis
            ? await tasks.TodayAsync(dzis, ct)
            : await tasks.UpcomingAsync(dzien.AddDays(-1), dzien, ct);

        var wybrane = await tasks.ByFocusDateAsync(dzien, ct);

        var razem = umowione
            .Concat(wybrane.Where(w => umowione.All(u => u.Id != w.Id)))
            .Where(z => z.State != TaskState.Done)
            .ToList();

        var projektyWg = (await projects.AllAsync(ct)).ToDictionary(p => p.Id);
        var obszaryWg = (await areas.AllAsync(ct)).ToDictionary(o => o.Id);

        var pozycje = razem
            .Select(z => (
                Pora: Pora(z, dzien),
                Wpis: new PozycjaPlanu(
                    z.Id,
                    z.Title,
                    Podpis(z, dzien, dzis, Nalezy(z, projektyWg, obszaryWg)),
                    Barwa(z, projektyWg, obszaryWg))))
            .Concat(await WydarzeniaAsync(dzien, ct))
            .OrderBy(p => p.Pora is null)
            .ThenBy(p => p.Pora)
            .ThenBy(p => p.Wpis.Tytul, StringComparer.CurrentCulture)
            .ToList();

        return pozycje.Select(p => p.Wpis).ToList();
    }

    /// <summary>
    /// Wydarzenia z podłączonych kalendarzy, wplecione w plan dnia.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bez nich widget odpowiadał na pytanie „co mam dziś <b>w Marshalu</b>", a nie
    /// „co mam dziś" — czyli na pytanie, którego się nie zadaje. Wizyta u lekarza wpisana
    /// w Google zajmuje dzień tak samo jak zadanie i plan, który ją pomija, kłamie o tym,
    /// ile zostało czasu.
    /// </para>
    /// <para>
    /// Przez siatkę kalendarza, nie wprost po składnicy wydarzeń: to ona odsiewa odbicia
    /// zadań udostępnionych. Zadanie z godziną ma w Google swoje wydarzenie, a wzięte
    /// stamtąd wprost stałoby na widgecie dwa razy — raz jako zadanie, raz jako jego cień.
    /// </para>
    /// <para>
    /// Odhaczone pomijane tak samo jak zadania: plan mówi, co jeszcze przed tobą.
    /// </para>
    /// </remarks>
    private async Task<IEnumerable<(TimeOnly? Pora, PozycjaPlanu Wpis)>> WydarzeniaAsync(
        DateOnly dzien, CancellationToken ct)
    {
        if (kalendarz is null)
        {
            return [];
        }

        var dni = await kalendarz.AgendaAsync(dzien, 1, ct);

        if (dni.Count == 0)
        {
            return [];
        }

        var dzienSiatki = dni[0];

        var calodniowe = dzienSiatki.AllDay
            .Where(e => e.Kind == AgendaKind.Event && !e.IsDone)
            .Select(e => ((TimeOnly?)null, new PozycjaPlanu(null, e.Title, "cały dzień", e.Color)));

        var zGodzina = dzienSiatki.Timed
            .Select(s => s.Entry)
            .Where(e => e.Kind == AgendaKind.Event && !e.IsDone)
            .Select(e => (
                (TimeOnly?)TimeOnly.FromTimeSpan(e.Start.TimeOfDay),
                new PozycjaPlanu(
                    null,
                    e.Title,
                    $"{Godzina(TimeOnly.FromTimeSpan(e.Start.TimeOfDay))} – "
                        + $"{Godzina(TimeOnly.FromTimeSpan(e.End.TimeOfDay))}",
                    e.Color)));

        return calodniowe.Concat(zGodzina);
    }

    /// <summary>Godzina, o której to stoi w dzisiejszym planie. Pusta, gdy bez godziny.</summary>
    /// <remarks>
    /// Wyłącznie dla dnia, który się ogląda. Zadanie zaległe ma godzinę sprzed paru dni
    /// i wstawiona między dzisiejsze udawałaby, że jest na nią umówione dziś.
    /// </remarks>
    private static TimeOnly? Pora(TaskItem zadanie, DateOnly dzien) =>
        zadanie.DoDate == dzien ? zadanie.DoTime : null;

    private static string Podpis(TaskItem zadanie, DateOnly dzien, DateOnly dzis, string? gdzie)
    {
        var czesci = new List<string>();

        if (Pora(zadanie, dzien) is { } pora)
        {
            // Koniec liczony z oszacowania, gdy jest. „16:00 – 16:30" mówi, ile dnia
            // to zajmie; samo „16:00" zostawia to do policzenia w głowie.
            czesci.Add(zadanie.EstimatedMinutes is { } minut && minut > 0
                ? $"{Godzina(pora)} – {Godzina(pora.AddMinutes(minut))}"
                : Godzina(pora));
        }
        else if (zadanie.DoDate is { } termin && termin < dzien)
        {
            czesci.Add($"zaległe z {termin:d.MM}");
        }
        else if (zadanie.FocusDate == dzien)
        {
            czesci.Add(dzien == dzis ? "wzięte na dziś" : "wzięte na ten dzień");
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
