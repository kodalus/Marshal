using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace Marshal.UI.Controls;

/// <summary>Pole godziny: okrągła tarcza systemu tam, gdzie jest, wybierak Avalonii gdzie indziej.</summary>
public partial class TimeField : UserControl
{
    public static readonly StyledProperty<TimeSpan?> ValueProperty =
        AvaloniaProperty.Register<TimeField, TimeSpan?>(
            nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public TimeSpan? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private bool _ownTyping;

    private readonly TimePicker _builtIn;

    private readonly Grid _system;

    private readonly Button _opener;

    public TimeField()
    {
        InitializeComponent();

        _builtIn = Find<TimePicker>(this, "Wbudowany");
        _system = Find<Grid>(this, "Systemowy");
        _opener = Find<Button>(this, "Otwarcie");

        // Rozstrzygnięcie przy pokazywaniu, nie raz przy powstawaniu: okno składa się
        // wcześniej niż podpięcie okienek systemu, a pole, które zapytało za wcześnie,
        // zostawało przy wybieraku wbudowanym na całe uruchomienie — bez śladu, bo oba
        // wyglądają jak pole godziny.
        AttachedToVisualTree += (_, _) => Resolve();

        _builtIn.PropertyChanged += (_, e) =>
        {
            if (e.Property == TimePicker.SelectedTimeProperty && !_ownTyping)
            {
                Value = _builtIn.SelectedTime;
            }
        };

        _opener.Click += async (_, _) =>
        {
            if (Pickers.Hour is not { } ask)
            {
                return;
            }

            var chosen = await ask(
                Value is { } now ? TimeOnly.FromTimeSpan(now) : null);

            Value = chosen?.ToTimeSpan();
        };

        Find<Button>(this, "Czyszczenie").Click += (_, _) => Value = null;

        Resolve();
        Refresh();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ValueProperty)
        {
            Refresh();
        }
    }

    /// <summary>Tarcza systemu albo wybierak wbudowany — jedno albo drugie, nigdy oba.</summary>
    private void Resolve()
    {
        _builtIn.IsVisible = !Pickers.SystemSink;
        _system.IsVisible = Pickers.SystemSink;
    }

    private void Refresh()
    {
        // Zmiana właściwości potrafi przyjść, zanim konstruktor dojdzie do odczytania
        // elementów — a wtedy pola są jeszcze puste. To jest ten sam kształt wywrotki,
        // przez który ta kontrolka wywracała całą aplikację przy starcie, więc stoi tu
        // zapora, a nie założenie, że się nie zdarzy.
        if (_builtIn is null || _opener is null)
        {
            return;
        }

        _ownTyping = true;

        try
        {
            _builtIn.SelectedTime = Value;
        }
        finally
        {
            _ownTyping = false;
        }

        // Doba, nie dwunastka z dopiskiem: kalendarz obok liczy godziny tak samo,
        // a dwa zapisy tej samej godziny w jednym oknie to jeden za dużo.
        _opener.Content = Value is { } time
            ? time.ToString(@"hh\:mm", CultureInfo.InvariantCulture)
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
    private static T Find<T>(UserControl where, string name)
        where T : Control =>
        where.FindControl<T>(name)
            ?? throw new InvalidOperationException($"Brak elementu „{name}” w układzie.");

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
