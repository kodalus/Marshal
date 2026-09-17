using Marshal.Application.Abstractions;
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
    IHlcSource hlc)
{
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
        var dzis = Today();
        var wybrane = await tasks.ByFocusDateAsync(dzis, ct);

        if (wybrane.Any(t => t.Id == taskId))
        {
            return new FocusResult(true, wybrane);
        }

        if (wybrane.Count >= Slots)
        {
            return new FocusResult(false, wybrane);
        }

        if (await tasks.FindAsync(taskId, ct) is not { } zadanie)
        {
            return new FocusResult(false, wybrane);
        }

        // Wzięcie z „kiedyś-może” aktywuje zadanie. Sama data wyboru zostawiała je
        // w stanie, którego „Teraz” nie pokazuje — wybrane na dziś i niewidoczne tam,
        // gdzie pyta się „co teraz”.
        zadanie.Activate(hlc.Next());
        zadanie.Focus(dzis, hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);

        return new FocusResult(true, await tasks.ByFocusDateAsync(dzis, ct));
    }

    /// <summary>Zdjęcie z wyboru. Bez żadnej innej zmiany i bez podbijania licznika.</summary>
    public async Task UnfocusAsync(Guid taskId, CancellationToken ct = default)
    {
        if (await tasks.FindAsync(taskId, ct) is not { } zadanie)
        {
            return;
        }

        zadanie.Unfocus(hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);
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
        var wygasle = await tasks.ExpiredFocusAsync(Today(), ct);

        foreach (var zadanie in wygasle)
        {
            zadanie.MissFocus(hlc.Next());
        }

        if (wygasle.Count > 0)
        {
            await unitOfWork.SaveChangesAsync(ct);
        }

        return wygasle.Count;
    }

    private DateOnly Today() => clock.Today;
}
