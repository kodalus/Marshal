using Marshal.Domain.Tasks;

namespace Marshal.Application.Calendar;

/// <summary>
/// Odbicie zadania w kalendarzu zewnętrznym.
/// </summary>
/// <remarks>
/// <para>
/// Interfejs, a nie wywołanie wprost, z jednego powodu: edycja zadań musi umieć
/// odświeżyć odbicie, a odświeżanie odbicia czyta zadania. Wołane wprost dałoby
/// dwie usługi wskazujące na siebie i kontener nie miałby od czego zacząć. Tutaj
/// edycja zna tylko ten interfejs, a jego wykonanie sięga po kalendarz.
/// </para>
/// <para>
/// Drugi powód: w testach, które nie mają nic wspólnego z kalendarzem, wstawia się
/// tu wersję pustą i żadna ścieżka zapisu nie próbuje niczego wysyłać na zewnątrz.
/// </para>
/// </remarks>
public interface ITaskMirror
{
    /// <summary>Wyrównanie odbicia do stanu zadania. Nic nie robi, gdy zadanie nie jest udostępnione.</summary>
    Task PushAsync(TaskItem task, CancellationToken ct = default);

    /// <summary>Skasowanie odbicia — zadanie idzie do kosza albo przestaje być udostępniane.</summary>
    Task RemoveAsync(TaskItem task, CancellationToken ct = default);
}

/// <summary>
/// Brak odbicia. Dla testów i dla aplikacji bez podłączonego kalendarza.
/// </summary>
public sealed class NoTaskMirror : ITaskMirror
{
    public Task PushAsync(TaskItem task, CancellationToken ct = default) => Task.CompletedTask;

    public Task RemoveAsync(TaskItem task, CancellationToken ct = default) => Task.CompletedTask;
}
