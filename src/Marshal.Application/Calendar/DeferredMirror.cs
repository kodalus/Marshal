using Marshal.Application.Abstractions;
using Marshal.Domain.Diagnostics;
using Marshal.Domain.Tasks;

namespace Marshal.Application.Calendar;

/// <summary>
/// Odbicie w kalendarzu robione <b>po</b> kliknięciu, a nie w jego trakcie.
/// </summary>
/// <remarks>
/// <para>
/// Wyrównanie odbicia idzie przez sieć do Google i trwa sekundę albo dwie. Dopóki było
/// robione wprost, tyle właśnie trwało odhaczenie zadania na kalendarzu: zapis lokalny
/// szedł w milisekundach, a ptaszek pojawiał się dopiero po powrocie z Google. Rzecz,
/// którą robi się kilkanaście razy dziennie jednym ruchem, nie może czekać na cudzy
/// serwer.
/// </para>
/// <para>
/// <b>Prawdą jest zadanie, wydarzenie jest jego cieniem</b> — i to rozstrzyga, co wolno
/// odłożyć. Zapis lokalny jest już zrobiony i to on jedzie synchronizacją; odbicie
/// dogania go chwilę później. Odwrotna kolejność nie wchodzi w grę i tu się nie zmienia:
/// przy zapisie wydarzenia <b>cudzego</b> kalendarza (CalendarSyncService) najpierw
/// idzie Google, potem baza.
/// </para>
/// <para>
/// <b>Jedno naraz i w kolejności zgłoszeń.</b> Dwie zmiany tego samego zadania puszczone
/// równolegle dotarłyby do Google w dowolnej kolejności i wygrałaby ta, która akurat
/// wróciła później. Do tego odbicie sięga do bazy, a kontekst bazy jest w tej aplikacji
/// pojedynczy — dwa odbicia naraz to ten sam kłopot, co dwa zapisy naraz.
/// </para>
/// <para>
/// <b>Kolejność zapewnia doczepianie, nie semafor.</b> Pierwsza wersja puszczała każdą
/// pracę osobno i kazała jej czekać na semafor. Semafor rzeczywiście przepuszczał jedną
/// naraz, ale <b>o kolejności decydowało to, która zdążyła po niego sięgnąć</b> — a to
/// wyznacza pula wątków, nie zgłaszający. Zadanie założone i zaraz skasowane potrafiło
/// więc pojechać do Google w kolejności „skasuj, wyślij": kasowanie nie zastawało jeszcze
/// żadnego wydarzenia i nie robiło nic, a wysłanie zakładało je chwilę później. W kalendarzu
/// zostawał wpis po zadaniu, którego już nie ma — i to po stronie Google, więc nie dawał
/// się wyrzucić do kosza jak zadanie, tylko trzeba go było kasować jak cudze wydarzenie.
/// Teraz każda praca czeka na <b>swoją poprzedniczkę</b>, a nie na wolny semafor, i ta
/// poprzedniczka jest wyznaczona w chwili zgłoszenia.
/// </para>
/// <para>
/// Czego to <b>nie</b> daje: trwałości. Zadanie odhaczone tuż przed zamknięciem
/// aplikacji może nie zdążyć wysłać ptaszka do Google. Kolejka w bazie rozwiązałaby to
/// w całości i jest do zrobienia wtedy, gdy okaże się potrzebna — na razie ceną jest
/// rzadki, kosmetyczny rozjazd w cudzym kalendarzu, a zyskiem interfejs, który odpowiada
/// od razu. Nieudane odbicie zostawia ślad w dzienniku, więc nie znika bez śladu.
/// </para>
/// </remarks>
public sealed class DeferredMirror(ITaskMirror mirror, IActivityLog journal) : ITaskMirror
{
    private readonly Lock _lock = new();

    /// <summary>Ostatnia zgłoszona praca. Następna doczepia się do niej.</summary>
    private Task _tail = Task.CompletedTask;

    public Task PushAsync(TaskItem task, CancellationToken ct = default)
    {
        Defer("Kalendarz: wysłanie zadania", task, (t, c) => mirror.PushAsync(t, c));

        return Task.CompletedTask;
    }

    public Task RemoveAsync(TaskItem task, CancellationToken ct = default)
    {
        Defer("Kalendarz: skasowanie odbicia", task, (t, c) => mirror.RemoveAsync(t, c));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Puszczenie pracy w tle, z której wyjątek ma dokąd trafić.
    /// </summary>
    /// <remarks>
    /// Bez znacznika odwołania z wołającego: praca ma dokończyć się także wtedy, gdy ten,
    /// kto ją zlecił, już się rozłączył — o to właśnie chodzi w odłożeniu. Odwołanie
    /// przekazane tutaj znaczyłoby odbicie przerwane w połowie przez zamknięcie ekranu.
    /// </remarks>
    private void Defer(string co, TaskItem task, Func<TaskItem, CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(task);

        var title = task.Title;

        // Bez dziedziczenia kontekstu wywołania. Brama na bazę pozna po nim, że dany
        // przepływ jest już w środku, i wpuści go ponownie bez czekania — a praca
        // puszczona w tle nie jest tym samym przepływem, tylko drugą ręką. Dziś
        // odkładanie zaczyna się poza bramą, więc różnicy nie widać; jutro wystarczy
        // jedno wywołanie wyżej, żeby zaczęło się w środku, i wtedy ta linijka jest
        // jedyną rzeczą stojącą między tym a odczytem w poprzek cudzego zapisu.
        using var withoutContext = ExecutionContext.SuppressFlow();

        lock (_lock)
        {
            // Poprzedniczka wyznaczona **tutaj**, czyli w kolejności zgłoszeń. To jest
            // cała różnica wobec semafora: tam kolejność wychodziła z tego, kto pierwszy
            // dobiegł, a tu jest ustalona, zanim cokolwiek ruszy.
            var previous = _tail;

            _tail = Task.Run(() => OneByOneAsync(previous, co, title, task, work));
        }
    }

    private async Task OneByOneAsync(
        Task previous,
        string co,
        string title,
        TaskItem task,
        Func<TaskItem, CancellationToken, Task> work)
    {
        // Poprzedniczka nigdy nie rzuca — wyjątek zostaje w niej, w dzienniku. Inaczej
        // jedno nieudane odbicie zrywałoby cały ogon kolejki.
        await previous;

        try
        {
            await work(task, CancellationToken.None);
        }
        catch (Exception e)
        {
            // Odbicie robione po fakcie nie ma komu oddać wyjątku: ekran dawno
            // odpowiedział. Dziennik jest tu jedyną drogą, żeby nieudane wysłanie
            // nie było nieodróżnialne od wysłanego.
            try
            {
                await journal.RecordAsync(
                    co, title, ActivityLevel.Problem, $"{e.GetType().Name}: {e.Message}");
            }
            catch
            {
                // Dziennik jest narzędziem do patrzenia, a nie częścią działania.
            }
        }
    }
}
