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
    IActivityLog journal,
    IAreaRepository areas) : ITaskMirror
{
    /// <summary>Ile trwa udostępnione zadanie bez podanej długości.</summary>
    private const int DefaultMinutes = 30;

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
        if (task.State == TaskState.Trashed || Day(task) is null)
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
        var chosen = task.SharedCalendarId ?? await CalendarForAreaAsync(task, ct);

        if (chosen is { } raw
            && await calendar.LiveCalendarAsync(raw, ct) is { } live
            && live != raw)
        {
            chosen = live;
        }

        if (chosen is not { } calendarId)
        {
            // Zapisane, bo brak kalendarza głównego i awaria wysyłki wyglądają z zewnątrz
            // identycznie: zadanie jest w Marshalu, a w Google go nie ma. Pierwsze jest
            // do ustawienia w dwie sekundy, drugie do naprawienia w kodzie.
            await journal.RecordAsync(
                "Kalendarz: wysłanie zadania",
                $"{task.Title} — pominięte",
                ActivityLevel.Ok,
                "Nie ustawiono kalendarza głównego (Ustawienia → Kalendarze).");

            return;
        }

        try
        {
            var id = await calendar.SaveEventAsync(
                calendarId, task.SharedEventId, Draft(task), ct);

            if (id != task.SharedEventId || task.SharedCalendarId != calendarId)
            {
                task.Share(calendarId, id, hlc.Next());
                await unitOfWork.SaveChangesAsync(ct);
            }

            await journal.RecordAsync("Kalendarz: wysłanie zadania", task.Title);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await journal.RecordAsync(
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
    /// <summary>
    /// Kalendarz, do którego należy zadanie z tego obszaru.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Obszar wskazuje kalendarz, więc zadanie z obszaru „Dzieci" ląduje w kalendarzu
    /// rodzinnym samo, bez ustawiania czegokolwiek przy zadaniu. Dotąd wszystko szło
    /// do jednego kalendarza głównego: zadanie z pracy i odbiór dziecka z przedszkola
    /// lądowały obok siebie w tym samym wspólnym kalendarzu, więc udostępnianie było
    /// wszystkim-albo-nic i w praktyce znaczyło „nic".
    /// </para>
    /// <para>
    /// Główny zostaje jako spad dla obszarów, którym nie przypisano kalendarza, i dla
    /// zadań bez obszaru — czyli dla wrzutów, których jeszcze nikt nie rozstrzygnął.
    /// </para>
    /// </remarks>
    private async Task<Guid?> CalendarForAreaAsync(TaskItem task, CancellationToken ct)
    {
        if (task.AreaId is { } area
            && await areas.FindAsync(area, ct) is { CalendarId: { } calendarId })
        {
            return calendarId;
        }

        return settings.MainCalendarId;
    }

    public async Task RemoveAsync(TaskItem task, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.SharedCalendarId is not { } pointed || task.SharedEventId is not { } ev)
        {
            // Bez wpisu: zadanie nieudostępnione przechodzi tędy przy każdym zapisie,
            // bo wysyłanie kieruje tu wszystko, co przestało mieć dzień. Nie ma czego
            // zdejmować i nie ma o czym pisać.
            return;
        }

        // Jak przy wysyłaniu: wskazanie na odrzucony duplikat to stare imię żyjącego
        // podłączenia. Bez tego zadania z drugiego urządzenia nie dawały się skasować,
        // bo kasowanie odbicia wywracało się na kalendarzu, którego „już nie ma".
        if (await calendar.LiveCalendarAsync(pointed, ct) is not { } calendarId)
        {
            // Podłączenia naprawdę nie ma — odbicia nie ma jak skasować, ale zadanie
            // ma przestać na nie wskazywać, inaczej próba wracałaby przy każdej zmianie.
            // Zapisane jako kłopot, bo to jest dokładnie ten przypadek, w którym wpis
            // zostaje w cudzym kalendarzu na zawsze: po zdjęciu wskazania nikt już nie
            // ma po czym poznać, że tam jest i skąd się wziął.
            task.Unshare(hlc.Next());
            await unitOfWork.SaveChangesAsync(ct);

            await journal.RecordAsync(
                "Kalendarz: skasowanie odbicia",
                task.Title,
                ActivityLevel.Problem,
                $"Kalendarza {pointed} nie ma już na liście podłączonych — "
                + $"wydarzenie {ev} zostaje w nim i trzeba je skasować ręcznie.");

            return;
        }

        // Najpierw u źródła, potem u nas — jak przy każdym zapisie na zewnątrz.
        // Odwrotna kolejność zostawiałaby przy nieudanym kasowaniu wydarzenie,
        // do którego nie mamy już żadnego wskazania.
        await calendar.DeleteEventAsync(calendarId, ev, ct);

        task.Unshare(hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);

        await journal.RecordAsync("Kalendarz: skasowanie odbicia", task.Title);
    }

    /// <summary>Ostatni kłopot z dokańczaniem. Do tego, żeby nie pisać go co minutę.</summary>
    private string? _lastTrouble;

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
    public async Task FinishDeletionAsync(CancellationToken ct = default)
    {
        var overdue = await tasks.PendingMirrorRemovalsAsync(ct);

        if (overdue.Count == 0)
        {
            _lastTrouble = null;
            return;
        }

        foreach (var task in overdue)
        {
            try
            {
                await RemoveAsync(task, ct);

                _lastTrouble = null;

                await journal.RecordAsync(
                    "Kalendarz: dokończone kasowanie odbicia", task.Title);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                var content = $"{e.GetType().Name}: {e.Message}";

                if (_lastTrouble != content)
                {
                    _lastTrouble = content;

                    await journal.RecordAsync(
                        "Kalendarz: zaległe kasowanie odbicia",
                        task.Title, ActivityLevel.Problem, content);
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
        if (await tasks.FindAsync(taskId, ct) is not { } task)
        {
            return null;
        }

        if ((await calendar.SourcesAsync(ct)).FirstOrDefault(z => z.Id == calendarId)
            is not { } source)
        {
            return "Tego kalendarza nie ma już na liście podłączonych.";
        }

        if (!calendar.CanWrite(source.Kind))
        {
            return $"Do kalendarza „{source.Name}” umiemy tylko czytać.";
        }

        // Dzień wystarczy. Dawniej wymagana była też godzina, bo bez niej nie było
        // wiadomo, gdzie postawić blok — ale zadanie bez pory nie jest blokiem, tylko
        // wpisem całodniowym, i to jest odpowiedź, której wtedy brakowało.
        if (Day(task) is null)
        {
            return "Najpierw dzień — kalendarz nie ma gdzie postawić zadania bez daty.";
        }

        if (task.SharedCalendarId == calendarId)
        {
            return null;
        }

        // Ze starego miejsca najpierw, żeby nie zostały dwa wpisy, gdyby zapis
        // w nowym padł. Kolejność odwrotna kosztowałaby duplikat w cudzym kalendarzu.
        await RemoveAsync(task, ct);

        var id = await calendar.SaveEventAsync(calendarId, null, Draft(task), ct);

        task.Share(calendarId, id, hlc.Next());
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
        if (await tasks.FindAsync(taskId, ct) is not { IsShared: true } task)
        {
            return;
        }

        // Wraca do kalendarza swojego obszaru, a gdy ten go nie ma — do głównego.
        if (await CalendarForAreaAsync(task, ct) is { } own
            && task.SharedCalendarId != own)
        {
            await ShareAsync(taskId, own, ct);
            return;
        }

        // Bez kalendarza obszaru i bez głównego nie ma dokąd wracać — zostaje zdjęcie wpisu.
        await RemoveAsync(task, ct);
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
    private static DateOnly? Day(TaskItem task) => task.DoDate ?? task.FocusDate;

    private CalendarDraft Draft(TaskItem task)
    {
        var zone = settings.Zone;
        var day = Day(task)!.Value;

        var name = task.State == TaskState.Done
            ? EventMark.Apply(task.Title)
            : EventMark.Strip(task.Title);

        // Bez godziny — wydarzenie całodniowe. Zgadnięta godzina zrobiłaby z zadania
        // spotkanie: w cudzym kalendarzu wyglądałoby jak coś umówionego na ósmą rano,
        // czego nikt nie umawiał. Koniec dnia później, bo u Google koniec całodniowego
        // jest wyłączny — ten sam dzień w obu polach daje wydarzenie zerowej długości.
        if (task.DoTime is not { } time)
        {
            var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            return new CalendarDraft(name, start, start.AddDays(1), AllDay: true);
        }

        var local = day.ToDateTime(time);
        var start = new DateTimeOffset(local, zone.GetUtcOffset(local));
        var length = TimeSpan.FromMinutes(task.EstimatedMinutes ?? DefaultMinutes);

        return new CalendarDraft(name, start, start + length);
    }
}
