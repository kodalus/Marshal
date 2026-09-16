using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;

namespace Marshal.Application.UseCases;

/// <summary>
/// Odpalanie przypomnień, których to urządzenie jeszcze nie pokazało.
/// </summary>
/// <remarks>
/// <para>
/// Przypomnienie z przeszłości też się odzywa. Aplikacja nie chodzi w tle, więc chwila
/// przypomnienia prawie nigdy nie zastaje jej otwartej — odezwanie się wyłącznie „co do
/// minuty" znaczyłoby, że przypomnienia nie działają w ogóle.
/// </para>
/// <para>
/// Zapomniane po odpaleniu, ale osobno na każdym urządzeniu (zob.
/// <see cref="Domain.Sync.ReminderShown"/>) — i z zapamiętaną chwilą, na którą było
/// ustawione, żeby przesunięcie przypomnienia odblokowało je ponownie.
/// </para>
/// </remarks>
public sealed class ReminderService(
    ITaskRepository tasks,
    IReminderLog log,
    INotifier notifier,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var now = clock.Now;
        var wymagalne = await tasks.DueRemindersAsync(now, ct);
        var pokazane = 0;

        foreach (var zadanie in wymagalne)
        {
            if (zadanie.ReminderAt is not { } chwila || await log.WasShownAsync(zadanie.Id, chwila, ct))
            {
                continue;
            }

            await notifier.ShowAsync(new Notification(zadanie.Id, zadanie.Title, zadanie.Note), ct);
            log.Record(zadanie.Id, chwila, now);
            pokazane++;
        }

        if (pokazane > 0)
        {
            await unitOfWork.SaveChangesAsync(ct);
        }

        return pokazane;
    }
}
