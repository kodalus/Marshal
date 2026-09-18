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
/// Dymki podpina projekt platformy przez <see cref="Systemowe"/> — na pulpicie przy
/// starcie okna, na Androidzie przy starcie okna albo odbiornika budzika.
/// </para>
/// <para>
/// <b>Zaległość na wypadek, gdy haczyka jeszcze nie ma.</b> Składanie zależności
/// samo nadrabia zaległe przypomnienia (<c>CatchUpAsync</c>), a wołają je także
/// odbiornik budzika i widget — czyli drogi, na których nie ma okna i nie ma kto
/// podpiąć dymków wcześniej. Pokazane w takiej chwili przypomnienie trafiało dotąd
/// wyłącznie na listę do odebrania przez okno, a w procesie bez okna nie było komu
/// jej odebrać. W bazie zostawało odnotowane jako pokazane, więc nie wracało już
/// nigdy: przypomnienie znikało bez śladu i bez objawu.
/// </para>
/// <para>
/// Dlatego przypomnienia pokazane bez podpiętego haczyka czekają, a podpięcie haczyka
/// je wypuszcza. Kolejność podpięcia i składania przestaje mieć znaczenie — a właśnie
/// na kolejności ta usterka stała.
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
    public static Func<Notification, CancellationToken, Task>? Systemowe
    {
        get => _systemowe;

        set
        {
            List<Notification> zaleglosc;

            lock (SystemGate)
            {
                _systemowe = value;

                if (value is null)
                {
                    // Odpięcie znaczy, że nie ma dokąd — a zaległość bez adresata to już
                    // nie zabezpieczenie, tylko rosnąca lista. W aplikacji haczyk podpina
                    // się raz na proces i nie odpina, więc dotyczy to wyłącznie sprzątania.
                    CzekajaceNaSystem.Clear();
                    return;
                }

                if (CzekajaceNaSystem.Count == 0)
                {
                    return;
                }

                zaleglosc = [.. CzekajaceNaSystem];
                CzekajaceNaSystem.Clear();
            }

            // Poza blokadą: pokazanie dymka woła system, a trzymanie przy tym zamka
            // blokowałoby każde kolejne przypomnienie.
            foreach (var przypomnienie in zaleglosc)
            {
                _ = Wypusc(value, przypomnienie);
            }
        }
    }

    private static Func<Notification, CancellationToken, Task>? _systemowe;

    private static readonly Lock SystemGate = new();

    /// <summary>
    /// Przypomnienia pokazane, zanim platforma podpięła dymki.
    /// </summary>
    /// <remarks>
    /// Ograniczone, bo zaległość rośnie tylko wtedy, gdy coś jest nie tak — a lista bez
    /// końca zamieniłaby jedną usterkę w drugą, gorszą. Pięćdziesiąt to i tak więcej,
    /// niż da się przeczytać naraz.
    /// </remarks>
    private static readonly List<Notification> CzekajaceNaSystem = [];

    private const int Zapas = 50;

    private static async Task Wypusc(
        Func<Notification, CancellationToken, Task> systemowe, Notification przypomnienie)
    {
        try
        {
            await systemowe(przypomnienie, CancellationToken.None);
        }
        catch (Exception e)
        {
            StanSystemowych = $"zaległe nie wyszło: {e.GetType().Name}: {e.Message}";
        }
    }

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

        // Sprawdzenie i odłożenie **pod jednym zamkiem**, bo inaczej zostaje szczelina:
        // haczyk podpięty między odczytem a odłożeniem wypuściłby zaległość bez tego
        // przypomnienia, a ono dołączyłoby do niej już po wszystkim i zostało tam na zawsze.
        Func<Notification, CancellationToken, Task>? systemowe;

        lock (SystemGate)
        {
            systemowe = _systemowe;

            if (systemowe is null)
            {
                if (CzekajaceNaSystem.Count < Zapas)
                {
                    CzekajaceNaSystem.Add(notification);
                }

                return;
            }
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
