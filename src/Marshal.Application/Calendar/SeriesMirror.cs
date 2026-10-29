using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Diagnostics;
using Marshal.Domain.Series;
using Marshal.Domain.Tasks;

namespace Marshal.Application.Calendar;

/// <summary>Odbicie serii w kalendarzu — jedno wydarzenie cykliczne na serię.</summary>
public interface ISeriesMirror
{
    Task PushAsync(TaskSeries series, CancellationToken ct = default);

    Task RemoveAsync(TaskSeries series, CancellationToken ct = default);

    /// <summary>Serie, które odbicia jeszcze nie mają — po kilka na przebieg.</summary>
    Task<int> CatchUpAsync(CancellationToken ct = default);
}

/// <summary>
/// Seria jako <b>jedno wydarzenie cykliczne</b> w podłączonym kalendarzu.
/// </summary>
/// <remarks>
/// <para>
/// Dni z cyklu nie mają własnych odbić i to jest sedno tego rozwiązania. Osobne
/// wydarzenie na każdy dzień znaczyłoby sześćdziesiąt wypraw po sieci na każdą serię
/// przy pierwszym postawieniu okna, sześćdziesiąt poprawek przy zmianie nazwy,
/// sześćdziesiąt skasowań przy zmianie rytmu — a przede wszystkim <b>dwa wydarzenia
/// na ten sam dzień</b>: pulpit i telefon stawiają okno niezależnie, więc dopóki
/// wskazanie na wydarzenie nie dojedzie synchronizacją, każdy z nich utworzyłby własne.
/// Przy jednym wydarzeniu na serię to pytanie znika, bo oba urządzenia wysyłają
/// dokładnie to samo.
/// </para>
/// <para>
/// <b>Rozwijaniem powtórzeń zajmuje się Google</b> — tak samo jak przy odczycie, gdzie
/// pytamy je o gotowe wystąpienia, a nie o reguły. Nasza siatka rysuje przy tym własne
/// dni, więc wystąpienia wracające z Google muszą być odsiewane po wydarzeniu
/// macierzystym; robi to CalendarSyncService.
/// </para>
/// <para>
/// <b>Nie udaje, że się udało.</b> Gdy zapis padnie, powiązanie zostaje takie, jakie
/// było, a wyjątek idzie w górę — jak przy zadaniu udostępnionym i z tego samego
/// powodu: to jedyne miejsce w aplikacji, w którym błąd psuje dane poza nią.
/// </para>
/// </remarks>
public sealed class SeriesMirror(
    CalendarSyncService calendar,
    ITaskSeriesRepository series,
    ITaskMirror days,
    IUnitOfWork unitOfWork,
    IClock clock,
    IHlcSource hlc,
    ISettings settings,
    IActivityLog journal,
    IAreaRepository areas) : ISeriesMirror
{
    /// <summary>Ile trwa dzień z cyklu bez podanej długości.</summary>
    private const int DefaultMinutes = 30;

    /// <summary>Ile serii wolno wysłać w jednym przebiegu dociągania.</summary>
    /// <remarks>
    /// Każda to jedna wyprawa po sieci, a dociąganie chodzi z minutnika: trzy na minutę
    /// znaczy, że kilkanaście serii ma odbicie po kilku minutach od uruchomienia, bez
    /// kolejki kilkunastu żądań naraz przy starcie — kiedy aplikacja ma co innego do
    /// roboty.
    /// </remarks>
    private const int Batch = 3;

    /// <summary>
    /// Serie, których nie udało się nigdzie postawić.
    /// </summary>
    /// <remarks>
    /// Bez tego dociąganie wracałoby co minutę do serii, dla której nie ma kalendarza —
    /// i co minutę zapisywało w dzienniku to samo zdanie, wypychając z niego wszystko
    /// inne. Pamięć sięga do zamknięcia aplikacji, bo to jest dokładnie ten czas,
    /// w którym odpowiedź może się zmienić: kalendarz główny wskazuje się w ustawieniach
    /// i wtedy warto spróbować jeszcze raz.
    /// </remarks>
    private readonly HashSet<Guid> _hopeless = [];

    /// <remarks>
    /// Serie przeniesione ze starego modelu nie mają odbicia i nic ich do niego nie
    /// popchnie: wysyłka siedzi przy zmianie serii, a tych nikt nie zmienia. Bez
    /// dociągania „Praca, dni robocze" trafiłaby do Google dopiero wtedy, gdyby ktoś
    /// otworzył jej kartę i nacisnął zapis.
    /// </remarks>
    public async Task<int> CatchUpAsync(CancellationToken ct = default)
    {
        var pending = (await series.ListAsync(ct))
            .Where(z => z.SharedEventId is null && !_hopeless.Contains(z.Id))
            .Take(Batch)
            .ToList();

        foreach (var one in pending)
        {
            // Wywrotka zjadana **tutaj**, a nie puszczana wyżej, i seria odkładana na
            // bok. Bez tego jedna seria, której Google nie przyjmuje, wracała co minutę
            // z tym samym komunikatem i dwoma wpisami w dzienniku — po godzinie dziennik
            // nie zawierał już nic poza nią. Powód jest zapisany raz, w PushAsync, a
            // kolejna próba należy do następnego uruchomienia: do tego czasu i tak nic
            // się nie zmieni.
            try
            {
                await PushAsync(one, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _hopeless.Add(one.Id);
                continue;
            }

            if (one.SharedEventId is null)
            {
                _hopeless.Add(one.Id);
            }
        }

        return pending.Count;
    }

    public async Task PushAsync(TaskSeries one, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(one);

        var template = one.Template;

        // Dni serii pod ręką raz: stąd bierze się i lista odwołanych, i sprzątanie
        // po starych odbiciach pojedynczych dni na końcu.
        var standing = await series.OccurrencesAsync(one.Id, ct);

        var lines = SeriesRrule.Lines(
            one.Rule, Last(one), template.DoTime, settings.Zone, Cancelled(one, standing));

        // Rytmu liczonego od wykonania nie da się zapisać regułą: nie ma dat, dopóki
        // poprzednie wystąpienie nie zostanie odhaczone. Wysłanie czegokolwiek byłoby
        // tu rozkładem, którego nikt nie obiecał — więc nie jedzie nic, a zapisane
        // wcześniej odbicie schodzi.
        if (lines is null)
        {
            await RemoveAsync(one, ct);

            await journal.RecordAsync(
                "Kalendarz: wysłanie serii",
                $"{template.Title} — pominięta",
                ActivityLevel.Ok,
                "Rytm liczony od wykonania nie ma dat do wysłania: kolejny dzień "
                + "wyznacza się dopiero przy odhaczeniu poprzedniego.",
                ct);

            return;
        }

        if (Zone() is null)
        {
            await journal.RecordAsync(
                "Kalendarz: wysłanie serii",
                $"{template.Title} — pominięta",
                ActivityLevel.Problem,
                $"Strefy „{settings.Zone.Id}” nie da się przełożyć na nazwę, którą zna "
                + "Google. Wydarzenie cykliczne bez strefy rozwijałoby się w złej porze, "
                + "więc nie jedzie nic.",
                ct);

            return;
        }

        var chosen = one.SharedCalendarId ?? await CalendarForAreaAsync(template.AreaId, ct);

        // Wskazanie rozstrzygane przed użyciem: dwa wiersze na ten sam kalendarz robią
        // się przy pierwszej synchronizacji między urządzeniami, a składanie duplikatów
        // robi z jednego z nich nagrobek. To jest stare imię tej samej rzeczy, nie brak.
        if (chosen is { } raw
            && await calendar.LiveCalendarAsync(raw, ct) is { } live
            && live != raw)
        {
            chosen = live;
        }

        if (chosen is not { } calendarId)
        {
            await journal.RecordAsync(
                "Kalendarz: wysłanie serii",
                $"{template.Title} — pominięta",
                ActivityLevel.Ok,
                "Nie ustawiono kalendarza głównego (Ustawienia → Kalendarze).",
                ct);

            return;
        }

        // Tożsamość wyliczona z serii — zob. SeriesEventId. Podawana **przy zakładaniu**,
        // bo tylko tam jest czym rozstrzygnąć wyścig dwóch urządzeń; przy łataniu
        // tożsamość wskazuje adres żądania, a podana w treści znaczyłaby prośbę o jej
        // zmianę.
        var wanted = SeriesEventId.For(one.Id);

        try
        {
            string id;

            if (one.SharedEventId is { Length: > 0 } standing)
            {
                try
                {
                    id = await calendar.SaveEventAsync(
                        calendarId, standing, Draft(one, lines), ct);
                }
                catch (EventGone)
                {
                    // Wydarzenia, na które wskazywaliśmy, już nie ma — ktoś skasował je
                    // w kalendarzu. Zakładamy je od nowa i tym razem pod tożsamością
                    // wyliczoną, żeby nie zależało to od tego, które urządzenie zdążyło.
                    id = await calendar.SaveEventAsync(
                        calendarId, null, Draft(one, lines, wanted), ct);
                }
            }
            else
            {
                id = await calendar.SaveEventAsync(
                    calendarId, null, Draft(one, lines, wanted), ct);
            }

            if (id != one.SharedEventId || one.SharedCalendarId != calendarId)
            {
                one.Share(calendarId, id, hlc.Next());
                await unitOfWork.SaveChangesAsync(ct);
            }

            await journal.RecordAsync("Kalendarz: wysłanie serii", template.Title);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await journal.RecordAsync(
                "Kalendarz: wysłanie serii", template.Title, ActivityLevel.Problem,
                $"{e.GetType().Name}: {e.Message}");

            throw;
        }

        await DropDayMirrorsAsync(standing, ct);
    }

    /// <summary>
    /// Dni, które rytm wyznacza, a których w serii nie ma.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dwie drogi prowadzą do tego samego: „pomiń to wystąpienie" stawia nagrobek na
    /// dniu, który w oknie stoi, a „odwołaj wystąpienie" zapisuje odwołanie w regule,
    /// przy dniu, którego jeszcze nie ma. Pierwsza daje wiersz ze stanem wyrzuconego,
    /// druga — zmianę w regule; w kalendarzu obie znaczą „tego dnia nie będzie".
    /// </para>
    /// <para>
    /// <b>Tylko dni z okna i tylko od dziś.</b> Nagrobek zdarza się także wtedy, gdy
    /// okno zabiera dzień, którego już nie chce — a taki dzień nie jest odwołany, tylko
    /// przestał wypadać z rytmu, więc reguła sama go nie wyznaczy i wyjmowanie go byłoby
    /// mówieniem Google o niczym. Przeszłość zostaje nietknięta: wystąpienia, które się
    /// odbyły, są zapisem tego, co było.
    /// </para>
    /// </remarks>
    private IReadOnlyList<DateOnly> Cancelled(TaskSeries one, IReadOnlyList<TaskItem> standing)
    {
        var today = clock.Today;
        var window = SeriesWindow.Plan(one, today).Select(z => z.Date).ToHashSet();

        var skipped = standing
            .Where(z => z.State == TaskState.Trashed
                     && z.DoDate is { } day
                     && window.Contains(day))
            .Select(z => z.DoDate!.Value);

        var dropped = one.Rule.Changes
            .Where(z => z.Dropped && z.Date >= today)
            .Select(z => z.Date);

        return [.. skipped.Concat(dropped).Distinct().Order()];
    }

    /// <summary>
    /// Zdjęcie odbić pojedynczych dni tej serii.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sprzątanie po czasie, w którym dzień z cyklu dostawał w Google własne wydarzenie:
    /// każdy dotknięty z ręki, a po odhaczeniu także każdy odhaczony. Zostawione obok
    /// wydarzenia cyklicznego byłyby dokładnie tym podwojeniem, przed którym cała ta
    /// zmiana ma chronić — tym razem widocznym u każdego, kto ten kalendarz ogląda.
    /// </para>
    /// <para>
    /// Pętla jest tania w stanie docelowym: po pierwszym sprzątnięciu żaden dzień nie
    /// ma już wskazania, więc zostaje sam przebieg po liście i zero wypraw po sieci.
    /// </para>
    /// </remarks>
    private async Task DropDayMirrorsAsync(IReadOnlyList<TaskItem> standing, CancellationToken ct)
    {
        foreach (var day in standing)
        {
            if (day.SharedEventId is { Length: > 0 })
            {
                await days.RemoveAsync(day, ct);
            }
        }
    }

    public async Task RemoveAsync(TaskSeries one, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(one);

        if (one.SharedCalendarId is not { } pointed || one.SharedEventId is not { } ev)
        {
            return;
        }

        if (await calendar.LiveCalendarAsync(pointed, ct) is not { } calendarId)
        {
            // Podłączenia naprawdę nie ma — wydarzenia nie ma jak skasować, ale seria
            // ma przestać na nie wskazywać, inaczej próba wracałaby przy każdej zmianie.
            one.Unshare(hlc.Next());
            await unitOfWork.SaveChangesAsync(ct);

            await journal.RecordAsync(
                "Kalendarz: skasowanie odbicia serii",
                one.Template.Title,
                ActivityLevel.Problem,
                $"Kalendarza {pointed} nie ma już na liście podłączonych — "
                + $"wydarzenie {ev} zostaje w nim i trzeba je skasować ręcznie.",
                ct);

            return;
        }

        // Najpierw u źródła, potem u nas — jak przy każdym zapisie na zewnątrz.
        await calendar.DeleteEventAsync(calendarId, ev, ct);

        one.Unshare(hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);

        await journal.RecordAsync("Kalendarz: skasowanie odbicia serii", one.Template.Title);
    }

    /// <summary>
    /// Ostatni dzień serii — data końca z reguły albo koniec policzonego okna.
    /// </summary>
    /// <remarks>
    /// Licznik pozostałych wystąpień nie przechodzi do reguły iCal wprost: tamtejsze
    /// <c>COUNT</c> liczy od początku serii, a nasz licznik mówi, ile <b>jeszcze</b>.
    /// Zamiast go tłumaczyć, pytamy okno o ostatni dzień, który wylicza — to jest ta
    /// sama odpowiedź, tylko podana datą, i wychodzi z tego samego kodu, który stawia
    /// dni w bazie.
    /// </remarks>
    private DateOnly? Last(TaskSeries one)
    {
        if (one.Rule.Until is { } until)
        {
            return until;
        }

        if (one.Rule.Count is null)
        {
            return null;
        }

        var plan = SeriesWindow.Plan(one, clock.Today);

        return plan.Count > 0 ? plan[^1].Date : one.Starts;
    }

    /// <summary>Kalendarz obszaru, a gdy go nie ma — główny. Jak przy zadaniu.</summary>
    private async Task<Guid?> CalendarForAreaAsync(Guid? area, CancellationToken ct)
    {
        if (area is { } mine && await areas.FindAsync(mine, ct) is { CalendarId: { } calendarId })
        {
            return calendarId;
        }

        return settings.MainCalendarId;
    }

    /// <summary>
    /// Wydarzenie z szablonu serii: pierwszy dzień jako początek, rytm jako reguła.
    /// </summary>
    /// <remarks>
    /// Początkiem jest <see cref="TaskSeries.Starts"/>, bo to jest pierwsze wystąpienie
    /// serii — także wtedy, gdy rytm tygodniowy nie wypadałby tego dnia. Reguła iCal
    /// działa tu tak samo jak nasze okno: dzień początku należy do rytmu z definicji,
    /// a nie dlatego, że pasuje do wzoru.
    /// </remarks>
    private CalendarDraft Draft(TaskSeries one, IReadOnlyList<string> lines, string? id = null)
    {
        var template = one.Template;
        var name = EventMark.Strip(template.Title);
        var day = one.Starts;
        var zone = Zone();

        // Bez godziny — wydarzenie całodniowe. Zgadnięta godzina zrobiłaby z cyklu
        // spotkanie o ósmej rano, którego nikt nie umawiał. Koniec dnia później, bo
        // u Google koniec całodniowego jest wyłączny.
        if (template.DoTime is not { } time)
        {
            var dayStart = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

            return new CalendarDraft(
                name, dayStart, dayStart.AddDays(1),
                AllDay: true, Recurrence: lines, TimeZone: zone, Id: id);
        }

        var local = day.ToDateTime(time);
        var start = new DateTimeOffset(local, settings.Zone.GetUtcOffset(local));
        var length = TimeSpan.FromMinutes(template.EstimatedMinutes ?? DefaultMinutes);

        return new CalendarDraft(
            name, start, start + length, Recurrence: lines, TimeZone: zone, Id: id);
    }

    private string? Zone() => SeriesRrule.IanaName(settings.Zone);
}
