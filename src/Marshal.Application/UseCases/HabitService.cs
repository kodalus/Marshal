using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Domain.Habits;

namespace Marshal.Application.UseCases;

/// <summary>Jeden dzień nawyku, gotowy do narysowania.</summary>
public sealed record HabitDay(DateOnly Day, int Amount, bool Counts)
{
    /// <summary>Zaczęty, ale poniżej progu — przy nawyku na ilość.</summary>
    public bool Partial => !Counts && Amount > 0;
}

/// <summary>
/// Nawyk z wszystkim, co o nim mówi ekran.
/// </summary>
/// <remarks>
/// Liczby obok siatki, nie zamiast niej. Siatka odpowiada na „jak to szło" jednym
/// spojrzeniem i to ona jest tu treścią; liczby odpowiadają na „ile dokładnie" i są
/// potrzebne dopiero wtedy, gdy ktoś już patrzy.
/// </remarks>
public sealed record HabitCard(
    Habit Habit,
    IReadOnlyList<HabitDay> Days,
    int Streak,
    int Best,
    int Total,
    int Today,
    DateOnly Started,
    int Missed)
{
    /// <summary>Ile dni temu to się zaczęło — razem z dzisiejszym.</summary>
    public int Since => Days.Count > 0
        ? Math.Max(1, Days[^1].Day.DayNumber - Started.DayNumber + 1)
        : 1;

    /// <summary>
    /// Ile z nich wyszło, w procentach.
    /// </summary>
    /// <remarks>
    /// Liczone od pierwszego zaliczonego dnia, nie od założenia nawyku. Nawyk wpisany
    /// w styczniu i zaczęty w marcu miałby inaczej dwa miesiące kary za to, że został
    /// wpisany wcześniej — a to jest liczba, którą się ogląda po to, żeby się nie zniechęcić.
    /// </remarks>
    public int Rate => Since == 0 ? 0 : (int)Math.Round(100.0 * Total / Since);

    public bool DoneToday => Today >= Math.Max(1, Habit.Target ?? 1);

    /// <summary>Ile razem — „4/20 stron" albo sam ptaszek.</summary>
    public string Score => Habit.Target is { } target
        ? $"{Today}/{target}{(Habit.Unit is { Length: > 0 } unit ? $" {unit}" : string.Empty)}"
        : string.Empty;

    public bool HasScore => Score.Length > 0;
}

/// <summary>
/// Nawyki: siatka dni, seria i dopisywanie dzisiejszego.
/// </summary>
/// <remarks>
/// <para>
/// <b>Osobno od zadań</b> — zob. <see cref="Habit"/>. Nawyk nie wchodzi do skrzynki,
/// do przeglądu ani do liczników zaległości, bo nie jest rzeczą do zrobienia, tylko
/// rzeczą, którą się ciągnie. Pięć nawyków dziennie wpisanych na listę zadań
/// zasypałoby ją tym, co i tak się wydarzy.
/// </para>
/// <para>
/// <b>Seria, rekord i licznik liczone od początku, siatka z okna.</b> Liczby z okna
/// kłamałyby u kogoś, kto ciągnie coś drugi rok — „rekord: 17 tygodni" znaczyłoby
/// wtedy tylko tyle, ile okno ma szerokości.
/// </para>
/// </remarks>
public sealed class HabitService(
    IHabitRepository habits,
    IUnitOfWork unitOfWork,
    IClock clock,
    IHlcSource hlc)
{
    /// <summary>Ile dni pokazuje siatka. Siedemnaście tygodni — tyle mieści telefon.</summary>
    public const int Window = 119;

    public async Task<IReadOnlyList<HabitCard>> BoardAsync(CancellationToken ct = default)
    {
        var list = await habits.ListAsync(ct: ct);

        if (list.Count == 0)
        {
            return [];
        }

        var marks = (await habits.MarksAsync(ct))
            .GroupBy(m => m.HabitId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(m => m.Day, m => m.Amount));

        var today = clock.Today;

        // Okno zaczyna się od poniedziałku, żeby wiersze siatki znaczyły dni tygodnia.
        // Bez wyrównania kolumna nie jest tygodniem i „zawsze wypada mi w weekendy"
        // przestaje dać się zobaczyć — a to jest jedna z dwóch rzeczy, które taka
        // siatka w ogóle mówi.
        var first = today.AddDays(-(Window - 1));
        var start = first.AddDays(-(((int)first.DayOfWeek + 6) % 7));

        var cards = new List<HabitCard>(list.Count);

        foreach (var habit in list)
        {
            var mine = marks.TryGetValue(habit.Id, out var found)
                ? found
                : new Dictionary<DateOnly, int>();

            var threshold = Math.Max(1, habit.Target ?? 1);
            var counting = mine.Where(z => z.Value >= threshold).Select(z => z.Key).ToHashSet();

            // Początek to **pierwszy zaliczony dzień**, a nie dzień założenia. Nawyk
            // wpisany w styczniu i zaczęty w marcu miałby inaczej dwa miesiące
            // pominiętych dni, zanim w ogóle ruszył.
            var began = counting.Count > 0 ? counting.Min() : today;

            cards.Add(new HabitCard(
                habit,
                [.. Enumerable.Range(0, today.DayNumber - start.DayNumber + 1)
                    .Select(step => start.AddDays(step))
                    .Select(day => new HabitDay(
                        day,
                        mine.GetValueOrDefault(day),
                        counting.Contains(day)))],
                Streak(counting, today),
                Best(counting),
                counting.Count,
                mine.GetValueOrDefault(today),
                began,
                Missed(counting, began, today)));
        }

        return cards;
    }

    /// <summary>
    /// Dopisanie dzisiejszego. Przy nawyku na ptaszek przestawia, przy nawyku na ilość dolicza.
    /// </summary>
    /// <remarks>
    /// Dotknięcie dzisiejszego kafelka jest jedyną czynnością, którą robi się codziennie,
    /// więc nie pyta o nic. Pomyłka kosztuje drugie dotknięcie: zaliczony dzień schodzi
    /// do zera tym samym miejscem — przy ptaszku i przy ilości jednakowo.
    ///
    /// <b>Dokładanie po jednym jest drogą dla małych progów</b>, nie dla wszystkich.
    /// Osiem szklanek wody tak się właśnie pije, ale do dwudziestu stron dochodziło się
    /// dwudziestoma dotknięciami — dlatego liczbę wpisuje się też wprost, tam gdzie
    /// jest pokazana (<see cref="SetAsync"/>).
    /// </remarks>
    public async Task<int> BumpAsync(Guid habitId, int by = 1, CancellationToken ct = default)
    {
        if (await habits.FindAsync(habitId, ct) is not { } habit)
        {
            return 0;
        }

        var today = clock.Today;
        var mark = await habits.MarkAsync(habitId, today, ct);

        if (mark is null)
        {
            mark = HabitMark.On(habitId, today, clock.Now, hlc.Next());
            habits.Add(mark);
        }

        var amount = habit.Target switch
        {
            // Ptaszek przestawia się, a nie rośnie: nawyk bez progu ma dwa stany i drugie
            // dotknięcie ma je zdejmować, a nie robić „dwa razy medytacja".
            null => mark.Amount > 0 ? 0 : 1,

            // Nawyk na ilość też musi dawać się odhaczyć z powrotem. Bez tego dzień raz
            // zaliczony rósł dalej — 21, 22, 23 stron — i nie było w całym programie
            // miejsca, w którym da się powiedzieć „pomyłka, dzisiaj tego nie było".
            { } goal when mark.Amount >= Math.Max(1, goal) => 0,

            _ => mark.Amount + by,
        };

        mark.Set(amount, hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);

        return mark.Amount;
    }

    /// <summary>Wpisanie liczby wprost — poprawka, nie dokładanie.</summary>
    public async Task SetAsync(
        Guid habitId, DateOnly day, int amount, CancellationToken ct = default)
    {
        var mark = await habits.MarkAsync(habitId, day, ct);

        if (mark is null)
        {
            if (amount <= 0)
            {
                return;
            }

            mark = HabitMark.On(habitId, day, clock.Now, hlc.Next());
            habits.Add(mark);
        }

        mark.Set(amount, hlc.Next());
        await unitOfWork.SaveChangesAsync(ct);
    }

    public async Task<Habit> CreateAsync(
        string title, int? target = null, string? unit = null, string? color = null,
        CancellationToken ct = default)
    {
        var list = await habits.ListAsync(ct: ct);
        var habit = Habit.Create(title, list.Count, clock.Now, hlc.Next());

        if (target is not null)
        {
            habit.SetTarget(target, unit, hlc.Next());
        }

        if (color is not null)
        {
            habit.SetColor(color, hlc.Next());
        }

        habits.Add(habit);
        await unitOfWork.SaveChangesAsync(ct);

        return habit;
    }

    public Task RenameAsync(Guid id, string title, CancellationToken ct = default) =>
        ChangeAsync(id, h => h.Rename(title, hlc.Next()), ct);

    public Task SetTargetAsync(
        Guid id, int? target, string? unit, CancellationToken ct = default) =>
        ChangeAsync(id, h => h.SetTarget(target, unit, hlc.Next()), ct);

    public Task SetColorAsync(Guid id, string? color, CancellationToken ct = default) =>
        ChangeAsync(id, h => h.SetColor(color, hlc.Next()), ct);

    /// <summary>Odłożenie: historia zostaje, seria przestaje się liczyć.</summary>
    public Task ArchiveAsync(Guid id, CancellationToken ct = default) =>
        ChangeAsync(id, h => h.Archive(hlc.Next()), ct);

    public Task ReviveAsync(Guid id, CancellationToken ct = default) =>
        ChangeAsync(id, h => h.Revive(hlc.Next()), ct);

    /// <summary>Nagrobek — razem z historią, bo bez nawyku nie znaczy ona nic.</summary>
    public Task DeleteAsync(Guid id, CancellationToken ct = default) =>
        ChangeAsync(id, h => h.MarkDeleted(hlc.Next()), ct);

    private async Task ChangeAsync(Guid id, Action<Habit> change, CancellationToken ct)
    {
        if (await habits.FindAsync(id, ct) is not { } habit)
        {
            return;
        }

        change(habit);
        await unitOfWork.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Ile dni przepadło między pierwszym zaliczonym a dzisiaj.
    /// </summary>
    /// <remarks>
    /// <b>Dzisiejszy jeszcze nieodhaczony nie jest pominięty</b> — dokładnie tak samo,
    /// jak nie zrywa serii. Dzień trwa; policzony jako przepadnięty byłby karą za to,
    /// że jest rano, a rano jest tą chwilą, w której na te liczby się patrzy.
    /// Stąd „razem" i „pominięte" nie muszą się zsumować do „dni od startu": brakującą
    /// jedynką jest dzisiejszy, wciąż otwarty.
    /// </remarks>
    private static int Missed(HashSet<DateOnly> counting, DateOnly began, DateOnly today)
    {
        var judged = counting.Contains(today) ? today : today.AddDays(-1);

        return Math.Max(0, judged.DayNumber - began.DayNumber + 1 - counting.Count);
    }

    /// <summary>
    /// Ile dni z rzędu, licząc wstecz.
    /// </summary>
    /// <remarks>
    /// <b>Dzisiejszy jeszcze nieodhaczony serii nie zrywa.</b> Dzień trwa; seria
    /// pokazana jako zerwana o poranku byłaby karą za to, że jest rano — a to jest
    /// dokładnie ta chwila, w której na serię się patrzy, żeby jej nie przerwać.
    /// Zerwana jest dopiero wtedy, gdy wczorajszego nie ma.
    /// </remarks>
    private static int Streak(HashSet<DateOnly> counting, DateOnly today)
    {
        var from = counting.Contains(today) ? today : today.AddDays(-1);
        var days = 0;

        while (counting.Contains(from))
        {
            days++;
            from = from.AddDays(-1);
        }

        return days;
    }

    /// <summary>Najdłuższy ciąg w całej historii.</summary>
    private static int Best(HashSet<DateOnly> counting)
    {
        var best = 0;

        foreach (var day in counting)
        {
            // Liczymy tylko od początków ciągów — inaczej ciąg długi na sto dni
            // przeszedłby się sto razy.
            if (counting.Contains(day.AddDays(-1)))
            {
                continue;
            }

            var run = 0;
            var walk = day;

            while (counting.Contains(walk))
            {
                run++;
                walk = walk.AddDays(1);
            }

            best = Math.Max(best, run);
        }

        return best;
    }
}
