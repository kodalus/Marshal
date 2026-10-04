using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application.UseCases;
using Marshal.Domain.Habits;

namespace Marshal.UI.ViewModels;

/// <summary>Jeden kwadracik siatki — jeden dzień.</summary>
/// <remarks>
/// Niesie swój dzień i swoją liczbę, bo w szczegółach nawyku kwadracik jest klikalny:
/// poprawianie dni wstecz jest tym, po co się tam w ogóle wchodzi. Ktoś odhaczył
/// wieczorem po północy, ktoś zapomniał telefonu — siatka ma mówić prawdę o tym,
/// jak było, a nie o tym, kiedy zdążył kliknąć.
/// </remarks>
public sealed record HabitCell(DateOnly Day, int Amount, bool Counts, IBrush Fill, string Tip);

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
    private const byte Half = 0x72;

    /// <summary>Dzień pusty. Widoczny, bo siatka bez pustych dni nie jest siatką.</summary>
    private const byte Empty = 0x2E;

    /// <summary>
    /// Kwadraciki rysowane <b>bielą na barwie kafelka</b>, a nie barwą na tle okna.
    /// </summary>
    /// <remarks>
    /// Kafelek niesie barwę nawyku w pełnej sile, bo to po niej rozpoznaje się go
    /// z odległości ręki — i wtedy siatka w tej samej barwie zlewa się z tłem. Biel
    /// o trzech kryciach działa tak samo przy każdym odcieniu: nie trzeba jej dobierać
    /// do barwy, a kontrast jest ten sam na fiolecie i na żółci.
    /// </remarks>
    private static IBrush Square(byte opacity) =>
        new SolidColorBrush(Color.FromArgb(opacity, 0xFF, 0xFF, 0xFF));

    public static HabitBox From(HabitCard card)
    {
        var cells = card.Days
            .Select(day => new HabitCell(
                day.Day,
                day.Amount,
                day.Counts,
                Square(day.Counts ? Full : day.Partial ? Half : Empty),
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

    /// <summary>
    /// Tło kafelka — barwa nawyku w pełnej sile.
    /// </summary>
    /// <remarks>
    /// Przy sześciu kafelkach pod sobą barwa odpowiada na „który to" szybciej niż
    /// nazwa, ale tylko wtedy, gdy jest jej dość, żeby ją zobaczyć. Prześwit na tle
    /// okna dawał sześć prostokątów w sześciu odcieniach granatu — różnicę dało się
    /// znaleźć, patrząc, a nie zobaczyć.
    /// </remarks>
    public IBrush Background => Palette.Background(Card.Habit.Color, task: true, 0xFF);

    /// <summary>Seria jednym zdaniem — bo to jest powód, dla którego się tu patrzy.</summary>
    public string Streak => Card.Streak switch
    {
        0 => "seria zerwana",
        1 => "1 dzień z rzędu",
        var days => $"{days} dni z rzędu",
    };

    public string Totals => $"rekord {Card.Best}  ·  razem {Card.Total}";

    // Liczby do szczegółu. Osobno, bo kafelek na liście ma mówić jednym spojrzeniem,
    // a szczegół — odpowiadać na pytania, które się po tym spojrzeniu pojawiają.

    public string StreakNumber => Card.Streak.ToString();

    public string BestNumber => Card.Best.ToString();

    public string TotalNumber => Card.Total.ToString();

    public string MissedNumber => Card.Missed.ToString();

    public string SinceNumber => Card.Since.ToString();

    public string RateNumber => $"{Card.Rate}%";

    public string StartedLabel => Card.Total == 0
        ? "jeszcze nie ruszyło"
        : $"od {Card.Started:dd.MM.yyyy}";
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

    public async Task CreateAsync()
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
    public async Task BumpAsync(HabitBox? box)
    {
        if (box is null)
        {
            return;
        }

        await habits.BumpAsync(box.Id);
        await LoadAsync();
    }

    /// <summary>Odłożenie na półkę — historia zostaje, seria przestaje się liczyć.</summary>
    public async Task ArchiveAsync(HabitBox? box)
    {
        if (box is not null)
        {
            await habits.ArchiveAsync(box.Id);
            Close();
            await LoadAsync();
        }
    }

    // ——— Szczegół nawyku ———————————————————————————————————————————————

    /// <summary>
    /// Otwarty nawyk. Szczegół zamiast listy, nie nakładka nad nią.
    /// </summary>
    /// <remarks>
    /// Tak samo jak przy notatkach: wchodzi się tu, żeby coś poprawić, a nie zerknąć,
    /// więc ekran ma należeć do tej jednej rzeczy. Nakładka zostawia pod spodem listę,
    /// której i tak nie widać, a zabiera jej miejsce na siatkę.
    /// </remarks>
    [ObservableProperty]
    public partial HabitBox? Opened { get; set; }

    public bool IsEditing => Opened is not null;

    partial void OnOpenedChanged(HabitBox? value)
    {
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(EditColorBrush));
    }

    [ObservableProperty]
    public partial string EditTitle { get; set; } = string.Empty;

    /// <summary>Próg dnia. Pusty znaczy „nawyk na ptaszek".</summary>
    [ObservableProperty]
    public partial decimal? EditTarget { get; set; }

    [ObservableProperty]
    public partial string EditUnit { get; set; } = string.Empty;

    /// <summary>Barwa otwartego nawyku, do pokazania na przycisku wybieraczki.</summary>
    public IBrush EditColorBrush => Opened is { } box
        ? Palette.Background(box.Habit.Color, task: true, 0xFF)
        : Brushes.Transparent;

    /// <summary>
    /// Barwa z wybieraczki — zapisywana od razu, bez „Zapisz".
    /// </summary>
    /// <remarks>
    /// Tak samo jak przy obszarach i projektach, i z tego samego powodu: barwę wybiera
    /// się patrząc, a patrzy się na kafelek, nie na formularz. Drugi krok kazałby wracać
    /// wzrokiem do przycisku, żeby zobaczyć to, co już widać.
    /// </remarks>
    public async Task PickColorAsync(string? color)
    {
        if (Opened is not { } box)
        {
            return;
        }

        await habits.SetColorAsync(box.Id, color);
        await LoadAsync();

        Opened = Items.FirstOrDefault(z => z.Id == box.Id);
    }

    /// <summary>
    /// Czy kasowanie jest już uzbrojone.
    /// </summary>
    /// <remarks>
    /// Drugie kliknięcie zamiast okienka — tak samo jak przy przywracaniu kopii i z tego
    /// samego powodu: skasowanie nawyku zabiera <b>całą jego historię</b>, czyli jedyną
    /// rzecz, która ma w nim wartość. Odłożenie na półkę stoi obok i jest odwracalne —
    /// i to ono ma być tym, po co sięga się najczęściej.
    /// </remarks>
    [ObservableProperty]
    public partial bool DeleteArmed { get; set; }

    /// <summary>
    /// Co się właśnie stało. Puste, dopóki nic.
    /// </summary>
    /// <remarks>
    /// Zapis, który zmienia samą nazwę, nie zmienia na ekranie **niczego** — pole
    /// już tę nazwę pokazuje. Przycisk bez odpowiedzi wygląda wtedy dokładnie jak
    /// przycisk zepsuty, a odróżnić jednego od drugiego nie da się inaczej niż
    /// wychodząc z ekranu i wracając.
    /// </remarks>
    [ObservableProperty]
    public partial string? Status { get; set; }

    public bool HasStatus => !string.IsNullOrEmpty(Status);

    partial void OnStatusChanged(string? value) => OnPropertyChanged(nameof(HasStatus));

    public string DeleteLabel => DeleteArmed ? "Na pewno? Kliknij jeszcze raz" : "Skasuj";

    partial void OnDeleteArmedChanged(bool value) => OnPropertyChanged(nameof(DeleteLabel));

    public void Open(HabitBox? box)
    {
        if (box is null)
        {
            return;
        }

        Opened = box;
        EditTitle = box.Habit.Title;
        EditTarget = box.Habit.Target;
        EditUnit = box.Habit.Unit ?? string.Empty;
        DeleteArmed = false;
        Status = null;
    }

    public void Close()
    {
        Opened = null;
        DeleteArmed = false;
        Status = null;
    }

    /// <summary>Zapis nazwy, progu i barwy — trzy pola, jeden przycisk.</summary>
    public async Task SaveAsync()
    {
        if (Opened is not { } box)
        {
            return;
        }

        // Pusta nazwa mówi, czemu nic się nie stało. Milczące wyjście w tym miejscu było
        // nieodróżnialne od zepsutego przycisku — a pole samo z siebie nie mówi, że jest
        // wymagane.
        if (string.IsNullOrWhiteSpace(EditTitle))
        {
            Status = "Nazwa nie może być pusta.";
            return;
        }

        await habits.RenameAsync(box.Id, EditTitle.Trim());
        await habits.SetTargetAsync(
            box.Id,
            EditTarget is { } target ? (int)target : null,
            EditUnit);

        await LoadAsync();

        // Otwarty zostaje otwarty, tylko świeży: po zmianie progu siatka wygląda inaczej,
        // bo dni poniżej nowej wartości przestają się liczyć. Zamknięcie szczegółu
        // kazałoby wejść drugi raz, żeby to zobaczyć.
        Opened = Items.FirstOrDefault(z => z.Id == box.Id);

        Status = "Zapisane.";
    }

    /// <summary>
    /// Przestawienie dnia wstecz.
    /// </summary>
    /// <remarks>
    /// Przy nawyku na ptaszek zwykłe przełączenie. Przy nawyku na ilość: zaliczony dzień
    /// schodzi do zera, a każdy inny skacze **do progu** — bo poprawia się tu zwykle
    /// „było, tylko nie kliknęłam", a nie „było dokładnie siedem stron".
    /// </remarks>
    public async Task ToggleDayAsync(HabitCell? cell)
    {
        if (Opened is not { } box || cell is null)
        {
            return;
        }

        var target = Math.Max(1, box.Habit.Target ?? 1);

        await habits.SetAsync(box.Id, cell.Day, cell.Counts ? 0 : target);
        await LoadAsync();

        Opened = Items.FirstOrDefault(z => z.Id == box.Id);
    }

    /// <summary>Skasowanie razem z historią — stąd dwa kliknięcia.</summary>
    public async Task DeleteAsync()
    {
        if (Opened is not { } box)
        {
            return;
        }

        if (!DeleteArmed)
        {
            DeleteArmed = true;
            return;
        }

        await habits.DeleteAsync(box.Id);
        Close();
        await LoadAsync();
    }
}
