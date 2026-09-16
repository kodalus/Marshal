using Marshal.Application.Abstractions;

namespace Marshal.Infrastructure.Notifications;

/// <summary>
/// Zbiera przypomnienia do odebrania przez interfejs aplikacji.
/// </summary>
/// <remarks>
/// <para>
/// Domyślna implementacja, dopóki nie ma powiadomień systemowych. Nie jest atrapą:
/// przypomnienie pokazane w oknie otwartej aplikacji jest prawdziwym przypomnieniem
/// i pokrywa przypadek, w którym siedzisz przy komputerze.
/// </para>
/// <para>
/// Czego **nie** daje: odezwania się przy zamkniętej aplikacji. To wymaga powiadomień
/// systemowych — na Androidzie kanału powiadomień i uprawnienia, na Windowsie
/// zarejestrowanego skrótu w menu Start. Jedno i drugie da się sprawdzić wyłącznie
/// na sprzęcie.
/// </para>
/// </remarks>
public sealed class InAppNotifier : INotifier
{
    private readonly Lock _gate = new();
    private readonly List<Notification> _oczekujace = [];

    public event EventHandler<Notification>? Shown;

    public Task ShowAsync(Notification notification, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _oczekujace.Add(notification);
        }

        Shown?.Invoke(this, notification);
        return Task.CompletedTask;
    }

    /// <summary>Odbiera i czyści to, co się uzbierało.</summary>
    public IReadOnlyList<Notification> Drain()
    {
        lock (_gate)
        {
            var kopia = _oczekujace.ToList();
            _oczekujace.Clear();
            return kopia;
        }
    }
}
