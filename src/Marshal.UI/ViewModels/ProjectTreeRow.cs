using Avalonia.Media;
using Marshal.Application.Review;
using Marshal.Application.UseCases;

namespace Marshal.UI.ViewModels;

/// <summary>
/// Wiersz ekranu „Projekty" — drzewo z warstwy aplikacji plus to, czego XAML
/// potrzebuje do narysowania: pędzel i widoczność kwadracika barwy.
/// </summary>
/// <remarks>
/// Osobny typ, a nie konwertery na <see cref="ProjectRow"/>: warstwa aplikacji nie zna
/// Avalonii i znać nie powinna, a konwerter byłby trzecim miejscem do zajrzenia przy
/// czytaniu jednego wiersza XAML-a. Ta sama zasada co przy blokach na siatce.
/// </remarks>
public sealed record ProjectTreeRow(ProjectRow Row, AreaBalance? Balance = null)
{
    private const string Brak = "#4A5580";

    public Guid Id => Row.Id;

    public string Label => Row.Label;

    public double Indent => Row.Indent;

    public string Marker => Row.Marker;

    public bool IsBlocked => Row.IsBlocked;

    public bool IsArea => Row.IsArea;

    /// <summary>Obszar wiersza. Dla wiersza obszaru to on sam.</summary>
    public Guid AreaId => Row.AreaId;

    /// <summary>Barwa po dziedziczeniu — ta sama, którą dostaną zadania.</summary>
    public string? Color => Row.Color;

    public bool HasColor => !string.IsNullOrWhiteSpace(Row.Color);

    /// <summary>
    /// Równowaga obszaru wpisana w ten sam wiersz.
    /// </summary>
    /// <remarks>
    /// Tabela równowagi miała własny ekran i to był błąd widoczny z czterech stron:
    /// na jednym ekranie dało się zmienić barwę i usunąć projekt, na drugim założyć
    /// obszar, a nazwę zmienić tylko tam — bo te same obiekty mieszkały w dwóch
    /// miejscach z różnymi możliwościami. Liczby są cechą obszaru, więc stoją przy nim.
    /// </remarks>
    public string BalanceText => Balance is not { } b
        ? string.Empty
        : b.DaysSinceMove is { } dni
            ? $"{b.ActiveProjects} aktywnych · {dni} dni bez ruchu"
            : $"{b.ActiveProjects} aktywnych · brak ruchu";

    public bool HasBalance => Balance is not null;

    /// <summary>Co da się tu założyć: w obszarze projekt, pod projektem podprojekt.</summary>
    public string AddLabel => IsArea ? "Nowy projekt tutaj…" : "Nowy podprojekt…";

    /// <summary>
    /// Kwadracik barwy. Bez przezroczystości — tutaj kolor jest **wybierany**, więc ma
    /// wyglądać dokładnie tak, jak został nazwany; przyciemnia się dopiero na siatce,
    /// gdzie leży pod tekstem.
    /// </summary>
    public IBrush Swatch =>
        Avalonia.Media.Color.TryParse(Row.Color ?? string.Empty, out var barwa)
            ? new SolidColorBrush(barwa)
            : new SolidColorBrush(Avalonia.Media.Color.Parse(Brak));
}

/// <summary>
/// Paleta do wyboru barwy obszaru i projektu.
/// </summary>
/// <remarks>
/// Zamknięta lista, nie pole tekstowe z szesnastkowym zapisem. Barwa ma tu jedno
/// zadanie: odróżnić obszary od siebie jednym spojrzeniem. Dowolność daje odcienie
/// nie do odróżnienia i barwy nieczytelne w jednym z dwóch motywów, a wybór spośród
/// ośmiu wystarczy na liczbę obszarów, jaką da się utrzymać w głowie.
/// </remarks>
public sealed record ColorChoice(string Label, string? Value)
{
    public static readonly IReadOnlyList<ColorChoice> All =
    [
        new("bez barwy", null),
        new("niebieski", "#4E7FD8"),
        new("zielony", "#3FA36B"),
        new("żółty", "#D8A93F"),
        new("pomarańczowy", "#DE7C3C"),
        new("czerwony", "#CF5757"),
        new("różowy", "#C862A6"),
        new("fioletowy", "#8367CE"),
        new("szary", "#78838F"),
    ];

    public override string ToString() => Label;
}
