using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application.UseCases;
using Marshal.Domain.Habits;

namespace Marshal.UI.ViewModels;

/// <summary>Jeden kwadracik siatki — jeden dzień.</summary>
public sealed record HabitCell(IBrush Fill, string Tip);

/// <summary>Kolumna siatki — jeden tydzień, od poniedziałku.</summary>
public sealed record HabitWeek(IReadOnlyList<HabitCell> Days);

/// <summary>
/// Nawyk gotowy do narysowania: siatka, liczby i barwy.
/// </summary>
/// <remarks>
/// Barwy liczone tutaj, a nie w XAML-u: kafelek ma trzy stany na kwadracik — było,
/// zaczęte, nie było — i każdy jest tą samą barwą o innej sile. Wyrażone przelicznikami
/// w wiązaniach byłyby trzema regułami w trzech miejscach.
/// </remarks>
public sealed record HabitBox(HabitCard Card, IReadOnlyList<HabitWeek> Weeks)
{
    /// <summary>Krycie dnia zaliczonego — pełne, bo to jest treść kafelka.</summary>
    private const byte Full = 0xF2;

    /// <summary>Dzień zaczęty, ale poniżej progu.</summary>
    private const byte Half = 0x80;

    /// <summary>Dzień pusty. Widoczny, bo siatka bez pustych dni nie jest siatką.</summary>
    private const byte Empty = 0x24;

    public static HabitBox From(HabitCard card)
    {
        var cells = card.Days
            .Select(day => new HabitCell(
                Palette.Background(
                    card.Habit.Color,
                    task: true,
                    day.Counts ? Full : day.Partial ? Half : Empty),
                $"{day.Day:dd.MM.yyyy} — {(day.Amount > 0 ? day.Amount.ToString() : "nic")}"))
            .ToList();

        var weeks = new List<HabitWeek>();

        for (var from = 0; from < cells.Count; from += 7)
        {
            weeks.Add(new HabitWeek(cells.GetRange(from, Math.Min(7, cells.Count - from))));
        }

        return new HabitBox(card, weeks);
    }

    public Habit Habit => Card.Habit;

    public string Title => Card.Habit.Title;

    public Guid Id => Card.Habit.Id;

    public bool DoneToday => Card.DoneToday;

    public string Mark => DoneToday ? "✓" : string.Empty;

    public string Score => Card.Score;

    public bool HasScore => Card.HasScore;

    public IBrush Background => Palette.Background(Card.Habit.Color, task: true, Empty);

    /// <summary>Seria jednym zdaniem — bo to jest powód, dla którego się tu patrzy.</summary>
    public string Streak => Card.Streak switch
    {
        0 => "seria zerwana",
        1 => "1 dzień z rzędu",
        var days => $"{days} dni z rzędu",
    };

    public string Totals => $"rekord {Card.Best}  ·  razem {Card.Total}";
}

/// <summary>
/// Nawyki: siatka dni zamiast listy do odhaczenia (prośba z 4.10).
/// </summary>
/// <remarks>
/// <para>
/// Ekran istnieje po to, żeby było <b>widać</b>. Lista z ptaszkami odpowiada na „czy
/// dziś było" i nic poza tym; siatka odpowiada na „jak to szło" jednym spojrzeniem,
/// a odpowiedź na to drugie jest jedynym powodem, dla którego ktokolwiek nawyk ciągnie.
/// </para>
/// <para>
/// Niezależny od kalendarza i od zadań. Nawyk nie wchodzi do skrzynki, do przeglądu
/// ani do liczników zaległości — zob. <see cref="Habit"/>.
/// </para>
/// </remarks>
public sealed partial class HabitsViewModel(HabitService habits) : ObservableObject
{
    public ObservableCollection<HabitBox> Items { get; } = [];

    [ObservableProperty]
    public partial string NewTitle { get; set; } = string.Empty;

    public bool HasItems => Items.Count > 0;

    /// <summary>Pustka nazwana wprost, bo pusty ekran sam z siebie nic nie mówi.</summary>
    public string Message =>
        "Nawyk to rzecz, której się nie odhacza z listy, tylko się ją ciągnie. "
        + "Wpisz nazwę i dotykaj kafelka każdego dnia — siatka pokaże resztę.";

    public async Task LoadAsync()
    {
        var board = await habits.BoardAsync();

        Items.Clear();
        foreach (var card in board)
        {
            Items.Add(HabitBox.From(card));
        }

        OnPropertyChanged(nameof(HasItems));
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(NewTitle))
        {
            return;
        }

        await habits.CreateAsync(NewTitle.Trim());
        NewTitle = string.Empty;

        await LoadAsync();
    }

    /// <summary>Dotknięcie kafelka: dziś zrobione, a przy nawyku na ilość — o jeden więcej.</summary>
    [RelayCommand]
    private async Task BumpAsync(HabitBox? box)
    {
        if (box is null)
        {
            return;
        }

        await habits.BumpAsync(box.Id);
        await LoadAsync();
    }

    /// <summary>Odłożenie na półkę — historia zostaje, seria przestaje się liczyć.</summary>
    [RelayCommand]
    private async Task ArchiveAsync(HabitBox? box)
    {
        if (box is not null)
        {
            await habits.ArchiveAsync(box.Id);
            await LoadAsync();
        }
    }
}
