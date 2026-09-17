using CommunityToolkit.Mvvm.ComponentModel;

namespace Marshal.UI.ViewModels;

/// <summary>
/// Jedno wyprzedzenie na liście do zaznaczenia: ile minut przed godziną zadania
/// i czy jest włączone.
/// </summary>
/// <remarks>
/// Klasa obserwowalna, nie zapis wartości, bo kwadracik musi mieć co zmieniać.
/// Lista w oknie jest **jedna**: gotowe wyprzedzenia i te dopisane ręcznie stoją
/// obok siebie w kolejności czasu. Osobna lista „własnych" znaczyłaby, że to samo
/// piętnaście minut wygląda inaczej w zależności od tego, skąd się wzięło.
/// </remarks>
public sealed partial class LeadChoice(int minutes) : ObservableObject
{
    /// <summary>Ile minut przed godziną zadania. Zero znaczy dokładnie o niej.</summary>
    public int Minutes { get; } = minutes;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    public string Label => Nazwa(Minutes);

    /// <summary>Wyprzedzenie po ludzku: „o czasie", „15 min wcześniej", „dzień wcześniej".</summary>
    public static string Nazwa(int minuty)
    {
        if (minuty <= 0)
        {
            return "o czasie";
        }

        var dni = minuty / (60 * 24);
        var godziny = minuty / 60 % 24;
        var reszta = minuty % 60;

        var czesci = new List<string>();

        if (dni > 0)
        {
            czesci.Add(dni == 1 ? "dzień" : $"{dni} dni");
        }

        if (godziny > 0)
        {
            czesci.Add($"{godziny} godz.");
        }

        if (reszta > 0)
        {
            czesci.Add($"{reszta} min");
        }

        return $"{string.Join(' ', czesci)} wcześniej";
    }

    public override string ToString() => Label;
}

/// <summary>Jednostka przy dopisywaniu własnego wyprzedzenia — ile minut znaczy jedna.</summary>
public sealed record LeadUnitChoice(int Minutes, string Label)
{
    public static readonly IReadOnlyList<LeadUnitChoice> All =
    [
        new(1, "minut"),
        new(60, "godzin"),
        new(60 * 24, "dni"),
    ];

    public override string ToString() => Label;
}
