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
    public static string Nazwa(int minutes)
    {
        if (minutes <= 0)
        {
            return "o czasie";
        }

        var days = minutes / (60 * 24);
        var godziny = minutes / 60 % 24;
        var reszta = minutes % 60;

        var parts = new List<string>();

        if (days > 0)
        {
            parts.Add(days == 1 ? "dzień" : $"{days} dni");
        }

        if (godziny > 0)
        {
            parts.Add($"{godziny} godz.");
        }

        if (reszta > 0)
        {
            parts.Add($"{reszta} min");
        }

        return $"{string.Join(' ', parts)} wcześniej";
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
