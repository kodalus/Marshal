using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application.Abstractions;
using Marshal.Application.Repositories;
using Marshal.Application.UseCases;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Diagnostics;
using Marshal.Domain.Tasks;

namespace Marshal.UI.ViewModels;

/// <summary>
/// Szczegół zadania: termin, dzień wykonania, przypomnienie i rytm (etap 4).
/// </summary>
/// <remarks>
/// Jedno wejście do wszystkiego, co etap 4 dołożył do modelu. Osobny ekran na każdą
/// z tych rzeczy oznaczałby cztery miejsca do znalezienia zamiast jednego, a wszystkie
/// cztery dotyczą tej samej decyzji: kiedy to ma się zdarzyć.
/// </remarks>
public sealed partial class TaskDetailViewModel(
    TaskEditService edit, IClock clock, IAreaRepository areas, IProjectRepository projects,
    InboxService inbox, IActivityLog log)
    : ObservableObject
{
    private Guid _id;
    private bool _loading;

    /// <summary>Ile trwa zadanie z godziną, ale bez podanego końca (spec 11).</summary>
    private const int DefaultMinutes = 30;

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Note { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateTimeOffset? DoDate { get; set; }

    /// <summary>
    /// Godzina rozpoczęcia i zakończenia.
    /// </summary>
    /// <remarks>
    /// Koniec nie jest osobnym polem w modelu: zadanie ma oszacowanie długości i to
    /// ono rysuje blok na siatce. Koniec jest więc innym sposobem powiedzenia tego
    /// samego — wpisanie go przelicza się na minuty, a brak zostawia trzydzieści.
    /// Dwie prawdy o jednej rzeczy rozjechałyby się przy pierwszej zmianie jednej z nich.
    /// </remarks>
    [ObservableProperty]
    public partial TimeSpan? DoTime { get; set; }

    [ObservableProperty]
    public partial TimeSpan? EndTime { get; set; }

    /// <summary>
    /// Miejsce zadania: obszar albo projekt w nim. Puste znaczy „jeszcze
    /// nierozstrzygnięte” — tak wygląda wrzut.
    /// </summary>
    /// <remarks>
    /// Do dziś było tu samo pole obszaru, a projekt dało się ustawić wyłącznie z menu
    /// podręcznego na liście — czyli szczegół zadania pokazywał **część** jego
    /// przynależności i nie dawało się jej stąd poprawić. Jedno drzewko zamiast dwóch
    /// pól zamyka też stan sprzeczny: projekt z jednego obszaru przy wybranym drugim.
    /// </remarks>
    [ObservableProperty]
    public partial PlacementChoice? SelectedPlacement { get; set; }

    public ObservableCollection<PlacementChoice> Placements { get; } = [];

    /// <summary>
    /// Czy pokazywać resztę pól.
    /// </summary>
    /// <remarks>
    /// Okno miało dwanaście pól jedno pod drugim i trzeba było przewijać, żeby dojść
    /// do przycisku zapisu. Widoczne zostaje to, co wypełnia się przy **każdym**
    /// zadaniu — nazwa, dzień, godziny — a rzadkie schodzą pod jeden przełącznik.
    /// Pole, które w dziewięciu zadaniach na dziesięć zostaje puste, jest w oknie
    /// kosztem, a nie możliwością.
    /// </remarks>
    [ObservableProperty]
    public partial bool ShowMore { get; set; }

    /// <summary>
    /// Pojedyncze sekcje pod paskiem ikon.
    /// </summary>
    /// <remarks>
    /// Każda ikona odsłania swoją rzecz, zamiast jednego przełącznika na wszystko:
    /// dopisanie terminu nie ma wywlekać rytmu, wagi i energii, których się nie
    /// dotyka. Sekcja z wypełnioną wartością otwiera się sama — ukryta wartość,
    /// która coś znaczy, jest gorsza od pustego pola na wierzchu.
    /// </remarks>
    [ObservableProperty]
    public partial bool ShowDeadline { get; set; }

    [ObservableProperty]
    public partial bool ShowReminder { get; set; }

    [ObservableProperty]
    public partial bool ShowPriority { get; set; }

    [ObservableProperty]
    public partial bool ShowEnergy { get; set; }

    [ObservableProperty]
    public partial bool ShowRepeat { get; set; }

    /// <summary>Podsumowanie schowanego: żeby zwinięte nie znaczyło „nie wiadomo co tam jest".</summary>
    public string MoreSummary
    {
        get
        {
            var parts = new List<string>();

            if (Deadline is { } deadline)
            {
                parts.Add($"termin {deadline:dd.MM}");
            }

            if (ReminderDay is not null || Leads.Any(w => w.IsChecked))
            {
                parts.Add("przypomnienie");
            }

            if (Waga != Priority.None)
            {
                parts.Add(SelectedPriority!.Label);
            }

            if (Sila != Energy.Unknown)
            {
                parts.Add(SelectedEnergyLevel!.Label);
            }

            if (Rytm is not null)
            {
                parts.Add("powtarza się");
            }

            return parts.Count == 0 ? "termin, przypomnienie, waga, energia, rytm"
                : string.Join(" · ", parts);
        }
    }

    /// <summary>Co poszło nie tak przy zapisie. Puste, gdy poszło.</summary>
    [ObservableProperty]
    public partial string? Problem { get; set; }

    public bool HasProblem => !string.IsNullOrEmpty(Problem);

    /// <summary>
    /// Czy zadanie jest odhaczone.
    /// </summary>
    /// <remarks>
    /// Kwadracik przy nazwie stał dotąd pusty także przy zadaniu zrobionym — pokazywał
    /// więc nie stan, tylko sam siebie. Przy zadaniu odhaczonym gdzie indziej wyglądało
    /// to jak zgubione odhaczenie.
    /// </remarks>
    [ObservableProperty]
    public partial bool IsDone { get; set; }

    /// <summary>Czy okno pokazuje zadanie, które już istnieje.</summary>
    /// <remarks>
    /// Przy zakładaniu nie ma czego odhaczać, a przycisk „Zrobione" stojący obok
    /// „Zapisz" był pułapką: oba zamykają okno, więc pomyłki nie było jak zauważyć.
    /// </remarks>
    public bool IsExisting => _id != Guid.Empty;

    [ObservableProperty]
    public partial DateTimeOffset? Deadline { get; set; }

    /// <summary>
    /// Dzień i pora przypomnienia osobno, bo wybiera się je osobno. Złożenie w chwilę
    /// dzieje się przy zapisie — <see cref="ReminderAt"/>.
    /// </summary>
    [ObservableProperty]
    public partial DateTimeOffset? ReminderDay { get; set; }

    [ObservableProperty]
    public partial TimeSpan? ReminderTime { get; set; }

    /// <summary>
    /// Wyprzedzenia zadania z godziną: ile przed nią ma się odezwać. Kilka naraz,
    /// bo jedno rzadko wystarcza — dobę wcześniej, żeby się przygotować, i kwadrans
    /// wcześniej, żeby wyjść.
    /// </summary>
    /// <remarks>
    /// Lista trzyma gotowy zestaw **oraz** to, co zadanie ma zapisane, także spoza
    /// zestawu. Dopisane ręcznie wskakuje na swoje miejsce w kolejności czasu, więc
    /// nie ma dwóch sposobów na to samo wyprzedzenie.
    /// </remarks>
    public ObservableCollection<LeadChoice> Leads { get; } = [];

    /// <summary>Zestaw pod ręką. Reszta dopisywana polem obok, bez zaśmiecania listy.</summary>
    private static readonly int[] Gotowe = [0, 5, 15, 30, 60, 60 * 24];

    /// <summary>Liczba dziesiętna z tego samego powodu co odstęp rytmu: NumericUpDown.</summary>
    [ObservableProperty]
    public partial decimal? CustomLead { get; set; } = 10;

    [ObservableProperty]
    public partial LeadUnitChoice? SelectedLeadUnit { get; set; } = LeadUnitChoice.All[0];

    public IReadOnlyList<LeadUnitChoice> LeadUnits => LeadUnitChoice.All;

    /// <summary>
    /// Czy zadanie ma godzinę. Wyprzedzenia liczą się **od niej**, więc bez godziny
    /// nie ma od czego — wtedy okno pokazuje przypomnienie z własnym dniem i porą.
    /// </summary>
    public bool HasTime => DoTime is not null;

    [ObservableProperty]
    public partial PriorityChoice? SelectedPriority { get; set; } = PriorityChoice.All[0];

    /// <summary>Liczba dziesiętna z tego samego powodu co odstęp rytmu: NumericUpDown.</summary>
    [ObservableProperty]
    public partial decimal? EstimatedMinutes { get; set; }

    [ObservableProperty]
    public partial EnergyLevelChoice? SelectedEnergyLevel { get; set; } = EnergyLevelChoice.All[0];

    // --- rytm ----------------------------------------------------------------

    [ObservableProperty]
    public partial RepeatChoice? SelectedRepeat { get; set; } = RepeatChoice.All[0];

    /// <summary>
    /// Liczby dziesiętne, nie całkowite — <c>NumericUpDown</c> operuje na
    /// <see cref="decimal"/>, a powiązanie kompilowane nie zamienia typu za nas.
    /// Zamiana na liczbę całkowitą dzieje się przy budowaniu reguły.
    /// </summary>
    [ObservableProperty]
    public partial decimal Interval { get; set; } = 1;

    [ObservableProperty]
    public partial bool Monday { get; set; }

    [ObservableProperty]
    public partial bool Tuesday { get; set; }

    [ObservableProperty]
    public partial bool Wednesday { get; set; }

    [ObservableProperty]
    public partial bool Thursday { get; set; }

    [ObservableProperty]
    public partial bool Friday { get; set; }

    [ObservableProperty]
    public partial bool Saturday { get; set; }

    [ObservableProperty]
    public partial bool Sunday { get; set; }

    [ObservableProperty]
    public partial decimal? DayOfMonth { get; set; }

    [ObservableProperty]
    public partial AnchorChoice? SelectedAnchor { get; set; } = AnchorChoice.All[0];

    [ObservableProperty]
    public partial MissedChoice? SelectedMissed { get; set; } = MissedChoice.All[0];

    public IReadOnlyList<RepeatChoice> Repeats => RepeatChoice.All;

    public IReadOnlyList<AnchorChoice> Anchors => AnchorChoice.All;

    public IReadOnlyList<MissedChoice> Missed => MissedChoice.All;

    public IReadOnlyList<PriorityChoice> Priorities => PriorityChoice.All;

    public IReadOnlyList<EnergyLevelChoice> Energies => EnergyLevelChoice.All;

    /// <summary>
    /// Rytm zdaniem, nie formularzem. Sześć pól da się wypełnić źle i nie zauważyć;
    /// zdanie da się przeczytać i od razu wiedzieć, czy to jest to, o co chodziło.
    /// </summary>
    public string Summary
    {
        get
        {
            var rule = BuildRule(out var problem);

            // Reguła niepełna nie może pokazywać się jako „nie powtarza się" — wybrałaś
            // „co tydzień", a zdanie mówiłoby, że rytmu nie ma. Zdanie musi mówić prawdę
            // o tym, co jest na ekranie, także wtedy, gdy na ekranie czegoś brakuje.
            return problem is not null ? problem : RecurrenceText.Describe(rule);
        }
    }

    /// <summary>
    /// Wybory z list, czytane odpornie na puste.
    /// </summary>
    /// <remarks>
    /// Pola wyboru **mogą** nie mieć nic wybranego — kontrolka potrafi wpisać pustkę
    /// przy składaniu i przy podmianie listy — a typ mówił, że nie mogą. Sięgnięcie
    /// po <c>.Kind</c> na pustce rzucało wyjątek przed pierwszą linijką, którą cokolwiek
    /// zapisuje: polecenie zapisu kończyło się **niczym**. Bez zapisu, bez komunikatu,
    /// bez wpisu w dzienniku, z otwartym oknem wyglądającym jak przed kliknięciem.
    /// </remarks>
    private RecurrenceKind? Rytm => SelectedRepeat?.Kind;

    private Priority Waga => SelectedPriority?.Value ?? Priority.None;

    private Energy Sila => SelectedEnergyLevel?.Value ?? Energy.Unknown;

    public bool IsRepeating => Rytm is not null;

    public bool NeedsInterval => Rytm
        is RecurrenceKind.EveryNDays or RecurrenceKind.Weekly
        or RecurrenceKind.Monthly or RecurrenceKind.Yearly;

    public bool NeedsWeekdays => Rytm == RecurrenceKind.Weekly;

    public bool NeedsDayOfMonth => Rytm == RecurrenceKind.Monthly;

    public event EventHandler? Saved;

    /// <summary>
    /// Drzewko miejsc: obszary z zagnieżdżonymi projektami.
    /// </summary>
    /// <remarks>
    /// Dociągane przy każdym otwarciu, bo obszary i projekty zmienia się na osobnym
    /// ekranie — zapamiętana lista zrobiłaby się nieprawdziwa dokładnie wtedy, gdy
    /// ktoś właśnie założył projekt i chce do niego coś wrzucić.
    /// </remarks>
    private async Task WczytajMiejscaAsync()
    {
        var drzewko = ProjectTree.Build(await areas.ActiveAsync(), await projects.ActiveAsync());

        Placements.Clear();
        foreach (var row in drzewko)
        {
            Placements.Add(PlacementChoice.From(row));
        }
    }

    /// <summary>
    /// Otwarcie szczegółu. Obszary dociągane przy każdym otwarciu, bo lista bywa
    /// zmieniana na osobnym ekranie i zapamiętana zrobiłaby się nieprawdziwa.
    /// </summary>
    public async Task LoadAsync(TaskItem task)
    {
        ArgumentNullException.ThrowIfNull(task);

        await WczytajMiejscaAsync();
        Load(task);
    }

    /// <summary>
    /// Nowe zadanie na wskazany dzień i godzinę — stąd otwiera je kliknięcie w pustą
    /// siatkę kalendarza.
    /// </summary>
    /// <remarks>
    /// Zadanie powstaje dopiero przy zapisie, nie przy otwarciu okna. Utworzone
    /// z góry zostawiałoby po zamknięciu bez zapisu pusty wpis w skrzynce — czyli
    /// karę za rozmyślenie się.
    /// </remarks>
    public async Task NewAsync(DateOnly day, TimeOnly time)
    {
        await WczytajMiejscaAsync();

        _loading = true;
        _id = Guid.Empty;
        Problem = null;
        OnPropertyChanged(nameof(HasProblem));

        Title = string.Empty;
        Note = string.Empty;
        IsDone = false;
        Deadline = null;
        ReminderDay = null;
        ReminderTime = null;
        SelectedPriority = Priorities[0];
        SelectedEnergyLevel = Energies[0];
        EstimatedMinutes = null;
        SelectedPlacement = Placements.FirstOrDefault();
        LoadRule(null);

        // Nowe zadanie z godziny na siatce ma domyślnie odezwać się o tej godzinie.
        // Wpisanie czegoś w kalendarz i niedowiedzenie się o tym jest najczęstszym
        // sposobem na przegapienie — a odznaczenie kosztuje jedno kliknięcie.
        WczytajWyprzedzenia([0]);

        DoDate = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), clock.Now.Offset);
        DoTime = time.ToTimeSpan();

        // Bez końca i bez długości: pół godziny to sposób **rysowania** bloku bez
        // oszacowania, a nie oszacowanie. Wpisane tu z góry zapisałoby się jako
        // decyzja, której nikt nie podjął.
        EndTime = null;

        _loading = false;
        ShowMore = false;
        ShowDeadline = false;
        ShowReminder = false;
        ShowPriority = false;
        ShowEnergy = false;
        ShowRepeat = false;
        Refresh();
        OnPropertyChanged(nameof(IsExisting));
        IsOpen = true;
    }

    public void Load(TaskItem task)
    {
        ArgumentNullException.ThrowIfNull(task);

        _loading = true;
        Problem = null;
        OnPropertyChanged(nameof(HasProblem));
        _id = task.Id;
        Title = task.Title;
        Note = task.Note ?? string.Empty;
        DoDate = ToOffset(task.DoDate);
        Deadline = ToOffset(task.Deadline);
        ReminderDay = task.ReminderAt is { } r ? ToOffset(DateOnly.FromDateTime(r.DateTime)) : null;
        ReminderTime = task.ReminderAt?.TimeOfDay;
        WczytajWyprzedzenia(task.ReminderLeads);
        IsDone = task.State == TaskState.Done;
        SelectedPriority = Priorities.First(p => p.Value == task.Priority);
        EstimatedMinutes = task.EstimatedMinutes;
        DoTime = task.DoTime?.ToTimeSpan();
        // Projekt ma pierwszeństwo przed samym obszarem: gdy zadanie należy do projektu,
        // wskazanie na obszar mówiłoby mniej, niż aplikacja wie — i zapis odpiąłby projekt.
        SelectedPlacement =
            Placements.FirstOrDefault(m => task.ProjectId is { } p && m.ProjectId == p)
            ?? Placements.FirstOrDefault(m => m.ProjectId is null && m.AreaId == task.AreaId);

        // Koniec z początku i długości — nie ma go w modelu, bo byłby drugą prawdą
        // o tej samej rzeczy.
        // Koniec **tylko** z prawdziwej długości. Doliczany z domyślnych trzydziestu
        // minut wyglądał jak wpisana wartość i przy pierwszym dotknięciu pola wracał
        // do bazy jako oszacowanie, którego nikt nie podał — zadanie na piętnaście
        // minut robiło się trzydziestominutowe samo z siebie. Aplikacja nie ma prawa
        // zmyślać długości: brak oszacowania to brak, a nie „pewnie pół godziny".
        EndTime = task.DoTime is { } start && task.EstimatedMinutes is { } length
            ? start.ToTimeSpan() + TimeSpan.FromMinutes(length)
            : null;
        SelectedEnergyLevel = Energies.First(e => e.Value == task.Energy);
        LoadRule(task.Recurrence);

        _loading = false;
        OnPropertyChanged(nameof(IsExisting));
        ShowMore = Deadline is not null
            || ReminderDay is not null
            || Leads.Any(w => w.IsChecked)
            || Rytm is not null
            || Waga != Priority.None;

        Refresh();
        IsOpen = true;
    }

    private void LoadRule(RecurrenceRule? rule)
    {
        SelectedRepeat = Repeats.First(r => r.Kind == rule?.Kind);
        Interval = rule?.Interval ?? 1;
        DayOfMonth = rule?.DayOfMonth;
        SelectedAnchor = Anchors.First(a => a.Value == (rule?.Anchor ?? RecurrenceAnchor.FromScheduled));

        // Accumulate nie ma pozycji na liście (spec 13.2 pkt 1), więc zadanie
        // przyniesione z drugiego urządzenia pokazuje się jako Carry. Zapisanie
        // szczegółu zmieni je na Carry — i to jest zamierzone, bo tak brzmi to,
        // co widać na ekranie.
        SelectedMissed = Missed.FirstOrDefault(m => m.Value == rule?.OnMissed) ?? Missed[0];

        var days = rule?.DaysOfWeek ?? Weekdays.None;
        Monday = days.Includes(DayOfWeek.Monday);
        Tuesday = days.Includes(DayOfWeek.Tuesday);
        Wednesday = days.Includes(DayOfWeek.Wednesday);
        Thursday = days.Includes(DayOfWeek.Thursday);
        Friday = days.Includes(DayOfWeek.Friday);
        Saturday = days.Includes(DayOfWeek.Saturday);
        Sunday = days.Includes(DayOfWeek.Sunday);
    }

    public void Close()
    {
        _ = log.RecordAsync("Zadanie: zamknięcie okna bez zapisu", Title);
        IsOpen = false;
    }

    /// <summary>
    /// Zapis szczegółu.
    /// </summary>
    /// <remarks>
    /// <b>Publiczna i wołana wprost z okna</b>, a nie przez polecenie. Kliknięcie
    /// w „Zapisz" nie robiło nic i nie zostawiało śladu nawet w pierwszej linijce tej
    /// metody — czyli warstwa poleceń nie doprowadzała do niej wcale. Polecenie
    /// asynchroniczne ma własny warunek wykonalności i własne pilnowanie
    /// jednoczesności; jedno i drugie potrafi cicho odmówić, a odmowa wygląda
    /// dokładnie jak martwy przycisk. Droga bez pośrednika jest krótsza o wszystko,
    /// czego tu nie potrzebujemy.
    /// </remarks>
    public async Task SaveAsync()
    {
        // Ślad **przed** wszystkim innym. Do dziś pierwszą rzeczą w tym poleceniu było
        // budowanie reguły rytmu, i to poza blokiem chroniącym: wyjątek stamtąd nie
        // miał dokąd trafić, bo polecenie wołane jest bez oczekiwania na wynik.
        await log.RecordAsync("Zadanie: polecenie zapisu", Title);

        string? problem;
        RecurrenceRule? rule;

        try
        {
            rule = BuildRule(out problem);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Problem = e.Message;
            OnPropertyChanged(nameof(HasProblem));

            await log.RecordAsync(
                "Zadanie: zapis", Title, ActivityLevel.Problem,
                $"budowanie rytmu: {e.GetType().Name}: {e.Message}");

            return;
        }

        if (problem is not null)
        {
            // Do dziś była tu cicha odmowa: przycisk klikał, okno zostawało otwarte
            // i nic nie mówiło dlaczego. Zdanie podsumowujące rytm bywa niżej,
            // poza widokiem, więc powód idzie także tutaj.
            Problem = problem;
            OnPropertyChanged(nameof(HasProblem));
            await log.RecordAsync("Zadanie: zapis", Title, ActivityLevel.Problem, problem);
            return;
        }

        try
        {
            if (_id == Guid.Empty)
            {
                if (string.IsNullOrWhiteSpace(Title))
                {
                    Problem = "Nowe zadanie potrzebuje nazwy.";
                    OnPropertyChanged(nameof(HasProblem));

                    await log.RecordAsync(
                        "Zadanie: zapis", "bez nazwy", ActivityLevel.Problem,
                        "Nowe zadanie zapisane bez nazwy nie powstaje.");

                    return;
                }

                _id = await inbox.CaptureAsync(Title);
            }

            await edit.ApplyAsync(_id, new TaskEdit(
                Title,
                string.IsNullOrWhiteSpace(Note) ? null : Note,
                ToDate(DoDate),
                ToDate(Deadline),
                ReminderAt(),
                rule,
                Waga,
                Minuty(),
                Sila,
                SelectedPlacement?.AreaId,
                DoTime is { } time ? TimeOnly.FromTimeSpan(time) : null,
                WybraneWyprzedzenia()));

            // Projekt osobnym wywołaniem, a nie kolejnym polem edycji: pole typu
            // Guid? nie umie odróżnić „zostaw jak jest" od „wyjmij z projektu",
            // a drzewko zawsze wyraża pełną decyzję o obu.
            if (SelectedPlacement is { } miejsce)
            {
                await edit.SetProjectAsync(_id, miejsce.ProjectId);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // **Każdy** wyjątek, nie wybrane rodzaje. Polecenie wołane jest bez
            // oczekiwania na wynik, więc rodzaj spoza listy nie miał dokąd trafić:
            // znikał bez śladu, a okno zostawało otwarte bez słowa wyjaśnienia.
            Problem = e.Message;
            OnPropertyChanged(nameof(HasProblem));

            await log.RecordAsync(
                "Zadanie: zapis", Title, ActivityLevel.Problem,
                $"{e.GetType().Name}: {e.Message}");

            return;
        }

        // Ślad także po udanym zapisie: „zapisało się, ale nie widać" i „nie zapisało
        // się" wyglądają na ekranie tak samo, a to dwie różne rzeczy do zrobienia.
        await log.RecordAsync(
            "Zadanie: zapis",
            $"{Title} — dzień {ToDate(DoDate)?.ToString("yyyy-MM-dd") ?? "brak"}, "
                + $"godzina {(DoTime is { } g ? g.ToString(@"hh\:mm") : "brak")}, "
                + $"miejsce {SelectedPlacement?.Label ?? "brak"}");

        IsOpen = false;
        Saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Długość zadania w minutach.
    /// </summary>
    /// <remarks>
    /// Godzina zakończenia jest ważniejsza od ręcznego oszacowania, bo została wpisana
    /// przy tym samym dniu i tej samej godzinie — a oszacowanie mogło zostać z innego
    /// planu. Koniec przed początkiem znaczy przejście przez północ.
    /// </remarks>
    private int? Minuty() => EstimatedMinutes is { } minutes ? (int)minutes : null;

    /// <summary>
    /// Długość i godzina zakończenia trzymane zgodnie.
    /// </summary>
    /// <remarks>
    /// To jest jedna wartość pokazana na dwa sposoby, więc zmiana każdego z nich
    /// przelicza drugi. Do dziś koniec wygrywał przy zapisie — a koniec wpisuje się
    /// sam, jako pół godziny od początku. Wpisanie „15 minut" w polu długości znikało
    /// więc bez śladu, zastąpione trzydziestoma z pola, którego nikt nie dotykał.
    /// </remarks>
    partial void OnEndTimeChanged(TimeSpan? value)
    {
        if (_loading || _zgodne || DoTime is not { } start || value is not { } end)
        {
            return;
        }

        var length = end > start
            ? end - start
            : end + TimeSpan.FromDays(1) - start;

        _zgodne = true;
        EstimatedMinutes = Math.Max(1, (int)length.TotalMinutes);
        _zgodne = false;
    }

    partial void OnEstimatedMinutesChanged(decimal? value)
    {
        if (_loading || _zgodne || DoTime is not { } start || value is not { } minutes)
        {
            return;
        }

        _zgodne = true;
        EndTime = start + TimeSpan.FromMinutes((double)Math.Max(1, minutes));
        _zgodne = false;
    }

    partial void OnDoTimeChanged(TimeSpan? value)
    {
        // Poza blokadami, bo lista wyprzedzeń pojawia się i znika razem z godziną,
        // także przy wczytywaniu.
        OnPropertyChanged(nameof(HasTime));

        // Godzina wpisana zadaniu, które żadnego przypomnienia nie ma, włącza to
        // o czasie. Zaplanowanie czegoś na konkretną porę i niedowiedzenie się o niej
        // jest najczęstszym sposobem na przegapienie.
        if (!_loading && value is not null && !Leads.Any(w => w.IsChecked))
        {
            Append(0).IsChecked = true;
            Refresh();
        }

        // Zabrana godzina gasi wyprzedzenia od razu na ekranie, a nie dopiero w bazie
        // przy zapisie. Kwadraciki schowane, ale wciąż zaznaczone, pokazywałyby przy
        // ponownym wpisaniu godziny stan, którego zadanie już nie ma.
        if (!_loading && value is null)
        {
            foreach (var wyprzedzenie in Leads)
            {
                wyprzedzenie.IsChecked = false;
            }

            Refresh();
        }

        if (_loading || _zgodne || value is not { } start)
        {
            return;
        }

        // Przy pustej długości koniec zostaje pusty — patrz wyżej.
        if (EstimatedMinutes is not { } minutes)
        {
            return;
        }

        _zgodne = true;
        EndTime = start + TimeSpan.FromMinutes((double)minutes);
        _zgodne = false;
    }

    /// <summary>Blokada wzajemnego przeliczania, żeby nie goniło się w kółko.</summary>
    private bool _zgodne;

    /// <summary>
    /// Odhaczenie i zdjęcie ptaszka z okna szczegółu. Zostawia ślad, bo zamyka okno
    /// tak samo jak zapis.
    /// </summary>
    /// <remarks>
    /// W obie strony, bo kwadracik pokazuje teraz stan zadania. Kwadracik, który daje
    /// się tylko zaznaczyć, wygląda na zepsuty — i zostawia omyłkowe odhaczenie bez
    /// drogi odwrotu.
    /// </remarks>
    public async Task CompleteAsync()
    {
        await log.RecordAsync(
            IsDone ? "Zadanie: zdjęcie ptaszka z okna" : "Zadanie: odhaczenie z okna", Title);

        if (_id == Guid.Empty)
        {
            Problem = "Nowe zadanie nie da się odhaczyć, zanim powstanie.";
            OnPropertyChanged(nameof(HasProblem));
            return;
        }

        if (IsDone)
        {
            await edit.ReopenAsync(_id);
        }
        else
        {
            await edit.CompleteAsync(_id);
        }

        IsOpen = false;
        Saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Zadanie do kosza, z okna szczegółu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Na komputerze kasowało się z listy, prawym przyciskiem. Na telefonie nie ma
    /// prawego przycisku ani listy pod ręką — szczegół jest tam całym ekranem — więc
    /// zadania otwartego z kalendarza <b>nie dało się usunąć w ogóle</b>. Jedyną drogą
    /// było wrócić na komputer.
    /// </para>
    /// <para>
    /// <b>Do kosza, nie „usuń".</b> Nazwa mówi prawdę o tym, co się dzieje: zadanie
    /// dostaje nagrobek i zostaje w archiwum, a nie znika z bazy. Dlatego też nie ma
    /// tu pytania „czy na pewno" — pytanie o potwierdzenie przy czynności odwracalnej
    /// uczy odklikiwać pytania, a to psuje te, przy których potwierdzenie ma sens.
    /// </para>
    /// <para>
    /// Udostępnione zabiera ze sobą swoje odbicie w kalendarzu — tym zajmuje się
    /// usługa skrzynki. Zostawione byłoby zaproszeniem na coś, czego już nie ma.
    /// </para>
    /// </remarks>
    public async Task TrashAsync()
    {
        await log.RecordAsync("Zadanie: do kosza z okna", Title);

        if (_id == Guid.Empty)
        {
            // Nowe zadanie nie ma czego wyrzucać; zamknięcie okna robi dokładnie to samo.
            Close();
            return;
        }

        await inbox.TrashAsync(_id);

        IsOpen = false;
        Saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Chwila przypomnienia z dnia i pory. Sam dzień bez pory znaczy rano — ale
    /// **sama pora bez dnia nie znaczy nic**, bo nie wiadomo którego. Wtedy przypomnienia
    /// nie ma, zamiast zgadywać dzisiaj i odezwać się natychmiast.
    /// </summary>
    private DateTimeOffset? ReminderAt()
    {
        if (ReminderDay is not { } day)
        {
            return null;
        }

        var time = ReminderTime ?? new TimeSpan(8, 0, 0);
        return new DateTimeOffset(day.Date.Add(time), clock.Now.Offset);
    }

    /// <summary>
    /// Reguła z tego, co na ekranie, albo <c>null</c> z powodem. Ten sam kod służy
    /// do zapisu i do zdania podsumowującego, więc podsumowanie nie może pokazywać
    /// czegoś innego niż to, co się zapisze.
    /// </summary>
    private RecurrenceRule? BuildRule(out string? problem)
    {
        problem = null;

        if (Rytm is not { } kind)
        {
            return null;
        }

        var days = Weekdays.None;
        if (Monday) { days |= Weekdays.Monday; }
        if (Tuesday) { days |= Weekdays.Tuesday; }
        if (Wednesday) { days |= Weekdays.Wednesday; }
        if (Thursday) { days |= Weekdays.Thursday; }
        if (Friday) { days |= Weekdays.Friday; }
        if (Saturday) { days |= Weekdays.Saturday; }
        if (Sunday) { days |= Weekdays.Sunday; }

        try
        {
            return new RecurrenceRule(
                kind,
                (int)Math.Max(1, Interval),
                days,
                kind == RecurrenceKind.Monthly && DayOfMonth is { } day ? (int)day : null,
                SelectedAnchor?.Value ?? RecurrenceRule.DefaultAnchorFor(kind),
                SelectedMissed?.Value ?? OnMissed.Carry);
        }
        catch (ArgumentException)
        {
            // Jedyny przypadek, jaki może tu wystąpić: rytm tygodniowy bez wskazanego
            // dnia. Komunikat z wyjątku niesie nazwę parametru, więc nie nadaje się
            // na ekran.
            problem = "co tydzień — ale w które dni?";
            return null;
        }
    }

    private static DateTimeOffset? ToOffset(DateOnly? date) =>
        date is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;

    private static DateOnly? ToDate(DateTimeOffset? value) =>
        value is { } v ? DateOnly.FromDateTime(v.Date) : null;

    /// <summary>
    /// Lista wyprzedzeń od nowa: gotowy zestaw plus to, co zadanie ma zapisane,
    /// w kolejności czasu. Zaznaczone jest wyłącznie to, co zadanie naprawdę ma.
    /// </summary>
    private void WczytajWyprzedzenia(IReadOnlyList<int> selected)
    {
        Leads.Clear();

        foreach (var minutes in Gotowe.Concat(selected).Distinct().OrderBy(m => m))
        {
            Leads.Add(new LeadChoice(minutes) { IsChecked = selected.Contains(minutes) });
        }
    }

    /// <summary>Wyprzedzenie na liście — to, które już tam jest, albo świeżo wstawione.</summary>
    private LeadChoice Append(int minutes)
    {
        if (Leads.FirstOrDefault(w => w.Minutes == minutes) is { } juz)
        {
            return juz;
        }

        var item = new LeadChoice(minutes);
        Leads.Insert(Leads.Count(w => w.Minutes < minutes), item);
        return item;
    }

    /// <summary>
    /// Dopisanie własnego wyprzedzenia: liczba razy jednostka. Od razu zaznaczone —
    /// nikt nie wpisuje „dwie godziny" po to, żeby zostawić to niewłączone.
    /// </summary>
    [RelayCommand]
    private void AddLead()
    {
        if (CustomLead is not { } count || SelectedLeadUnit is not { } jednostka)
        {
            return;
        }

        var minutes = (int)Math.Round(count) * jednostka.Minutes;

        if (minutes < 0)
        {
            return;
        }

        Append(minutes).IsChecked = true;
        Refresh();
    }

    /// <summary>
    /// Wyprzedzenia do zapisu. Zadanie bez godziny zwraca pustą listę, czyli „żadnych":
    /// wyprzedzenie liczy się od godziny, więc zabranie jej zabiera to, od czego liczyło.
    /// Przypomnienie, które zostałoby przy zadaniu bez pory, nie miałoby kiedy się odezwać.
    /// </summary>
    private IReadOnlyList<int> WybraneWyprzedzenia() =>
        HasTime ? Leads.Where(w => w.IsChecked).Select(w => w.Minutes).ToList() : [];

    private void Refresh()
    {
        OnPropertyChanged(nameof(MoreSummary));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasTime));
        OnPropertyChanged(nameof(IsRepeating));
        OnPropertyChanged(nameof(NeedsInterval));
        OnPropertyChanged(nameof(NeedsWeekdays));
        OnPropertyChanged(nameof(NeedsDayOfMonth));
    }

    partial void OnSelectedRepeatChanged(RepeatChoice? value)
    {
        if (_loading)
        {
            return;
        }

        // Domyślne zaczepienie wynika z rodzaju (spec 5.7), więc zmiana rodzaju ma je
        // przestawić. Zostawione ręcznie ustawione dałoby „co poniedziałek, licząc od
        // wykonania" jako stan domyślny — czyli rytm dryfujący na środy.
        if (value?.Kind is { } kind)
        {
            SelectedAnchor = Anchors.First(a => a.Value == RecurrenceRule.DefaultAnchorFor(kind));
        }

        Refresh();
    }

    partial void OnIntervalChanged(decimal value) => Refresh();

    partial void OnDayOfMonthChanged(decimal? value) => Refresh();

    partial void OnSelectedAnchorChanged(AnchorChoice? value) => Refresh();

    partial void OnSelectedMissedChanged(MissedChoice? value) => Refresh();

    partial void OnMondayChanged(bool value) => Refresh();

    partial void OnTuesdayChanged(bool value) => Refresh();

    partial void OnWednesdayChanged(bool value) => Refresh();

    partial void OnThursdayChanged(bool value) => Refresh();

    partial void OnFridayChanged(bool value) => Refresh();

    partial void OnSaturdayChanged(bool value) => Refresh();

    partial void OnSundayChanged(bool value) => Refresh();
}
