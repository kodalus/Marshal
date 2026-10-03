using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Diagnostics;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Tasks;

namespace Marshal.Application.UseCases;

public sealed record RolloverReport(int Moved, int Spawned, int Merged = 0);

/// <summary>
/// Przejście dnia: zaległe zaplanowane i pominięte wystąpienia serii (spec 8.4, 8.7).
/// </summary>
/// <remarks>
/// <para>
/// Wołane przy starcie aplikacji, przy powrocie z tła i po każdym scaleniu
/// synchronizacji. Nie ma osobnego wyzwalacza o północy i nie będzie: aplikacja nie
/// chodzi w tle, a „przejście dnia" to nie zdarzenie w czasie, tylko zastana różnica
/// między datą zapisaną a dzisiejszą.
/// </para>
/// <para>
/// Wołanie jest **powtarzalne bez skutków ubocznych** — to nie jest wygoda, tylko
/// warunek: dwa urządzenia robią to samo, niezależnie i bez umawiania się, które ma.
/// </para>
/// </remarks>
public sealed class DayRolloverService(
    ITaskRepository tasks,
    IUnitOfWork unitOfWork,
    IClock clock,
    IHlcSource hlc,
    IActivityLog? journal = null)
{
    /// <summary>
    /// Ile wystąpień wolno dorobić w jednym przebiegu.
    /// </summary>
    /// <remarks>
    /// Dotyczy wyłącznie <see cref="OnMissed.Accumulate"/> — jedynej ścieżki w modelu,
    /// która potrafi wyprodukować stertę. Rok bez otwarcia aplikacji przy powtarzaniu
    /// codziennym dałby trzysta pozycji naraz, czyli dokładnie to, przed czym ma chronić
    /// zasada 1.2. Reszta zaległości dojdzie przy kolejnym uruchomieniu; nic nie ginie,
    /// tylko schodzi partiami.
    /// </remarks>
    private const int MaxSpawnsPerRun = 120;

    public async Task<RolloverReport> RunAsync(CancellationToken ct = default)
    {
        var now = clock.Now;
        var today = clock.Today;

        // Najpierw bliźniaki, potem zaległości: dwie kopie tej samej serii przeszłyby
        // przejście dnia każda osobno i wyprodukowały dwa następniki zamiast jednego.
        var merged = await MergeTwinsAsync(ct);

        var queue = new Queue<TaskItem>(await tasks.OverdueByDoDateAsync(today, ct));
        var moved = 0;
        var fresh = 0;

        while (queue.Count > 0)
        {
            var task = queue.Dequeue();
            var before = (task.DoDate, task.RollCount, task.CarriedSince);

            if (RecurrenceRunner.Rollover(task, now, hlc.Next) is { } next)
            {
                tasks.Add(next);
                fresh++;

                // Świeże wystąpienie samo bywa zaległe: przy Accumulate rytm nadrabia
                // po jednym dniu, więc tydzień nieobecności to tydzień pozycji. Bez
                // domknięcia tu zaległość schodziłaby po jednym dniu na uruchomienie.
                if (next.DoDate < today && fresh < MaxSpawnsPerRun)
                {
                    queue.Enqueue(next);
                }
            }

            if (before != (task.DoDate, task.RollCount, task.CarriedSince))
            {
                moved++;
            }
        }

        if (moved > 0 || fresh > 0)
        {
            await unitOfWork.SaveChangesAsync(ct);
        }

        return new RolloverReport(moved, fresh, merged);
    }

    /// <summary>
    /// Zlanie w jedno kopii tej samej serii, które powstały na dwóch urządzeniach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Do dziś następnik dostawał identyfikator losowy, więc telefon i pulpit, które ten
    /// sam dzień przekroczyły osobno, tworzyły dwa różne zadania — a scalanie rozpoznaje
    /// rzeczy po identyfikatorze i przyjmowało oba. Seria podwajała się na siatce
    /// i podwajała każdą swoją zapowiedź. Od teraz tożsamość następnika jest wyliczana
    /// (<see cref="OccurrenceId"/>) i nowe bliźniaki nie powstają; ten krok sprząta te,
    /// które zdążyły powstać wcześniej.
    /// </para>
    /// <para>
    /// <b>Bliźniak to nie „podobne zadanie".</b> Muszą zgadzać się nazwa, miejsce
    /// i <b>kształt</b> rytmu — rodzaj, odstęp, dni, zaczepienie, odpowiedź na minięcie.
    /// Dwa zadania o tej samej nazwie i tym samym rytmie, w tym samym obszarze
    /// i projekcie, nie są dwiema rzeczami: w modelu seria ma naraz **jedno** żywe
    /// wystąpienie. Dwa podobne zadania bez rytmu — jak najbardziej, i dlatego samo
    /// podobieństwo nie wystarcza.
    /// </para>
    /// <para>
    /// <b>Po kształcie, nie po całym zapisie reguły, i bez pytania o dzień.</b> Pierwsze
    /// podejście pytało o jedno i drugie — i nie sklejało niczego. Kopie rozchodzą się
    /// w obu tych wymiarach: przejście dnia obcina minione odwołania i pomniejsza licznik
    /// pozostałych wystąpień, a urządzenie otwarte później przeskakuje serię dalej, bo
    /// nadrabia więcej dni. Pytanie „czy zapisy są identyczne" odpowiadało więc „to różne
    /// rytmy" na dwie kopie tego samego.
    /// </para>
    /// <para>
    /// <b>Zostaje wystąpienie najbliższe, a remis rozstrzyga identyfikator.</b> Żywym
    /// wystąpieniem serii jest to następne w kolejce; zostawienie tego, które pobiegło
    /// do przodu, gubiłoby dzień po drodze. Rozstrzygnięcie remisu musi wypaść tak samo
    /// na obu urządzeniach — inaczej każde zostawiłoby inną kopię i po scaleniu nie
    /// zostałaby żadna. Data utworzenia do tego się nie nadaje: pochodzi z dwóch różnych
    /// zegarów.
    /// </para>
    /// <para>
    /// Do kosza, nie z bazy: kopia dostaje nagrobek i zostaje w archiwum. Gdyby ten krok
    /// kiedykolwiek pomylił się co do bliźniaka, pomyłka ma być do obejrzenia i do
    /// cofnięcia, a nie do odtworzenia z kopii zapasowej.
    /// </para>
    /// </remarks>
    private async Task<int> MergeTwinsAsync(CancellationToken ct)
    {
        // Po **kształcie** rytmu, nie po całym jego zapisie. Pierwsze podejście
        // porównywało reguły znak w znak i dlatego nie sklejało niczego: dwie kopie tej
        // samej serii niemal nigdy nie mają identycznego zapisu, bo każda zaszła kawałek
        // dalej po swojemu — przejście dnia obcina minione odwołania i pomniejsza licznik
        // pozostałych wystąpień. Kształt (rodzaj, odstęp, dni, zaczepienie, odpowiedź na
        // minięcie) stoi przez całe życie serii i odpowiada na pytanie „który to rytm".
        var twins = (await tasks.RecurringAsync(ct))
            .Where(t => t.Recurrence is not null)
            .GroupBy(t => (t.Title, t.AreaId, t.ProjectId, t.Recurrence!.Shape))
            .Where(g => g.Count() > 1)
            .ToList();

        if (twins.Count == 0)
        {
            return 0;
        }

        var gone = 0;

        foreach (var group in twins)
        {
            // Zostaje wystąpienie **najbliższe**, a nie najstarszy wpis. Kopie rozchodzą
            // się nie tylko w zapisie reguły, ale i w czasie: urządzenie otwarte później
            // przeskoczyło serię dalej, bo nadrabiało więcej dni. W modelu żyje naraz
            // jedno wystąpienie serii i jest nim to następne w kolejce — zostawienie
            // tego, które pobiegło do przodu, gubiłoby dzień po drodze.
            //
            // Identyfikator rozstrzyga remis i musi go rozstrzygać, bo wybór ma wypaść
            // tak samo na obu urządzeniach: inaczej każde zostawiłoby inną kopię
            // i po scaleniu nie zostałaby żadna.
            foreach (var extra in group
                .OrderBy(t => t.DoDate ?? DateOnly.MaxValue)
                .ThenBy(t => t.Id)
                .Skip(1))
            {
                extra.Trash(hlc.Next());
                gone++;
            }
        }

        await unitOfWork.SaveChangesAsync(ct);

        // Do dziennika, bo to jedyna czynność w przejściu dnia, która **zabiera**
        // zadania — i jedyna, o której trzeba móc powiedzieć „zadziałała" albo „nie
        // zadziałała" bez zaglądania do bazy. Brak tego wpisu kosztował jedną rundę:
        // nie dało się odróżnić „nie znalazło bliźniaków" od „w ogóle nie doszło".
        if (journal is not null)
        {
            var what = string.Join(", ", twins
                .Select(g => $"{g.Key.Title} ×{g.Count()}")
                .Take(5));

            await journal.RecordAsync(
                "Przejście dnia: sklejenie kopii serii",
                $"{gone} do kosza",
                ActivityLevel.Ok,
                what,
                ct);
        }

        return gone;
    }
}
