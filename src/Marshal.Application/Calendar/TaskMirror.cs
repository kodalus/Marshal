using Marshal.Application.Abstractions;
using Marshal.Domain.Diagnostics;
using Marshal.Application.Repositories;
using Marshal.Domain.Tasks;

namespace Marshal.Application.Calendar;

/// <summary>
/// Zadanie udostępnione jako wydarzenie w podłączonym kalendarzu (spec 10.2).
/// </summary>
/// <remarks>
/// <para>
/// Po to, żeby ktoś, kto nie ma Marshala, widział u siebie to, co go dotyczy.
/// Udostępnia się pojedyncze zadania, nie całe obszary — obszar rodzinny mieści
/// i „odebrać dziecko", i „kupić prezent", a widzieć je mają różne osoby.
/// </para>
/// <para>
/// <b>Wysyłamy tylko to, co udostępnione.</b> Zadanie bez powiązania nie dotyka
/// kalendarza w żaden sposób, także wtedy, gdy zmienia się co sekundę. To jedyne
/// miejsce w aplikacji, w którym błąd psuje dane poza nią.
/// </para>
/// <para>
/// <b>Nie udaje, że się udało.</b> Gdy zapis do kalendarza padnie, powiązanie zostaje
/// takie, jakie było, a wyjątek idzie w górę. Ciche odpięcie zostawiłoby w cudzym
/// kalendarzu wydarzenie, o którym nikt już nie pamięta.
/// </para>
/// </remarks>
public sealed class TaskMirror(
    CalendarSyncService calendar,
    ITaskRepository tasks,
    IUnitOfWork unitOfWork,
    IHlcSource hlc,
    ISettings settings,
    IActivityLog dziennik) : ITaskMirror
{
    /// <summary>Ile trwa udostępnione zadanie bez podanej długości.</summary>
    private const int DomyslneMinuty = 30;

    /// <summary>Kalendarz, w którym zadania lądują domyślnie. Na potrzeby menu w oknie.</summary>
    public Guid? MainCalendarId => settings.MainCalendarId;

    public async Task PushAsync(TaskItem task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        // Zadanie, które przestało mieć dzień albo godzinę, przestaje być wydarzeniem.
        // Kalendarz nie ma jak pokazać „kiedyś w tym tygodniu", a zostawione w nim
        // wydarzenie kłamałoby o porze, której nikt nie wybrał.
        // Dawniej wymagana była godzina. Znaczyło to, że wszystko, co ma dzień, ale nie
        // ma pory — wzięte na dziś, „zrobić w czwartek" — istniało wyłącznie w Marshalu
        // i nikt poza nim tego nie widział.
        if (task.State == TaskState.Trashed || Dzien(task) is null)
        {
            await RemoveAsync(task, ct);
            return;
        }

        // Kalendarz zadania albo domyślny. To jest cała reguła: zadanie z godziną leci
        // do kalendarza głównego samo, a przeniesione gdzie indziej zostaje tam, gdzie
        // je przeniesiono. Bez domyślnego trzeba było pamiętać o udostępnieniu przy
        // każdym zadaniu z osobna — a synchronizacja, o której trzeba pamiętać, nie
        // jest synchronizacją.
        // Wskazanie rozstrzygane przed użyciem: zadanie udostępnione na drugim urządzeniu
        // przyjeżdża ze wskazaniem na **tamtejszy** wiersz kalendarza, a składanie
        // duplikatów robi z niego nagrobek. To jest stare imię tej samej rzeczy, nie brak.
        var wskazane = task.SharedCalendarId ?? settings.MainCalendarId;

        if (wskazane is { } surowy
            && await calendar.ZywyKalendarzAsync(surowy, ct) is { } zywy
            && zywy != surowy)
        {
            wskazane = zywy;
        }

        if (wskazane is not { } kalendarz)
        {
            // Zapisane, bo brak kalendarza głównego i awaria wysyłki wyglądają z zewnątrz
            // identycznie: zadanie jest w Marshalu, a w Google go nie ma. Pierwsze jest
            // do ustawienia w dwie sekundy, drugie do naprawienia w kodzie.
            await dziennik.RecordAsync(
                "Kalendarz: wysłanie zadania",
                $"{task.Title} — pominięte",
                ActivityLevel.Ok,
                "Nie ustawiono kalendarza głównego (Ustawienia → Kalendarze).");

            return;
        }

        try
        {
            var identyfikator = await calendar.SaveEventAsync(
                kalendarz, task.SharedEventId, Szkic(task), ct);

            if (identyfikator != task.SharedEventId || task.SharedCalendarId != kalendarz)
            {
                task.Share(kalendarz, identyfikator, hlc.Next());
                await unitOfWork.SaveChangesAsync(ct);
            }

            await dziennik.RecordAsync("Kalendarz: wysłanie zadania", task.Title);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await dziennik.RecordAsync(
                "Kalendarz: wysłanie zadania", task.Title, ActivityLevel.Problem,
                $"{e.GetType().Name}: {e.Message}");

            throw;
        }
    }

    /// <summary>
    /// Zdjęcie odbicia z kalendarza.
    /// </summary>
    /// <remarks>
    /// <b>Każde wyjście zostawia ślad.</b> Do dziś wysłanie zapisywało się w dzienniku,
    /// a zdjęcie nie zapisywało się wcale — więc z dziennika nie dało się odróżnić
    /// „skasowane" od „nigdy nie doszło do skutku". Przy wpisie, który został w cudzym
    /// kalendarzu po zadaniu wyrzuconym do kosza, jest to jedyne pytanie, jakie się
    /// zadaje, a odpowiedzi nie było nigdzie. Cisza jest tu gorsza od kłopotu: kłopot
    /// widać.
    /// </remarks>
    public async Task RemoveAsync(TaskItem task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.SharedCalendarId is not { } wskazany || task.SharedEventId is not { } wydarzenie)
        {
            // Bez wpisu: zadanie nieudostępnione przechodzi tędy przy każdym zapisie,
            // bo wysyłanie kieruje tu wszystko, co przestało mieć dzień. Nie ma czego
            // zdejmować i nie ma o czym pisać.
            return;
        }

        // Jak przy wysyłaniu: wskazanie na odrzucony duplikat to stare imię żyjącego
        // podłączenia. Bez tego zadania z drugiego urządzenia nie dawały się skasować,
        // bo kasowanie odbicia wywracało się na kalendarzu, którego „już nie ma".
        if (await calendar.ZywyKalendarzAsync(wskazany, ct) is not { } kalendarz)
        {
            // Podłączenia naprawdę nie ma — odbicia nie ma jak skasować, ale zadanie
            // ma przestać na nie wskazywać, inaczej próba wracałaby przy każdej zmianie.
            // Zapisane jako kłopot, bo to jest dokładnie ten przypadek, w którym wpis
            // zostaje w cudzym kalendarzu na zawsze: po zdjęciu wskazania nikt już nie
            // ma po czym poznać, że tam jest i skąd się wziął.
            task.Unshare(hlc.Next());
            await unitOfWork.SaveChangesAsync(ct);

            await dziennik.RecordAsync(
                "Kalendarz: skasowanie odbicia",
                task.Title,
                ActivityLevel.Problem,
                $"Kalendarza {wskazany} nie ma już na liście podłączonych — "
                + $"wydarzenie {wydarzenie} zostaje w nim i trzeba je skasować ręcznie.");

            return;
        }

        // Najpierw u źródła, potem u nas — jak przy każdym zapisie na zewnątrz.
        // Odwrotna kolejność zostawiałaby przy nieudanym kasowaniu wydarzenie,
        // do którego nie mamy już żadnego wskazania.
        await calendar.DeleteEventAsync(kalendarz, wydarzenie, ct);

        task.Unshare(hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);

        await dziennik.RecordAsync("Kalendarz: skasowanie odbicia", task.Title);
    }

    /// <summary>Ostatni kłopot z dokańczaniem. Do tego, żeby nie pisać go co minutę.</summary>
    private string? _ostatniKlopot;

    /// <summary>
    /// Dokończenie kasowań odbić, które się nie udały.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Odbicie jest robione <b>po</b> kliknięciu i nie ma trwałości: zadanie wyrzucone
    /// przy padniętej sieci albo tuż przed zamknięciem aplikacji zostawiało w kalendarzu
    /// wydarzenie, po którym nikt już nie sprzątał. Z zewnątrz wyglądało to najgorzej,
    /// jak może: wpis wisiał dalej, a że zadania po tej stronie już nie było, nie dawał
    /// się wyrzucić do kosza jak zadanie — trzeba go było kasować jak cudze wydarzenie,
    /// z potwierdzeniem.
    /// </para>
    /// <para>
    /// Kolejki w bazie nie trzeba było zakładać, bo <b>ona już tam jest</b>. Wyrzucone
    /// zadanie przestaje wskazywać na swoje odbicie dopiero wtedy, gdy zdjęcie się
    /// udało, więc para „wyrzucone, a wskazuje" to zapisany ślad po prośbie, która nie
    /// doszła do skutku. Zostawało ją tylko przeczytać.
    /// </para>
    /// <para>
    /// Kłopot zapisywany dopiero przy zmianie treści: dokańczanie chodzi co minutę,
    /// a sieć potrafi nie działać godzinami — ten sam wpis tysiąc razy wypchnąłby
    /// z dziennika wszystko inne i zamienił go w miejsce, do którego się nie zagląda.
    /// </para>
    /// </remarks>
    public async Task DokonczKasowaniaAsync(CancellationToken ct = default)
    {
        var zalegle = await tasks.PendingMirrorRemovalsAsync(ct);

        if (zalegle.Count == 0)
        {
            _ostatniKlopot = null;
            return;
        }

        foreach (var zadanie in zalegle)
        {
            try
            {
                await RemoveAsync(zadanie, ct);

                _ostatniKlopot = null;

                await dziennik.RecordAsync(
                    "Kalendarz: dokończone kasowanie odbicia", zadanie.Title);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                var tresc = $"{e.GetType().Name}: {e.Message}";

                if (_ostatniKlopot != tresc)
                {
                    _ostatniKlopot = tresc;

                    await dziennik.RecordAsync(
                        "Kalendarz: zaległe kasowanie odbicia",
                        zadanie.Title, ActivityLevel.Problem, tresc);
                }

                // Reszta zaległych zostaje na następny raz: skoro to nie poszło, kolejne
                // najpewniej też nie pójdą, a każde z nich to osobna wyprawa po sieci.
                return;
            }
        }
    }

    /// <summary>
    /// Przeniesienie zadania do wskazanego kalendarza. Oddaje powód odmowy albo nic.
    /// </summary>
    /// <remarks>
    /// Przeniesienie, nie dołożenie: zadanie stoi w jednym kalendarzu naraz. Stanie
    /// w dwóch znaczyłoby dwa wpisy na jedną rzecz w jednym widoku telefonu — i dwa
    /// miejsca, w których trzeba by je potem odhaczyć.
    /// </remarks>
    public async Task<string?> ShareAsync(Guid taskId, Guid calendarId, CancellationToken ct = default)
    {
        if (await tasks.FindAsync(taskId, ct) is not { } zadanie)
        {
            return null;
        }

        if ((await calendar.SourcesAsync(ct)).FirstOrDefault(z => z.Id == calendarId)
            is not { } zrodlo)
        {
            return "Tego kalendarza nie ma już na liście podłączonych.";
        }

        if (!calendar.CanWrite(zrodlo.Kind))
        {
            return $"Do kalendarza „{zrodlo.Name}” umiemy tylko czytać.";
        }

        // Dzień wystarczy. Dawniej wymagana była też godzina, bo bez niej nie było
        // wiadomo, gdzie postawić blok — ale zadanie bez pory nie jest blokiem, tylko
        // wpisem całodniowym, i to jest odpowiedź, której wtedy brakowało.
        if (Dzien(zadanie) is null)
        {
            return "Najpierw dzień — kalendarz nie ma gdzie postawić zadania bez daty.";
        }

        if (zadanie.SharedCalendarId == calendarId)
        {
            return null;
        }

        // Ze starego miejsca najpierw, żeby nie zostały dwa wpisy, gdyby zapis
        // w nowym padł. Kolejność odwrotna kosztowałaby duplikat w cudzym kalendarzu.
        await RemoveAsync(zadanie, ct);

        var identyfikator = await calendar.SaveEventAsync(calendarId, null, Szkic(zadanie), ct);

        zadanie.Share(calendarId, identyfikator, hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);

        return null;
    }

    /// <summary>
    /// Powrót zadania do kalendarza głównego.
    /// </summary>
    /// <remarks>
    /// „Przestaję to udostępniać" nie znaczy „odwołuję to": zadanie dalej jest do
    /// zrobienia i dalej ma stać w kalendarzu, do którego zaglądam sama. Znika tylko
    /// z tego wspólnego — czyli z widoku drugiej osoby, i to jest dokładnie ta zmiana,
    /// którą się właśnie postanowiło. Od odwoływania jest kosz.
    /// </remarks>
    public async Task UnshareAsync(Guid taskId, CancellationToken ct = default)
    {
        if (await tasks.FindAsync(taskId, ct) is not { IsShared: true } zadanie)
        {
            return;
        }

        if (settings.MainCalendarId is { } glowny && zadanie.SharedCalendarId != glowny)
        {
            await ShareAsync(taskId, glowny, ct);
            return;
        }

        // Bez kalendarza głównego nie ma dokąd wracać — zostaje samo zdjęcie wpisu.
        await RemoveAsync(zadanie, ct);
    }

    /// <summary>
    /// Zadanie w postaci wydarzenia.
    /// </summary>
    /// <remarks>
    /// Odhaczone niesie ptaszek w nazwie — tak samo jak wydarzenie odhaczone na siatce.
    /// Druga osoba widzi wtedy u siebie, że rzecz jest zrobiona, bez pytania.
    /// </remarks>
    /// <summary>Dzień, na który zadanie ma stanąć w kalendarzu. Puste znaczy „na żaden".</summary>
    /// <remarks>
    /// Dzień wykonania, a gdy go nie ma — dzień, na który zadanie zostało wybrane.
    /// Ta sama reguła co na siatce w Marshalu: zadanie umówione na czwartek i wzięte
    /// na dziś stoi w czwartek, bo tam jest umówione.
    /// </remarks>
    private static DateOnly? Dzien(TaskItem task) => task.DoDate ?? task.FocusDate;

    private CalendarDraft Szkic(TaskItem task)
    {
        var strefa = settings.Zone;
        var dzien = Dzien(task)!.Value;

        var nazwa = task.State == TaskState.Done
            ? EventMark.Apply(task.Title)
            : EventMark.Strip(task.Title);

        // Bez godziny — wydarzenie całodniowe. Zgadnięta godzina zrobiłaby z zadania
        // spotkanie: w cudzym kalendarzu wyglądałoby jak coś umówionego na ósmą rano,
        // czego nikt nie umawiał. Koniec dnia później, bo u Google koniec całodniowego
        // jest wyłączny — ten sam dzień w obu polach daje wydarzenie zerowej długości.
        if (task.DoTime is not { } pora)
        {
            var poczatek = new DateTimeOffset(dzien.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            return new CalendarDraft(nazwa, poczatek, poczatek.AddDays(1), AllDay: true);
        }

        var lokalny = dzien.ToDateTime(pora);
        var start = new DateTimeOffset(lokalny, strefa.GetUtcOffset(lokalny));
        var dlugosc = TimeSpan.FromMinutes(task.EstimatedMinutes ?? DomyslneMinuty);

        return new CalendarDraft(nazwa, start, start + dlugosc);
    }
}
