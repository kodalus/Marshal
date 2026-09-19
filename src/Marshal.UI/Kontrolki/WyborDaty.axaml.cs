using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace Marshal.UI.Kontrolki;

/// <summary>
/// Pole daty: kalendarz miesiąca, a na Androidzie ten systemowy.
/// </summary>
/// <remarks>
/// <para>
/// Wbudowany wybierak Avalonii to trzy kolumny do przewijania — rok, miesiąc, dzień.
/// Na pytanie „która środa" nie odpowiada wcale: trzeba ją sobie policzyć, zanim się
/// ją wybierze. Kalendarz miesiąca odpowiada na nie samym wyglądem i to on jest tu
/// w obu miejscach — na pulpicie własny, na Androidzie systemowy, bo tamten palec zna.
/// </para>
/// <para>
/// O to, który pokazać, pyta się <b>przy pokazywaniu</b>, nie raz przy powstawaniu.
/// Okno składa się wcześniej niż podpięcie okienek systemu i pole, które zapytało za
/// wcześnie, zostawało przy wybieraku wbudowanym na całe uruchomienie — bez śladu,
/// bo oba wyglądają jak pole daty.
/// </para>
/// </remarks>
public partial class WyborDaty : UserControl
{
    public static readonly StyledProperty<DateTimeOffset?> ValueProperty =
        AvaloniaProperty.Register<WyborDaty, DateTimeOffset?>(
            nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public DateTimeOffset? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private readonly Button _otwarcie;

    private readonly Calendar _miesiac;

    private readonly Flyout _rozwiniecie;

    /// <summary>Zapora przed odbiciem: nasz zapis do kalendarza wraca do nas jako zmiana.</summary>
    private bool _wlasneWpisanie;

    public WyborDaty()
    {
        InitializeComponent();

        _otwarcie = Znajdz<Button>(this, "Otwarcie");

        _miesiac = new Calendar { SelectionMode = CalendarSelectionMode.SingleDate };
        _rozwiniecie = new Flyout { Content = _miesiac };

        _miesiac.SelectedDatesChanged += (_, _) =>
        {
            if (_wlasneWpisanie)
            {
                return;
            }

            Value = _miesiac.SelectedDate is { } day
                ? new DateTimeOffset(day.Date, TimeSpan.Zero)
                : null;

            // Dzień wybrany, więc nie ma na co dłużej patrzeć. Rozwinięcie zostawione
            // otwarte zasłaniałoby pole, które właśnie wypełniło.
            _rozwiniecie.Hide();
        };

        _otwarcie.Click += async (_, _) =>
        {
            if (Pickery.Data is not { } zapytaj)
            {
                return;
            }

            var wybrana = await zapytaj(
                Value is { } now ? DateOnly.FromDateTime(now.Date) : null);

            Value = wybrana is { } day
                ? new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                : null;
        };

        Znajdz<Button>(this, "Czyszczenie").Click += (_, _) => Value = null;

        // Rozstrzygnięcie dopiero tutaj — do tej chwili okienka systemu mogły się
        // jeszcze nie podpiąć.
        AttachedToVisualTree += (_, _) => Resolve();

        Odswiez();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ValueProperty)
        {
            Odswiez();
        }
    }

    /// <summary>Kalendarz własny albo systemowy — jedno albo drugie, nigdy oba.</summary>
    private void Resolve() =>
        _otwarcie.Flyout = Pickery.SystemSink ? null : _rozwiniecie;

    private void Odswiez()
    {
        // Zmiana właściwości potrafi przyjść, zanim konstruktor dojdzie do odczytania
        // elementów — a wtedy pola są jeszcze puste. To jest ten sam kształt wywrotki,
        // przez który ta kontrolka wywracała całą aplikację przy starcie, więc stoi tu
        // zapora, a nie założenie, że się nie zdarzy.
        if (_otwarcie is null || _miesiac is null)
        {
            return;
        }

        _wlasneWpisanie = true;

        try
        {
            _miesiac.SelectedDate = Value?.Date;

            if (Value is { } day)
            {
                // Otwieraj na miesiącu, który jest wybrany, a nie na bieżącym.
                _miesiac.DisplayDate = day.Date;
            }
        }
        finally
        {
            _wlasneWpisanie = false;
        }

        // „Wybierz", nie pusty przycisk: pusty wygląda na zepsuty, a kreska nie mówi,
        // co się stanie po dotknięciu.
        _otwarcie.Content = Value is { } data ? $"{data:yyyy-MM-dd}" : "wybierz";
    }

    /// <summary>
    /// Elementy z XAML-a odczytywane, a nie brane z pól.
    /// </summary>
    /// <remarks>
    /// W tym projekcie <c>InitializeComponent</c> jest pisany ręcznie i woła wyłącznie
    /// wczytanie XAML-a. Pola dla nazwanych elementów tworzy wtedy generator, ale
    /// wypełnia je <b>jego</b> wersja tej metody — której tu nie ma. Sięgnięcie po nie
    /// kończyło się pustym wskazaniem w konstruktorze i wywrotką całej aplikacji przy
    /// starcie, bo kontrolka powstaje w środku składania okna.
    /// </remarks>
    private static T Znajdz<T>(UserControl where, string name)
        where T : Control =>
        where.FindControl<T>(name)
            ?? throw new InvalidOperationException($"Brak elementu „{name}” w układzie.");

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
