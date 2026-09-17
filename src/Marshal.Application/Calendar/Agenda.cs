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

    /// <summary>Czy zadanie jest już odhaczone. Ptaszek należy do pola, nie do nazwy.</summary>
    bool IsDone = false)
{
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

        var wszystkie = entries.ToList();
        var wynik = new List<AgendaDay>(days);

        for (var i = 0; i < days; i++)
        {
            var dzien = from.AddDays(i);

            var calodniowe = wszystkie
                .Where(e => e.IsAllDay && Covers(e, dzien))
                .OrderBy(e => e.Title, StringComparer.Ordinal)
                .ToList();

            var godzinowe = wszystkie
                .Where(e => !e.IsAllDay && Covers(e, dzien))
                .Select(e => ClipTo(e, dzien))
                .OrderBy(e => e.Start)
                .ThenByDescending(e => e.End)
                .ToList();

            wynik.Add(new AgendaDay(dzien, calodniowe, Layout(godzinowe)));
        }

        return wynik;
    }

    /// <summary>
    /// Czy wpis dotyka tego dnia. Koniec równy północy **nie liczy się** jako następny
    /// dzień — spotkanie do 24:00 kończy się dziś, a nie zaczyna jutro.
    /// </summary>
    private static bool Covers(AgendaEntry entry, DateOnly day)
    {
        var poczatek = DateOnly.FromDateTime(entry.Start.DateTime);
        var koniec = DateOnly.FromDateTime(entry.End.DateTime);

        if (entry.End.TimeOfDay == TimeSpan.Zero && koniec > poczatek)
        {
            koniec = koniec.AddDays(-1);
        }

        return day >= poczatek && day <= koniec;
    }

    /// <summary>
    /// Przycięcie do granic dnia. Wydarzenie od 23:00 do 01:00 pojawia się na obu dniach,
    /// na każdym jako jego własny kawałek — inaczej na siatce drugiego dnia zaczynałoby
    /// się „minus godzinę temu".
    /// </summary>
    private static AgendaEntry ClipTo(AgendaEntry entry, DateOnly day)
    {
        var poczatekDnia = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), entry.Start.Offset);
        var koniecDnia = poczatekDnia.AddDays(1);

        var start = entry.Start < poczatekDnia ? poczatekDnia : entry.Start;
        var koniec = entry.End > koniecDnia ? koniecDnia : entry.End;

        return entry with { Start = start, End = koniec };
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
        var sloty = new List<AgendaSlot>(timed.Count);
        var grono = new List<(AgendaEntry Entry, int Column)>();
        var konceKolumn = new List<DateTimeOffset>();
        var najdalszyKoniec = DateTimeOffset.MinValue;

        void ZamknijGrono()
        {
            foreach (var (wpis, kolumna) in grono)
            {
                sloty.Add(new AgendaSlot(wpis, kolumna, konceKolumn.Count));
            }

            grono.Clear();
            konceKolumn.Clear();
            najdalszyKoniec = DateTimeOffset.MinValue;
        }

        foreach (var wpis in timed)
        {
            if (grono.Count > 0 && wpis.Start >= najdalszyKoniec)
            {
                ZamknijGrono();
            }

            // Pierwsza kolumna, która zdążyła się zwolnić. Bez tego dwa krótkie
            // spotkania jedno po drugim zajmowałyby dwie kolumny, choć nie kolidują.
            var kolumna = konceKolumn.FindIndex(koniec => koniec <= wpis.Start);

            if (kolumna < 0)
            {
                kolumna = konceKolumn.Count;
                konceKolumn.Add(wpis.End);
            }
            else
            {
                konceKolumn[kolumna] = wpis.End;
            }

            grono.Add((wpis, kolumna));

            if (wpis.End > najdalszyKoniec)
            {
                najdalszyKoniec = wpis.End;
            }
        }

        ZamknijGrono();
        return sloty;
    }
}
