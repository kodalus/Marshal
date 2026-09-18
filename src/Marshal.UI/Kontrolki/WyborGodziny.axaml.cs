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

    public WyborGodziny()
    {
        InitializeComponent();

        Wbudowany.IsVisible = !Pickery.Systemowe;
        Systemowy.IsVisible = Pickery.Systemowe;

        Wbudowany.PropertyChanged += (_, e) =>
        {
            if (e.Property == TimePicker.SelectedTimeProperty && !_wlasneWpisanie)
            {
                Wartosc = Wbudowany.SelectedTime;
            }
        };

        Otwarcie.Click += async (_, _) =>
        {
            if (Pickery.Godzina is not { } zapytaj)
            {
                return;
            }

            var wybrana = await zapytaj(
                Wartosc is { } teraz ? TimeOnly.FromTimeSpan(teraz) : null);

            Wartosc = wybrana?.ToTimeSpan();
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
            Wbudowany.SelectedTime = Wartosc;
        }
        finally
        {
            _wlasneWpisanie = false;
        }

        // Doba, nie dwunastka z dopiskiem: kalendarz obok liczy godziny tak samo,
        // a dwa zapisy tej samej godziny w jednym oknie to jeden za dużo.
        Otwarcie.Content = Wartosc is { } pora
            ? pora.ToString(@"hh\:mm", CultureInfo.InvariantCulture)
            : "wybierz";
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
