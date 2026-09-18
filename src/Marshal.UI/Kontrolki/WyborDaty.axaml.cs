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

    public WyborDaty()
    {
        InitializeComponent();

        Wbudowany.IsVisible = !Pickery.Systemowe;
        Systemowy.IsVisible = Pickery.Systemowe;

        Wbudowany.PropertyChanged += (_, e) =>
        {
            if (e.Property == DatePicker.SelectedDateProperty && !_wlasneWpisanie)
            {
                Wartosc = Wbudowany.SelectedDate;
            }
        };

        Otwarcie.Click += async (_, _) =>
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

        Czyszczenie.Click += (_, _) => Wartosc = null;

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
        _wlasneWpisanie = true;

        try
        {
            Wbudowany.SelectedDate = Wartosc;
        }
        finally
        {
            _wlasneWpisanie = false;
        }

        // „Wybierz", nie pusty przycisk: pusty wygląda na zepsuty, a kreska nie mówi,
        // co się stanie po dotknięciu.
        Otwarcie.Content = Wartosc is { } dzien ? $"{dzien:yyyy-MM-dd}" : "wybierz";
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
