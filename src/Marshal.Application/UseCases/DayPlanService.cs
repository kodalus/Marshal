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
public sealed record PlanRow(
    Guid? TaskId,
    string Title,
    string Caption,
    string? Color,

    /// <summary>Kalendarz, z którego wpis pochodzi. Puste przy zadaniach Marshala.</summary>
    Guid? SourceId = null,

    /// <summary>Identyfikator u źródła — bez niego nie da się tam nic zmienić.</summary>
    string? ExternalId = null,

    /// <summary>Czy da się zapisać zmianę tam, skąd wpis pochodzi.</summary>
    bool CanWrite = false,

    /// <summary>
    /// Zadanie niosące rytm — gdy wiersz jest wystąpieniem narysowanym do przodu.
    /// </summary>
    /// <remarks>
    /// Takie wystąpienie jeszcze nie istnieje: powstanie przy odhaczeniu poprzedniego.
    /// Nie ma więc czego odhaczyć na kafelku, a dotknięcie prowadzi na siatkę tego dnia,
    /// na którym stoi — bo to o ten dzień się pyta, patrząc na widget.
    /// </remarks>
    Guid? RhythmId = null)
{
    /// <summary>Czy wiersz jest wystąpieniem rytmu narysowanym do przodu.</summary>
    public bool IsAhead => RhythmId is not null;

    /// <summary>
    /// Co da się odhaczyć wprost z kafelka: własne zadanie i wydarzenie z kalendarza,
    /// do którego umiemy pisać.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ta sama zasada co na siatce w oknie</b> i to jest tu cała rzecz. Kwadracik
    /// przy wpisie w oknie, a przy tym samym wpisie na kafelku już nie, znaczy dla ręki,
    /// że jedno z dwóch jest zepsute — i nie ma jak zgadnąć które.
    /// </para>
    /// <para>
    /// Wydarzenie nie ma u nas pola „zrobione": ptaszek idzie do jego nazwy w Google.
    /// Bez prawa zapisu nie miałby więc gdzie wylądować i kwadracik się nie pokazuje —
    /// przycisk, który nic nie robi, jest gorszy od jego braku. Kanał iCal jest tylko
    /// do odczytu i to jest właśnie ten przypadek.
    /// </para>
    /// </remarks>
    public bool CanComplete =>
        TaskId is not null || (CanWrite && SourceId is not null && ExternalId is not null);
}

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
public sealed class DayPlanService(
    ITaskRepository tasks,
    IProjectRepository projects,
    IAreaRepository areas,
    IClock clock,
    CalendarSyncService? calendar = null)
{
    public Task<IReadOnlyList<PlanRow>> TodayAsync(CancellationToken ct = default) =>
        ForDayAsync(clock.Today, ct);

    /// <summary>
    /// Które dni z podanego zakresu mają cokolwiek w planie — na kropki paska tygodnia.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Na kropki paska tygodnia w widgecie: kafelek ma jednym spojrzeniem mówić, gdzie
    /// w tygodniu jest gęsto, a gdzie pusto. Bez tego pasek pokazywałby same numery
    /// i żeby dowiedzieć się czegokolwiek, trzeba by dotknąć każdego dnia po kolei.
    /// </para>
    /// <para>
    /// <b>Nie przez plan dnia.</b> Najpierw stało tu siedem pełnych planów — po jednym
    /// na dzień. Każdy sięgał po zadania, wybory, projekty, obszary i siatkę kalendarza,
    /// budował wiersze z podpisami i barwami, sortował je, a odpowiedź z tego wszystkiego
    /// brzmiała „pusto czy nie". Trzydzieści pięć zapytań na siedem pytań tak–nie, przy
    /// każdym przerysowaniu kafelka, w odbiorniku rozgłoszenia, któremu Android liczy
    /// czas. Teraz są cztery zapytania na cały zakres.
    /// </para>
    /// <para>
    /// Cena jest taka, że „dzień zajęty" ma tu drugą definicję i może rozjechać się
    /// z listą pod kropką. Dlatego jest ona wypisana wprost — dzień zajmuje zadanie
    /// z dniem wykonania albo terminem, wybór na ten dzień, zaległe (należące do dzisiaj,
    /// nie do dnia, w którym miały być), wydarzenie z kalendarza inne niż odbicie zadania
    /// oraz wystąpienie rytmu narysowane do przodu; odhaczone nie liczy się tak samo jak
    /// w planie — a test porównuje obie definicje dzień po dniu, żeby rozjazd wyszedł
    /// w CI, nie na kafelku.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlySet<DateOnly>> BusyAsync(
        DateOnly od, int days, CancellationToken ct = default)
    {
        var busy = new HashSet<DateOnly>();

        if (days <= 0)
        {
            return busy;
        }

        var today = clock.Today;
        var last = od.AddDays(days - 1);

        void Mark(DateOnly day)
        {
            if (day >= od && day <= last)
            {
                busy.Add(day);
            }
        }

        // Dzień i termin osobno: składnica oddaje zadanie, gdy w oknie jest którekolwiek
        // z nich, a plan dnia pokazuje je w obu tych dniach. Zapalenie kropki tylko po
        // dniu wykonania gasiłoby dni, w których stoi sam termin.
        foreach (var task in await tasks.UpcomingAsync(od.AddDays(-1), last, ct))
        {
            if (task.State == TaskState.Done)
            {
                continue;
            }

            if (task.DoDate is { } doDate)
            {
                Mark(doDate);
            }

            if (task.Deadline is { } deadline)
            {
                Mark(deadline);
            }
        }

        // Wybór na dzień nie jest umówieniem i nie ma dnia wykonania, ale dzień zajmuje
        // tak samo — w planie dnia stoi obok terminów. Górna granica jest wyłączna,
        // stąd dzień po ostatnim.
        foreach (var task in await tasks.FocusedBetweenAsync(od, last.AddDays(1), ct))
        {
            if (task.State != TaskState.Done && task.FocusDate is { } day)
            {
                Mark(day);
            }
        }

        // Zaległe należą do dzisiaj, nie do dnia, w którym miały być — tak samo jak
        // w planie dnia. Pytane tylko wtedy, gdy dzisiaj jest w zakresie i jeszcze nie
        // ma kropki, bo to jedyne zapytanie, którego nie da się zadać dla całego okna.
        if (today >= od && today <= last && !busy.Contains(today)
            && (await tasks.TodayAsync(today, ct)).Any(z => z.State != TaskState.Done))
        {
            Mark(today);
        }

        // Jedna siatka na cały zakres zamiast siedmiu jednodniowych.
        if (calendar is not null)
        {
            foreach (var day in await calendar.AgendaAsync(od, days, ct))
            {
                // Ten sam przesiew co w planie dnia — dosłownie ta sama funkcja, bo obie
                // definicje „dzień zajęty" muszą się zgadzać dzień po dniu.
                var anything = day.AllDay.Any(Shown) || day.Timed.Any(s => Shown(s.Entry));

                if (anything)
                {
                    Mark(day.Date);
                }
            }
        }

        return busy;
    }

    /// <summary>Plan dowolnego dnia — do przeglądania w przód i wstecz.</summary>
    /// <remarks>
    /// Dziś różni się od pozostałych dni jednym: bierze także <b>zaległe</b>. Zaległe
    /// należą do dzisiejszego dnia, bo to dziś trzeba z nimi coś zrobić; dołożone do
    /// czwartku udawałyby, że ktoś je na czwartek zaplanował.
    /// </remarks>
    public async Task<IReadOnlyList<PlanRow>> ForDayAsync(
        DateOnly day, CancellationToken ct = default)
    {
        var today = clock.Today;

        var upcoming = day == today
            ? await tasks.TodayAsync(today, ct)
            : await tasks.UpcomingAsync(day.AddDays(-1), day, ct);

        var selected = await tasks.ByFocusDateAsync(day, ct);

        var total = upcoming
            .Concat(selected.Where(w => upcoming.All(u => u.Id != w.Id)))
            .Where(z => z.State != TaskState.Done)
            .ToList();

        var projectsById = (await projects.AllAsync(ct)).ToDictionary(p => p.Id);
        var areasById = (await areas.AllAsync(ct)).ToDictionary(o => o.Id);

        var rows = total
            .Select(z => (
                Time: Time(z, day),
                Entry: new PlanRow(
                    z.Id,
                    z.Title,
                    Caption(z, day, today, Belongs(z, projectsById, areasById)),
                    Color(z, projectsById, areasById))))
            .Concat(await EventsAsync(day, ct))
            .OrderBy(p => p.Time is null)
            .ThenBy(p => p.Time)
            .ThenBy(p => p.Entry.Title, StringComparer.CurrentCulture)
            .ToList();

        return rows.Select(p => p.Entry).ToList();
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
    private async Task<IEnumerable<(TimeOnly? Time, PlanRow Entry)>> EventsAsync(
        DateOnly day, CancellationToken ct)
    {
        if (calendar is null)
        {
            return [];
        }

        var days = await calendar.AgendaAsync(day, 1, ct);

        if (days.Count == 0)
        {
            return [];
        }

        var gridDay = days[0];

        var allDay = gridDay.AllDay
            .Where(Shown)
            .Select(e => ((TimeOnly?)null, new PlanRow(
                null, e.Title, "cały dzień", e.Color, e.SourceId, e.ExternalId, e.CanWrite,
                e.RhythmId)));

        var withHour = gridDay.Timed
            .Select(s => s.Entry)
            .Where(Shown)
            .Select(e => (
                (TimeOnly?)TimeOnly.FromTimeSpan(e.Start.TimeOfDay),
                new PlanRow(
                    null,
                    e.Title,
                    $"{Hour(TimeOnly.FromTimeSpan(e.Start.TimeOfDay))} – "
                        + $"{Hour(TimeOnly.FromTimeSpan(e.End.TimeOfDay))}",
                    e.Color,
                    e.SourceId,
                    e.ExternalId,
                    e.CanWrite,
                    e.RhythmId)));

        return allDay.Concat(withHour);
    }

    /// <summary>
    /// Co z siatki wchodzi do planu dnia: wydarzenia i wystąpienia rytmu narysowane
    /// do przodu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zadania są w planie już z innej strony — po składnicy — i wzięte stąd drugi raz
    /// stałyby na kafelku podwójnie. Wystąpienia rytmu **nie są jeszcze zadaniami**,
    /// więc składnica ich nie zna i tędy wchodzą po raz pierwszy.
    /// </para>
    /// <para>
    /// Bez nich widget odpowiadał na pytanie „co mam dziś zapisane", a kalendarz na
    /// pytanie „co mam dziś" — i te dwie odpowiedzi rozjeżdżały się dokładnie o rytm,
    /// czyli o to, co powtarza się najczęściej.
    /// </para>
    /// </remarks>
    private static bool Shown(AgendaEntry entry) =>
        !entry.IsDone && (entry.Kind == AgendaKind.Event || entry.IsAhead);

    /// <summary>Godzina, o której to stoi w dzisiejszym planie. Pusta, gdy bez godziny.</summary>
    /// <remarks>
    /// Wyłącznie dla dnia, który się ogląda. Zadanie zaległe ma godzinę sprzed paru dni
    /// i wstawiona między dzisiejsze udawałaby, że jest na nią umówione dziś.
    /// </remarks>
    private static TimeOnly? Time(TaskItem task, DateOnly day) =>
        task.DoDate == day ? task.DoTime : null;

    private static string Caption(TaskItem task, DateOnly day, DateOnly today, string? where)
    {
        var parts = new List<string>();

        if (Time(task, day) is { } time)
        {
            // Koniec liczony z oszacowania, gdy jest. „16:00 – 16:30" mówi, ile dnia
            // to zajmie; samo „16:00" zostawia to do policzenia w głowie.
            parts.Add(task.EstimatedMinutes is { } minutes && minutes > 0
                ? $"{Hour(time)} – {Hour(time.AddMinutes(minutes))}"
                : Hour(time));
        }
        else if (task.DoDate is { } deadline && deadline < day)
        {
            parts.Add($"zaległe z {deadline:d.MM}");
        }
        else if (task.FocusDate == day)
        {
            parts.Add(day == today ? "wzięte na dziś" : "wzięte na ten dzień");
        }

        if (where is not null)
        {
            parts.Add(where);
        }

        return string.Join(" / ", parts);
    }

    /// <summary>Godzina jako „16:00".</summary>
    /// <remarks>
    /// Niezmiennicza, nie lokalna: dwukropek jest tu <b>znakiem</b>, a nie separatorem
    /// do podmiany. Kultura systemowa potrafi wstawić w to miejsce kropkę albo
    /// dwunastkę z „PM", a plan ma wyglądać tak samo jak siatka kalendarza obok.
    /// </remarks>
    private static string Hour(TimeOnly time) =>
        $"{time.Hour:D2}:{time.Minute:D2}";

    private static string? Belongs(
        TaskItem task,
        IReadOnlyDictionary<Guid, Project> projects,
        IReadOnlyDictionary<Guid, Area> areas)
    {
        if (task.ProjectId is { } project && projects.TryGetValue(project, out var p))
        {
            return p.Outcome;
        }

        return task.AreaId is { } area && areas.TryGetValue(area, out var o)
            ? o.Name
            : null;
    }

    /// <summary>
    /// Barwa paska: zadania, a gdy go nie ma — projektu, a gdy i tego nie ma — obszaru.
    /// Ta sama zasada, co na siatce kalendarza.
    /// </summary>
    private static string? Color(
        TaskItem task,
        IReadOnlyDictionary<Guid, Project> projects,
        IReadOnlyDictionary<Guid, Area> areas)
    {
        if (!string.IsNullOrWhiteSpace(task.Color))
        {
            return task.Color;
        }

        if (task.ProjectId is { } project
            && projects.TryGetValue(project, out var p)
            && !string.IsNullOrWhiteSpace(p.Color))
        {
            return p.Color;
        }

        return task.AreaId is { } area
            && areas.TryGetValue(area, out var o)
            && !string.IsNullOrWhiteSpace(o.Color)
                ? o.Color
                : null;
    }
}
