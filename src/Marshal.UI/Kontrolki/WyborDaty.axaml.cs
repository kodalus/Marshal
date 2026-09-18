using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace Marshal.UI.Kontrolki;

/// <summary>Pole daty: okienko systemu tam, gdzie jest, wybierak Avalonii tam, gdzie go nie ma.</summary>
public partial class WyborDaty : UserControl
{
    public static readonly StyledProperty<DateTimeOffset?> WartoscProperty =
        AvaloniaProperty.Register<WyborDaty, DateTimeOffset?>(
            nameof(Wartosc), defaultBindingMode: BindingMode.TwoWay);

    public DateTimeOffset? Wartosc
    {
        get => GetValue(WartoscProperty);
        set => SetValue(WartoscProperty, value);
    }

    /// <summary>Zapora przed odbiciem: nasz zapis do wybieraka wraca do nas jako zmiana.</summary>
    private bool _wlasneWpisanie;

    private readonly DatePicker _wbudowany;

    private readonly Grid _systemowy;

    private readonly Button _otwarcie;

    public WyborDaty()
    {
        InitializeComponent();

        _wbudowany = Znajdz<DatePicker>(this, "Wbudowany");
        _systemowy = Znajdz<Grid>(this, "Systemowy");
        _otwarcie = Znajdz<Button>(this, "Otwarcie");

        _wbudowany.IsVisible = !Pickery.Systemowe;
        _systemowy.IsVisible = Pickery.Systemowe;

        _wbudowany.PropertyChanged += (_, e) =>
        {
            if (e.Property == DatePicker.SelectedDateProperty && !_wlasneWpisanie)
            {
                Wartosc = _wbudowany.SelectedDate;
            }
        };

        _otwarcie.Click += async (_, _) =>
        {
            if (Pickery.Data is not { } zapytaj)
            {
                return;
            }

            var wybrana = await zapytaj(
                Wartosc is { } teraz ? DateOnly.FromDateTime(teraz.Date) : null);

            Wartosc = wybrana is { } dzien
                ? new DateTimeOffset(dzien.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                : null;
        };

        Znajdz<Button>(this, "Czyszczenie").Click += (_, _) => Wartosc = null;

        Odswiez();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WartoscProperty)
        {
            Odswiez();
        }
    }

    private void Odswiez()
    {
        // Zmiana właściwości potrafi przyjść, zanim konstruktor dojdzie do odczytania
        // elementów — a wtedy pola są jeszcze puste. To jest ten sam kształt wywrotki,
        // przez który ta kontrolka wywracała całą aplikację przy starcie, więc stoi tu
        // zapora, a nie założenie, że się nie zdarzy.
        if (_wbudowany is null || _otwarcie is null)
        {
            return;
        }

        _wlasneWpisanie = true;

        try
        {
            _wbudowany.SelectedDate = Wartosc;
        }
        finally
        {
            _wlasneWpisanie = false;
        }

        // „Wybierz", nie pusty przycisk: pusty wygląda na zepsuty, a kreska nie mówi,
        // co się stanie po dotknięciu.
        _otwarcie.Content = Wartosc is { } dzien ? $"{dzien:yyyy-MM-dd}" : "wybierz";
    }

    /// <summary>
    /// Elementy z XAML-a odczytywane, a nie brane z pól.
    /// </summary>
    /// <remarks>
    /// W tym projekcie <c>InitializeComponent</c> jest pisany ręcznie i woła wyłącznie
    /// wczytanie XAML-a. Pola dla nazwanych elementów tworzy wtedy generator, ale
    /// wypełnia je <b>swoja</b> wersja tej metody — której tu nie ma. Sięgnięcie po nie
    /// kończyło się pustym wskazaniem w konstruktorze i wywrotką całej aplikacji przy
    /// starcie, bo kontrolka powstaje w środku składania okna.
    ///
    /// Odczyt po nazwie nie zależy od generatora i jest tym, co reszta okna robi od
    /// początku.
    /// </remarks>
    private static T Znajdz<T>(UserControl gdzie, string nazwa)
        where T : Control =>
        gdzie.FindControl<T>(nazwa)
            ?? throw new InvalidOperationException($"Brak elementu „{nazwa}” w układzie.");

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
