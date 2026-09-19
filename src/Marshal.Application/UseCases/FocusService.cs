using Marshal.Application.Abstractions;
using Marshal.Application.Calendar;
using Marshal.Application.Repositories;
using Marshal.Domain.Tasks;

namespace Marshal.Application.UseCases;

/// <summary>
/// Wynik próby wybrania zadania na dziś. Odmowa niesie ze sobą obecną piątkę,
/// bo pytanie „które schodzi" bez pokazania czego dotyczy nie jest pytaniem.
/// </summary>
public sealed record FocusResult(bool Accepted, IReadOnlyList<TaskItem> Current);

/// <summary>
/// Wybór pięciu zadań na dzień (spec 8.6, N14).
/// </summary>
/// <remarks>
/// <para>
/// Limit działa tu bez oporu, a na wadze działałby źle, i to jest cała różnica: dotyczy
/// **jednego dnia**, nie odbiera niczemu ważności na zawsze, zeruje się co dobę — więc
/// nie ma czego gromadzić i sterta nie powstaje — a wymuszony wybór wypada przy porannym
/// planowaniu, nie przy wrzucaniu do skrzynki.
/// </para>
/// <para>
/// Na koniec dnia niewykonany wybór **po prostu wygasa**. Bez czerwieni, bez ekranu
/// podsumowania, bez „wykonano 2 z 5". Licznik pominięć pracuje po cichu na potrzeby N13.
/// </para>
/// </remarks>
public sealed class FocusService(
    ITaskRepository tasks,
    IUnitOfWork unitOfWork,
    IClock clock,
    IHlcSource hlc,
    ITaskMirror? mirror = null)
{
    /// <summary>
    /// Odbicie wyboru w kalendarzu zewnętrznym.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zadanie wybrane na dziś dostaje dzień, więc od tej chwili należy do kalendarza
    /// tak samo jak zadanie umówione — tyle że bez godziny, czyli jako całodniowe.
    /// Zdjęcie z wyboru i wygaśnięcie zabierają mu ten dzień, więc wydarzenie znika.
    /// </para>
    /// <para>
    /// Awaria wysyłki **nie przewraca** wyboru. Wzięcie zadania na dziś jest gestem
    /// wykonywanym przy porannym planowaniu, często w biegu; odmowa dlatego, że Google
    /// akurat nie odpowiada, znaczyłaby, że planowania nie da się zrobić bez sieci.
    /// Ślad po nieudanej wysyłce zostaje w dzienniku — zapisuje go samo odbicie.
    /// </para>
    /// </remarks>
    private async Task MirrorAsync(TaskItem task, CancellationToken ct)
    {
        if (mirror is null)
        {
            return;
        }

        try
        {
            await mirror.PushAsync(task, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Zapisane już w dzienniku przez odbicie. Wybór zostaje.
        }
    }

    /// <summary>Ile zadań wolno wybrać na jeden dzień (N14).</summary>
    public const int Slots = 5;

    public Task<IReadOnlyList<TaskItem>> TodayAsync(CancellationToken ct = default) =>
        tasks.ByFocusDateAsync(Today(), ct);

    /// <summary>
    /// Próbuje wybrać zadanie na dziś. Przy pełnej piątce **nie zmienia nic** i oddaje
    /// obecny wybór, żeby okno mogło zapytać, które schodzi.
    /// </summary>
    public async Task<FocusResult> TryFocusAsync(Guid taskId, CancellationToken ct = default)
    {
        var today = Today();
        var selected = await tasks.ByFocusDateAsync(today, ct);

        if (selected.Any(t => t.Id == taskId))
        {
            return new FocusResult(true, selected);
        }

        if (selected.Count >= Slots)
        {
            return new FocusResult(false, selected);
        }

        if (await tasks.FindAsync(taskId, ct) is not { } task)
        {
            return new FocusResult(false, selected);
        }

        // Wzięcie z „kiedyś-może” aktywuje zadanie. Sama data wyboru zostawiała je
        // w stanie, którego „Teraz” nie pokazuje — wybrane na dziś i niewidoczne tam,
        // gdzie pyta się „co teraz”.
        task.Activate(hlc.Next());
        task.Focus(today, hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);
        await MirrorAsync(task, ct);

        return new FocusResult(true, await tasks.ByFocusDateAsync(today, ct));
    }

    /// <summary>Zdjęcie z wyboru. Bez żadnej innej zmiany i bez podbijania licznika.</summary>
    public async Task UnfocusAsync(Guid taskId, CancellationToken ct = default)
    {
        if (await tasks.FindAsync(taskId, ct) is not { } task)
        {
            return;
        }

        task.Unfocus(hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);
        await MirrorAsync(task, ct);
    }

    /// <summary>
    /// Wygaszenie wyborów z dni minionych. Wołane razem z przejściem dnia.
    /// </summary>
    /// <remarks>
    /// Zadanie wraca do puli, a licznik pominięć rośnie o jeden. Nic poza tym się nie
    /// dzieje — żadnego oznaczenia, żadnej zmiany stanu, żadnej plakietki.
    /// </remarks>
    public async Task<int> ExpireAsync(CancellationToken ct = default)
    {
        var expired = await tasks.ExpiredFocusAsync(Today(), ct);

        foreach (var task in expired)
        {
            task.MissFocus(hlc.Next());
        }

        if (expired.Count > 0)
        {
            await unitOfWork.SaveChangesAsync(ct);

            foreach (var task in expired)
            {
                await MirrorAsync(task, ct);
            }
        }

        return expired.Count;
    }

    private DateOnly Today() => clock.Today;
}
