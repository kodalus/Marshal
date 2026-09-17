using Marshal.Application.Abstractions;

namespace Marshal.Infrastructure.Notifications;

/// <summary>
/// Zbiera przypomnienia do odebrania przez interfejs aplikacji.
/// </summary>
/// <remarks>
/// <para>
/// Pasek w oknie **i** wyjście na dymek systemowy, gdy platforma go daje. Jedno nie
/// zastępuje drugiego: dymek odzywa się, gdy patrzysz gdzie indziej, a pasek zostaje
/// na ekranie, gdy dymek się rozpłynął albo Windows go zatrzymał.
/// </para>
/// <para>
/// Na pulpicie dymki podpina projekt platformy przez <see cref="Systemowe"/>.
/// Na Androidzie jeszcze ich nie ma — tam trzeba kanału powiadomień i uprawnienia,
/// a jedno i drugie da się sprawdzić wyłącznie na sprzęcie.
/// </para>
/// </remarks>
public sealed class InAppNotifier : INotifier
{
    private readonly Lock _gate = new();
    private readonly List<Notification> _oczekujace = [];

    /// <summary>
    /// Wyjście na powiadomienia systemowe, podstawiane przez warstwę platformy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pole statyczne, i to jest świadome. Powiadomienie systemowe umie pokazać tylko
    /// projekt platformy — pakiet, który to robi, jest desktopowy i nie może wejść do
    /// warstwy współdzielonej z Androidem. Kontener powstaje wewnątrz aplikacji okna,
    /// więc projekt platformy nie ma gdzie wstawić swojej wersji; haczyk ustawiany raz
    /// przy starcie jest tańszy niż przeprowadzanie budowy kontenera przez wszystkie
    /// trzy projekty po to, żeby podać jedną funkcję.
    /// </para>
    /// <para>
    /// Puste znaczy „ta platforma nie umie" i nie jest błędem: pasek w oknie działa
    /// dalej i jest prawdziwym przypomnieniem, gdy siedzisz przy komputerze.
    /// </para>
    /// </remarks>
    public static Func<Notification, CancellationToken, Task>? Systemowe { get; set; }

    /// <summary>
    /// Co wyszło z podpinania powiadomień systemowych. Do dziennika przy starcie.
    /// </summary>
    /// <remarks>
    /// Bez tego „nie ma powiadomienia" ma trzy przyczyny wyglądające identycznie:
    /// pakiet się nie podpiął, podpiął się i Windows odmówił, albo w ogóle nie było
    /// czego pokazać. Pierwsza jest do naprawienia w kodzie, druga po stronie systemu,
    /// trzecia nie jest usterką — a z samego braku dymka nie da się ich rozróżnić.
    /// </remarks>
    public static string StanSystemowych { get; set; } = "nie podpięto";

    public event EventHandler<Notification>? Shown;

    public async Task ShowAsync(Notification notification, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _oczekujace.Add(notification);
        }

        Shown?.Invoke(this, notification);

        if (Systemowe is not { } systemowe)
        {
            return;
        }

        // Awaria powiadomienia systemowego nie ma zabierać ze sobą tego w oknie.
        // Toast na Windowsie potrafi nie wyjść z powodów, na które nie mamy wpływu —
        // brak skrótu w menu Start, wyłączone powiadomienia, tryb skupienia.
        try
        {
            await systemowe(notification, ct);
        }
        catch (Exception e)
        {
            // Zostaje pasek w oknie.
            StanSystemowych = $"pokazanie nie udało się: {e.GetType().Name}: {e.Message}";
        }
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
