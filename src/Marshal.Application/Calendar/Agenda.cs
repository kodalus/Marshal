namespace Marshal.Application.Calendar;

public enum AgendaKind
{
    /// <summary>Wydarzenie z kalendarza — pełne, nieprzezroczyste (spec 11).</summary>
    Event = 0,

    /// <summary>Zadanie — półprzezroczyste, bo jest zamiarem, a nie umową.</summary>
    Task = 1,
}

/// <summary>Jedna rzecz na siatce, już przycięta do dnia, na którym leży.</summary>
public sealed record AgendaEntry(
    string Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsAllDay,
    AgendaKind Kind,
    string? Color,
    Guid? TaskId,

    /// <summary>Kalendarz, z którego wpis pochodzi. Puste przy zadaniach Marshala.</summary>
    Guid? SourceId = null,

    /// <summary>Identyfikator u źródła — bez niego nie da się tam nic zmienić.</summary>
    string? ExternalId = null,

    /// <summary>Czy wpis jest już odhaczony. Ptaszek należy do pola, nie do nazwy.</summary>
    bool IsDone = false,

    /// <summary>
    /// Czy da się zapisać zmianę tam, skąd wpis pochodzi.
    /// </summary>
    /// <remarks>
    /// Liczone tutaj, a nie w oknie: to, czy kalendarz ma pisarza, jest wiedzą warstwy
    /// aplikacji, a okno musiałoby po nią sięgać osobno przy każdym rysowaniu siatki.
    /// Przy zadaniach Marshala zawsze prawda — własne zadania zapisujemy u siebie.
    /// </remarks>
    bool CanWrite = false,

    /// <summary>
    /// Zadanie niosące rytm — gdy wpis jest jego wystąpieniem narysowanym do przodu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Takie wystąpienie **nie istnieje jeszcze jako zadanie**: powstaje dopiero wtedy,
    /// gdy poprzednie zostanie odhaczone. Rysunek rytmu, nie rzecz — dlatego nie ma
    /// własnego identyfikatora i nie da się go ani odhaczyć, ani przenieść.
    /// </para>
    /// <para>
    /// Identyfikator serii zamiast samej flagi, bo dotknięcie takiego wystąpienia ma
    /// dokąd prowadzić: do zadania, które niesie regułę — czyli tam, gdzie rytm da się
    /// obejrzeć i zmienić. Flaga mówiłaby „tego nie dotykaj" i kończyła rozmowę.
    /// </para>
    /// </remarks>
    Guid? RhythmId = null)
{
    /// <summary>Czy wpis jest wystąpieniem narysowanym do przodu, a nie zadaniem.</summary>
    public bool IsAhead => RhythmId is not null;

    public double StartHour => Start.TimeOfDay.TotalHours;

    public double EndHour => End.TimeOfDay.TotalHours is var h && h <= StartHour ? 24 : h;

    /// <summary>Wysokość w godzinach. Najmniej kwadrans, żeby dało się w to trafić palcem.</summary>
    public double Hours => Math.Max(0.25, EndHour - StartHour);
}

/// <summary>Pozycja wpisu w kolumnie: która i z ilu.</summary>
public sealed record AgendaSlot(AgendaEntry Entry, int Column, int Columns);

/// <summary>Jeden dzień siatki: pasek całodniowy u góry i rzeczy o godzinach pod nim.</summary>
public sealed record AgendaDay(
    DateOnly Date,
    IReadOnlyList<AgendaEntry> AllDay,
    IReadOnlyList<AgendaSlot> Timed);

/// <summary>
/// Układanie siatki godzinowej (spec 11, ekran „Kalendarz").
/// </summary>
/// <remarks>
/// Funkcja czysta: dostaje wpisy i zakres dni, oddaje gotowy układ. Nakładanie się
/// wydarzeń i przejścia przez północ da się dzięki temu sprawdzić wprost, bez rysowania
/// czegokolwiek — a to są dokładnie te dwie rzeczy, które w kalendarzach wychodzą źle
/// i widać to dopiero okiem.
/// </remarks>
public static class Agenda
{
    public static IReadOnlyList<AgendaDay> Build(
        IEnumerable<AgendaEntry> entries, DateOnly from, int days)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(days);

        var all = entries.ToList();
        var result = new List<AgendaDay>(days);

        for (var i = 0; i < days; i++)
        {
            var day = from.AddDays(i);

            var allDay = all
                .Where(e => e.IsAllDay && Covers(e, day))
                .OrderBy(e => e.Title, StringComparer.Ordinal)
                .ToList();

            var hourly = all
                .Where(e => !e.IsAllDay && Covers(e, day))
                .Select(e => ClipTo(e, day))
                .OrderBy(e => e.Start)
                .ThenByDescending(e => e.End)
                .ToList();

            result.Add(new AgendaDay(day, allDay, Layout(hourly)));
        }

        return result;
    }

    /// <summary>
    /// Czy wpis dotyka tego dnia. Koniec równy północy **nie liczy się** jako następny
    /// dzień — spotkanie do 24:00 kończy się dziś, a nie zaczyna jutro.
    /// </summary>
    private static bool Covers(AgendaEntry entry, DateOnly day)
    {
        var start = DateOnly.FromDateTime(entry.Start.DateTime);
        var end = DateOnly.FromDateTime(entry.End.DateTime);

        if (entry.End.TimeOfDay == TimeSpan.Zero && end > start)
        {
            end = end.AddDays(-1);
        }

        return day >= start && day <= end;
    }

    /// <summary>
    /// Przycięcie do granic dnia. Wydarzenie od 23:00 do 01:00 pojawia się na obu dniach,
    /// na każdym jako jego własny kawałek — inaczej na siatce drugiego dnia zaczynałoby
    /// się „minus godzinę temu".
    /// </summary>
    private static AgendaEntry ClipTo(AgendaEntry entry, DateOnly day)
    {
        var dayStart = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), entry.Start.Offset);
        var dayEnd = dayStart.AddDays(1);

        var start = entry.Start < dayStart ? dayStart : entry.Start;
        var end = entry.End > dayEnd ? dayEnd : entry.End;

        return entry with { Start = start, End = end };
    }

    /// <summary>
    /// Kolumny dla nakładających się wpisów.
    /// </summary>
    /// <remarks>
    /// Wpisy dzielone są na grona rzeczy, które się ze sobą stykają, i dopiero w gronie
    /// liczona jest liczba kolumn. Liczenie kolumn dla całego dnia rozrzedziłoby poranne
    /// spotkanie na jedną trzecią szerokości tylko dlatego, że wieczorem coś się nakłada.
    /// </remarks>
    private static List<AgendaSlot> Layout(List<AgendaEntry> timed)
    {
        var slots = new List<AgendaSlot>(timed.Count);
        var cluster = new List<(AgendaEntry Entry, int Column)>();
        var columnEnds = new List<DateTimeOffset>();
        var furthestEnd = DateTimeOffset.MinValue;

        void CloseCluster()
        {
            foreach (var (entry, column) in cluster)
            {
                slots.Add(new AgendaSlot(entry, column, columnEnds.Count));
            }

            cluster.Clear();
            columnEnds.Clear();
            furthestEnd = DateTimeOffset.MinValue;
        }

        foreach (var entry in timed)
        {
            if (cluster.Count > 0 && entry.Start >= furthestEnd)
            {
                CloseCluster();
            }

            // Pierwsza kolumna, która zdążyła się zwolnić. Bez tego dwa krótkie
            // spotkania jedno po drugim zajmowałyby dwie kolumny, choć nie kolidują.
            var column = columnEnds.FindIndex(end => end <= entry.Start);

            if (column < 0)
            {
                column = columnEnds.Count;
                columnEnds.Add(entry.End);
            }
            else
            {
                columnEnds[column] = entry.End;
            }

            cluster.Add((entry, column));

            if (entry.End > furthestEnd)
            {
                furthestEnd = entry.End;
            }
        }

        CloseCluster();
        return slots;
    }
}
