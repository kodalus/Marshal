using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace Marshal.UI.Kontrolki;

/// <summary>Pole godziny: okrągła tarcza systemu tam, gdzie jest, wybierak Avalonii gdzie indziej.</summary>
public partial class WyborGodziny : UserControl
{
    public static readonly StyledProperty<TimeSpan?> WartoscProperty =
        AvaloniaProperty.Register<WyborGodziny, TimeSpan?>(
            nameof(Wartosc), defaultBindingMode: BindingMode.TwoWay);

    public TimeSpan? Wartosc
    {
        get => GetValue(WartoscProperty);
        set => SetValue(WartoscProperty, value);
    }

    private bool _wlasneWpisanie;

    private readonly TimePicker _wbudowany;

    private readonly Grid _systemowy;

    private readonly Button _otwarcie;

    public WyborGodziny()
    {
        InitializeComponent();

        _wbudowany = Znajdz<TimePicker>(this, "Wbudowany");
        _systemowy = Znajdz<Grid>(this, "Systemowy");
        _otwarcie = Znajdz<Button>(this, "Otwarcie");

        _wbudowany.IsVisible = !Pickery.Systemowe;
        _systemowy.IsVisible = Pickery.Systemowe;

        _wbudowany.PropertyChanged += (_, e) =>
        {
            if (e.Property == TimePicker.SelectedTimeProperty && !_wlasneWpisanie)
            {
                Wartosc = _wbudowany.SelectedTime;
            }
        };

        _otwarcie.Click += async (_, _) =>
        {
            if (Pickery.Godzina is not { } zapytaj)
            {
                return;
            }

            var wybrana = await zapytaj(
                Wartosc is { } teraz ? TimeOnly.FromTimeSpan(teraz) : null);

            Wartosc = wybrana?.ToTimeSpan();
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
            _wbudowany.SelectedTime = Wartosc;
        }
        finally
        {
            _wlasneWpisanie = false;
        }

        // Doba, nie dwunastka z dopiskiem: kalendarz obok liczy godziny tak samo,
        // a dwa zapisy tej samej godziny w jednym oknie to jeden za dużo.
        _otwarcie.Content = Wartosc is { } pora
            ? pora.ToString(@"hh\:mm", CultureInfo.InvariantCulture)
            : "wybierz";
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
