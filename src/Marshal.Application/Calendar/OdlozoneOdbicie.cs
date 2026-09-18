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
/// Czego to <b>nie</b> daje: trwałości. Zadanie odhaczone tuż przed zamknięciem
/// aplikacji może nie zdążyć wysłać ptaszka do Google. Kolejka w bazie rozwiązałaby to
/// w całości i jest do zrobienia wtedy, gdy okaże się potrzebna — na razie ceną jest
/// rzadki, kosmetyczny rozjazd w cudzym kalendarzu, a zyskiem interfejs, który odpowiada
/// od razu. Nieudane odbicie zostawia ślad w dzienniku, więc nie znika bez śladu.
/// </para>
/// </remarks>
public sealed class OdlozoneOdbicie(ITaskMirror odbicie, IActivityLog dziennik) : ITaskMirror
{
    private readonly SemaphoreSlim _brama = new(1, 1);

    public Task PushAsync(TaskItem task, CancellationToken ct = default)
    {
        Odloz("Kalendarz: wysłanie zadania", task, (t, c) => odbicie.PushAsync(t, c));

        return Task.CompletedTask;
    }

    public Task RemoveAsync(TaskItem task, CancellationToken ct = default)
    {
        Odloz("Kalendarz: skasowanie odbicia", task, (t, c) => odbicie.RemoveAsync(t, c));

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
    private void Odloz(string co, TaskItem task, Func<TaskItem, CancellationToken, Task> praca)
    {
        ArgumentNullException.ThrowIfNull(task);

        var tytul = task.Title;

        // Bez dziedziczenia kontekstu wywołania. Brama na bazę pozna po nim, że dany
        // przepływ jest już w środku, i wpuści go ponownie bez czekania — a praca
        // puszczona w tle nie jest tym samym przepływem, tylko drugą ręką. Dziś
        // odkładanie zaczyna się poza bramą, więc różnicy nie widać; jutro wystarczy
        // jedno wywołanie wyżej, żeby zaczęło się w środku, i wtedy ta linijka jest
        // jedyną rzeczą stojącą między tym a odczytem w poprzek cudzego zapisu.
        using var bezKontekstu = ExecutionContext.SuppressFlow();

        _ = Task.Run(async () =>
        {
            await _brama.WaitAsync();

            try
            {
                await praca(task, CancellationToken.None);
            }
            catch (Exception e)
            {
                // Odbicie robione po fakcie nie ma komu oddać wyjątku: ekran dawno
                // odpowiedział. Dziennik jest tu jedyną drogą, żeby nieudane wysłanie
                // nie było nieodróżnialne od wysłanego.
                try
                {
                    await dziennik.RecordAsync(
                        co, tytul, ActivityLevel.Problem, $"{e.GetType().Name}: {e.Message}");
                }
                catch
                {
                    // Dziennik jest narzędziem do patrzenia, a nie częścią działania.
                }
            }
            finally
            {
                _brama.Release();
            }
        });
    }
}
