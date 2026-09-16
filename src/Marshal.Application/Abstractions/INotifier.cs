namespace Marshal.Application.Abstractions;

public sealed record Notification(Guid TaskId, string Title, string? Body);

/// <summary>
/// Pokazanie przypomnienia użytkownikowi.
/// </summary>
/// <remarks>
/// Wydzielone interfejsem, bo to jedyny kawałek przypomnień, który wygląda inaczej na
/// każdej platformie — i jedyny, którego nie da się sprawdzić testem. Wybieranie, co
/// i kiedy pokazać, leży po tej stronie i jest pokryte testami; za interfejsem zostaje
/// samo wołanie systemu.
/// </remarks>
public interface INotifier
{
    Task ShowAsync(Notification notification, CancellationToken ct = default);
}
