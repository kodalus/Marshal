using FluentAssertions;
using Marshal.Domain.Primitives;
using Marshal.Domain.Recurrence;
using Marshal.Domain.Tasks;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Odhaczanie wystąpień i przejście dnia (spec 8.4, 8.7).
/// </summary>
public sealed class RecurrenceRunnerTests
{
    private readonly Guid _area = Guid.CreateVersion7();
    private Hlc _clock = Hlc.Zero("test");

    private Hlc Stempel() => _clock = Hlc.Next(_clock, _clock.WallMs + 1);

    private static DateOnly D(string iso) => DateOnly.Parse(iso);

    private static DateTimeOffset Moment(string iso, int hour = 9) =>
        new(DateOnly.Parse(iso).ToDateTime(new TimeOnly(hour, 0)), TimeSpan.FromHours(2));

    private TaskItem Zaplanowane(string doDate, RecurrenceRule? rule = null)
    {
        var task = TaskItem.Capture("Wynieść śmieci", Moment(doDate), Stempel());
        task.Schedule(_area, D(doDate), Stempel());

        if (rule is not null)
        {
            task.SetRecurrence(rule, Stempel());
        }

        return task;
    }

    // --- odhaczenie (8.4) ----------------------------------------------------

    [Fact]
    public void Odhaczenie_zwyklego_zadania_nie_rodzi_nastepnika()
    {
        var task = Zaplanowane("2026-09-16");

        RecurrenceRunner.Complete(task, Moment("2026-09-16"), Stempel).Should().BeNull();
        task.State.Should().Be(TaskState.Done);
    }

    [Fact]
    public void Odhaczenie_z_zaczepieniem_na_planie_nie_przesuwa_rytmu()
    {
        // „Co poniedziałek śmieci" odhaczone we wtorek: kolejny poniedziałek zostaje
        // poniedziałkiem. To jest cała różnica między rytmem świata a rytmem moich rąk.
        var task = Zaplanowane(
            "2026-09-14", new RecurrenceRule(RecurrenceKind.Weekly, daysOfWeek: Weekdays.Monday));

        var next = RecurrenceRunner.Complete(task, Moment("2026-09-15"), Stempel);

        next!.DoDate.Should().Be(D("2026-09-21"));
    }

    [Fact]
    public void Odhaczenie_z_zaczepieniem_na_wykonaniu_przesuwa_caly_rytm()
    {
        // „Co 3 dni podlewanie" odhaczone z dwudniowym opóźnieniem: kolejne trzy dni
        // liczą się od podlania, a nie od tego, co było obiecane.
        var task = Zaplanowane(
            "2026-09-14", new RecurrenceRule(RecurrenceKind.EveryNDays, interval: 3));

        var next = RecurrenceRunner.Complete(task, Moment("2026-09-16"), Stempel);

        next!.DoDate.Should().Be(D("2026-09-19"));
    }

    [Fact]
    public void Odhaczone_wystapienie_zostaje_odhaczone_a_nie_wedruje_w_przyszlosc()
    {
        // Historia „robiłam to w każdy poniedziałek prócz jednego" jest całą wartością
        // powtarzalności; zadanie przestawiane w przyszłość jej nie niesie.
        var task = Zaplanowane(
            "2026-09-14", new RecurrenceRule(RecurrenceKind.Weekly, daysOfWeek: Weekdays.Monday));

        var next = RecurrenceRunner.Complete(task, Moment("2026-09-14"), Stempel);

        task.State.Should().Be(TaskState.Done);
        task.DoDate.Should().Be(D("2026-09-14"));
        next!.Id.Should().NotBe(task.Id);
        next.State.Should().Be(TaskState.Scheduled);
    }

    [Fact]
    public void Nastepne_wystapienie_dziedziczy_obszar_projekt_i_wage()
    {
        var task = Zaplanowane("2026-09-14", new RecurrenceRule(RecurrenceKind.Daily));
        var project = Guid.CreateVersion7();
        task.MoveTo(_area, project, Stempel());
        task.SetPriority(Priority.High, Stempel());
        task.SetNote("z kluczem do piwnicy", Stempel());

        var next = RecurrenceRunner.Complete(task, Moment("2026-09-14"), Stempel)!;

        next.AreaId.Should().Be(_area);
        next.ProjectId.Should().Be(project);
        next.Priority.Should().Be(Priority.High);
        next.Note.Should().Be("z kluczem do piwnicy");
        next.Title.Should().Be("Wynieść śmieci");
    }

    [Fact]
    public void Termin_przenosi_sie_z_zachowaniem_odstepu()
    {
        // „Zapłacić do 10-go" przy racie robionej 5-go to pięć dni zapasu, co miesiąc
        // tyle samo. Skopiowany wprost byłby od razu przeterminowany i N5 zacząłby kłamać.
        var task = Zaplanowane(
            "2026-09-05", new RecurrenceRule(RecurrenceKind.Monthly, dayOfMonth: 5));
        task.SetDeadline(D("2026-09-10"), Stempel());

        var next = RecurrenceRunner.Complete(task, Moment("2026-09-05"), Stempel)!;

        next.DoDate.Should().Be(D("2026-10-05"));
        next.Deadline.Should().Be(D("2026-10-10"));
    }

    [Fact]
    public void Regule_nosi_zawsze_najnowsze_wystapienie()
    {
        var task = Zaplanowane("2026-09-14", new RecurrenceRule(RecurrenceKind.Daily));

        var next = RecurrenceRunner.Complete(task, Moment("2026-09-14"), Stempel)!;

        task.Recurrence.Should().BeNull();
        next.Recurrence.Should().NotBeNull();
    }

    [Fact]
    public void Odhaczenie_dwa_razy_tego_samego_dnia_nie_robi_dwoch_nastepnikow()
    {
        var task = Zaplanowane("2026-09-14", new RecurrenceRule(RecurrenceKind.Daily));

        RecurrenceRunner.Complete(task, Moment("2026-09-14"), Stempel).Should().NotBeNull();
        RecurrenceRunner.Complete(task, Moment("2026-09-14"), Stempel).Should().BeNull();
    }

    [Fact]
    public void Odhaczenie_zadania_z_przyszlosci_liczy_od_jego_daty()
    {
        // Zrobione z wyprzedzeniem. Przy zaczepieniu na planie rytm zostaje nietknięty.
        var task = Zaplanowane(
            "2026-09-21", new RecurrenceRule(RecurrenceKind.Weekly, daysOfWeek: Weekdays.Monday));

        var next = RecurrenceRunner.Complete(task, Moment("2026-09-16"), Stempel)!;

        next.DoDate.Should().Be(D("2026-09-28"));
    }

    [Fact]
    public void Wyczerpana_seria_nie_rodzi_nastepnika()
    {
        var task = Zaplanowane("2026-09-14", new RecurrenceRule(RecurrenceKind.Daily, count: 1));

        RecurrenceRunner.Complete(task, Moment("2026-09-14"), Stempel).Should().BeNull();
    }

    // --- przejście dnia (8.4 tabela, 8.7) ------------------------------------

    [Fact]
    public void Zwykle_zaplanowane_przesuwa_sie_na_dzis_z_licznikiem()
    {
        var task = Zaplanowane("2026-09-10");

        RecurrenceRunner.Rollover(task, Moment("2026-09-16"), Stempel).Should().BeNull();

        task.DoDate.Should().Be(D("2026-09-16"));
        task.RollCount.Should().Be(1);
        task.State.Should().Be(TaskState.Scheduled);
    }

    [Fact]
    public void Przesuniecie_nie_rusza_terminu()
    {
        // Termin to fakt zewnętrzny — świat się nie przesunął, więc minięcie zostaje
        // przeterminowaniem (N5). Data wykonania to obietnica dana sobie, i to ona idzie.
        var task = Zaplanowane("2026-09-10");
        task.SetDeadline(D("2026-09-12"), Stempel());

        RecurrenceRunner.Rollover(task, Moment("2026-09-16"), Stempel);

        task.Deadline.Should().Be(D("2026-09-12"));
    }

    [Fact]
    public void Przejscie_dnia_puszczone_dwa_razy_liczy_raz()
    {
        var task = Zaplanowane("2026-09-10");

        RecurrenceRunner.Rollover(task, Moment("2026-09-16"), Stempel);
        RecurrenceRunner.Rollover(task, Moment("2026-09-16"), Stempel);

        task.RollCount.Should().Be(1);
    }

    [Fact]
    public void Zadanie_na_dzis_nie_jest_ruszane()
    {
        var task = Zaplanowane("2026-09-16");

        RecurrenceRunner.Rollover(task, Moment("2026-09-16"), Stempel).Should().BeNull();

        task.RollCount.Should().Be(0);
    }

    [Fact]
    public void Wykonane_zadanie_z_przeszlosci_nie_jest_ruszane()
    {
        var task = Zaplanowane("2026-09-10");
        task.Complete(Moment("2026-09-10"), Stempel());

        RecurrenceRunner.Rollover(task, Moment("2026-09-16"), Stempel).Should().BeNull();

        task.DoDate.Should().Be(D("2026-09-10"));
    }

    [Fact]
    public void Pominiete_z_Skip_trafia_do_kosza_i_rodzi_nastepne_na_dzis()
    {
        // Nie na 15-go: wystąpienia między datą pominiętą a dniem dzisiejszym też
        // przepadają, bo na tym polega Skip. Tworzenie ich po to, żeby zaraz wyrzucić,
        // dałoby tylko nagrobki do rozesłania.
        var task = Zaplanowane(
            "2026-09-14",
            new RecurrenceRule(RecurrenceKind.Daily, onMissed: OnMissed.Skip));

        var next = RecurrenceRunner.Rollover(task, Moment("2026-09-16"), Stempel);

        task.State.Should().Be(TaskState.Trashed);
        next!.DoDate.Should().Be(D("2026-09-16"));
    }

    [Fact]
    public void Pominiete_z_Carry_przenosi_sie_na_dzis_i_nie_rodzi_nastepnego()
    {
        var task = Zaplanowane("2026-09-14", new RecurrenceRule(RecurrenceKind.Daily));

        var next = RecurrenceRunner.Rollover(task, Moment("2026-09-16"), Stempel);

        next.Should().BeNull();
        task.DoDate.Should().Be(D("2026-09-16"));
        task.CarriedSince.Should().Be(D("2026-09-14"));
        task.Recurrence.Should().NotBeNull();
    }

    [Fact]
    public void Carry_przez_trzy_tygodnie_z_rzedu_pamieta_pierwszy_dzien()
    {
        // „Zaległe od 14 września", nie „od wczoraj". Bez tego N12 nigdy nie doliczyłby
        // trzydziestu dni i strażnik rytmu nie odezwałby się nigdy.
        var task = Zaplanowane("2026-09-14", new RecurrenceRule(RecurrenceKind.Daily));

        foreach (var day in new[] { "2026-09-15", "2026-09-22", "2026-10-05" })
        {
            RecurrenceRunner.Rollover(task, Moment(day), Stempel);
        }

        task.CarriedSince.Should().Be(D("2026-09-14"));
        task.DoDate.Should().Be(D("2026-10-05"));
    }

    [Fact]
    public void Pominiete_z_Accumulate_zostaje_zalegloscia_a_rytm_idzie_dalej()
    {
        // Rozstrzygnięcie sprzeczności 8.4 z 8.7: gdyby zaległe wystąpienie zostało
        // zaplanowane na swoją dawną datę, nazajutrz przesunęłoby się na dziś, potem
        // znowu, i po czterech dniach odpaliłoby N15. Zostaje więc następną akcją
        // bez dnia, a dzień, na który było umówione, siedzi w „zaległe od".
        var task = Zaplanowane(
            "2026-09-14",
            new RecurrenceRule(RecurrenceKind.Daily, onMissed: OnMissed.Accumulate));

        var next = RecurrenceRunner.Rollover(task, Moment("2026-09-16"), Stempel);

        task.State.Should().Be(TaskState.Next);
        task.DoDate.Should().BeNull();
        task.CarriedSince.Should().Be(D("2026-09-14"));
        task.Recurrence.Should().BeNull();
        next!.DoDate.Should().Be(D("2026-09-15"));
    }

    [Fact]
    public void Zalegle_wystapienie_z_Accumulate_nie_jest_juz_przesuwane()
    {
        var task = Zaplanowane(
            "2026-09-14",
            new RecurrenceRule(RecurrenceKind.Daily, onMissed: OnMissed.Accumulate));

        RecurrenceRunner.Rollover(task, Moment("2026-09-16"), Stempel);
        RecurrenceRunner.Rollover(task, Moment("2026-09-20"), Stempel);

        task.RollCount.Should().Be(0);
        task.CarriedSince.Should().Be(D("2026-09-14"));
    }

    [Fact]
    public void Skip_po_tygodniu_nieobecnosci_daje_jedno_wystapienie_a_nie_siedem()
    {
        // Nagrobek każdego przeskoczonego dnia nie jest niczyją informacją, a rozjechałby
        // się po wszystkich urządzeniach.
        var task = Zaplanowane(
            "2026-09-09",
            new RecurrenceRule(RecurrenceKind.Daily, onMissed: OnMissed.Skip));

        var next = RecurrenceRunner.Rollover(task, Moment("2026-09-16"), Stempel);

        next!.DoDate.Should().Be(D("2026-09-16"));
        RecurrenceRunner.Rollover(next, Moment("2026-09-16"), Stempel).Should().BeNull();
    }

    [Fact]
    public void Skip_zuzywa_licznik_takze_za_dni_przeskoczone()
    {
        // Seria „pięć razy" przespana przez pięć dni jest serią skończoną, a nie serią,
        // która czeka na kolejne pięć okazji.
        var task = Zaplanowane(
            "2026-09-09",
            new RecurrenceRule(RecurrenceKind.Daily, onMissed: OnMissed.Skip, count: 3));

        RecurrenceRunner.Rollover(task, Moment("2026-09-16"), Stempel).Should().BeNull();
    }

    [Fact]
    public void Accumulate_puszczony_dwa_razy_nie_robi_dwoch_kopii()
    {
        // Najważniejszy test przejścia dnia: bez zabrania reguły poprzednikowi każde
        // uruchomienie dokładałoby po jednej pozycji, a aplikacja startuje wiele razy
        // dziennie i na dwóch urządzeniach.
        var task = Zaplanowane(
            "2026-09-14",
            new RecurrenceRule(RecurrenceKind.Daily, onMissed: OnMissed.Accumulate));

        RecurrenceRunner.Rollover(task, Moment("2026-09-16"), Stempel).Should().NotBeNull();
        RecurrenceRunner.Rollover(task, Moment("2026-09-16"), Stempel).Should().BeNull();
    }

    // --- zmiany pojedynczych wystąpień ---------------------------------------

    [Fact]
    public void Odhaczenie_zaklada_nastepne_tam_gdzie_je_przelozono()
    {
        // Wystąpienie przełożone na siatce, zanim powstało. Odhaczenie poprzedniego ma
        // uszanować tamtą decyzję — inaczej narysowana zapowiedź kłamałaby wobec tego,
        // co naprawdę powstaje.
        var task = Zaplanowane(
            "2026-09-16",
            new RecurrenceRule(
                RecurrenceKind.Weekly,
                daysOfWeek: Weekdays.Wednesday,
                changes:
                [
                    new RecurrenceChange(
                        D("2026-09-23"), Day: D("2026-09-24"), Time: new TimeOnly(17, 0)),
                ]));

        task.SetDoTime(new TimeOnly(19, 0), Stempel());

        var next = RecurrenceRunner.Complete(task, Moment("2026-09-16"), Stempel);

        next!.DoDate.Should().Be(D("2026-09-24"));
        next.DoTime.Should().Be(new TimeOnly(17, 0), "pora zmiany dotyczy tego jednego razu");
    }

    [Fact]
    public void Odhaczenie_przeskakuje_odwolane_wystapienie()
    {
        var task = Zaplanowane(
            "2026-09-16",
            new RecurrenceRule(
                RecurrenceKind.Weekly,
                daysOfWeek: Weekdays.Wednesday,
                changes: [new RecurrenceChange(D("2026-09-23"), Dropped: true)]));

        RecurrenceRunner.Complete(task, Moment("2026-09-16"), Stempel)!
            .DoDate.Should().Be(D("2026-09-30"));
    }

    [Fact]
    public void Nastepnik_nie_niesie_zmian_z_dni_minionych()
    {
        var task = Zaplanowane(
            "2026-09-16",
            new RecurrenceRule(
                RecurrenceKind.Daily,
                changes:
                [
                    new RecurrenceChange(D("2026-09-17"), Day: D("2026-09-18")),
                    new RecurrenceChange(D("2026-09-20"), Dropped: true),
                ]));

        var next = RecurrenceRunner.Complete(task, Moment("2026-09-16"), Stempel);

        // Zastosowana zmiana odpada razem ze swoim dniem; zostaje ta, która jeszcze przed.
        next!.Recurrence!.Changes
            .Select(z => z.Date)
            .Should().Equal(D("2026-09-20"));
    }

    [Fact]
    public void Nastepnik_bierze_dlugosc_z_rytmu_a_nie_z_wystapienia_ktore_odchodzi()
    {
        // Dzisiejsze wystąpienie rozciągnięte na siatce do sześciu godzin, a rytm pamięta
        // osiem. Gdyby długość przechodziła z wystąpienia, jedno dłuższe popołudnie
        // zmieniałoby rytm na zawsze.
        var task = Zaplanowane(
            "2026-09-16",
            new RecurrenceRule(RecurrenceKind.Daily, minutes: 480));

        task.SetEstimate(360, task.Energy, Stempel());

        RecurrenceRunner.Complete(task, Moment("2026-09-16"), Stempel)!
            .EstimatedMinutes.Should().Be(480);
    }

    [Fact]
    public void Nastepnik_bierze_pore_z_rytmu_a_nie_z_wystapienia_ktore_odchodzi()
    {
        // Dzisiejsze wystąpienie przeciągnięte na siatce na dziesiątą, a rytm pamięta
        // dziewiątą. Gdyby pora przechodziła z wystąpienia, jedno spóźnione popołudnie
        // przestawiałoby rytm na zawsze.
        var task = Zaplanowane(
            "2026-09-16",
            new RecurrenceRule(RecurrenceKind.Daily, time: new TimeOnly(9, 0)));

        task.SetDoTime(new TimeOnly(10, 0), Stempel());

        RecurrenceRunner.Complete(task, Moment("2026-09-16"), Stempel)!
            .DoTime.Should().Be(new TimeOnly(9, 0));
    }

    [Fact]
    public void Dlugosc_zapisana_przy_wystapieniu_wygrywa_nad_dlugoscia_rytmu()
    {
        var task = Zaplanowane(
            "2026-09-16",
            new RecurrenceRule(
                RecurrenceKind.Daily,
                changes: [new RecurrenceChange(D("2026-09-17"), Minutes: 120)],
                minutes: 480));

        var next = RecurrenceRunner.Complete(task, Moment("2026-09-16"), Stempel);

        next!.DoDate.Should().Be(D("2026-09-17"));
        next.EstimatedMinutes.Should().Be(120, "zmiana dotyczy tego jednego razu");
    }

    [Fact]
    public void Wyprzedzenia_bierze_sie_z_rytmu_a_nie_z_wystapienia()
    {
        // Przypomnienie jest cechą rytmu: seria mówi „pół godziny wcześniej" i to ona
        // rozstrzyga, a nie to, co akurat niesie wystąpienie schodzące ze sceny.
        var task = Zaplanowane(
            "2026-09-16",
            new RecurrenceRule(RecurrenceKind.Daily, leads: [30]));

        task.SetDoTime(new TimeOnly(19, 0), Stempel());
        task.SetReminderLeads([5], Stempel());

        RecurrenceRunner.Complete(task, Moment("2026-09-16"), Stempel)!
            .ReminderLeads.Should().Equal(30);
    }

    [Fact]
    public void Nastepnik_niesie_przypomnienia_poprzednika()
    {
        // Przypomnienie z własną chwilą przesuwa się o tyle dni, ile dzieli wystąpienia:
        // „w przeddzień o dwudziestej" ma zostać przeddniem, a nie odezwać się natychmiast.
        // Wyprzedzenia przechodzą bez przeliczania — liczą się od godziny wystąpienia.
        var task = Zaplanowane(
            "2026-09-16", new RecurrenceRule(RecurrenceKind.Weekly, daysOfWeek: Weekdays.Wednesday));

        task.SetDoTime(new TimeOnly(19, 0), Stempel());
        task.SetReminderLeads([30, 1440], Stempel());
        task.SetReminder(Moment("2026-09-15", 20), Stempel());

        var next = RecurrenceRunner.Complete(task, Moment("2026-09-16"), Stempel);

        next!.DoDate.Should().Be(D("2026-09-23"));
        next.ReminderAt.Should().Be(Moment("2026-09-22", 20), "przeddzień zostaje przeddniem");
        next.ReminderLeads.Should().Equal(30, 1440);
    }

    [Fact]
    public void Pominiecie_wyrzuca_biezace_wystapienie_i_zostawia_rytm()
    {
        // „Tej środy nie będzie". Do dziś jedyną drogą było wyrzucenie zadania — a razem
        // z nim przepadał rytm, bo regułę niesie właśnie to wystąpienie.
        var task = Zaplanowane(
            "2026-09-16", new RecurrenceRule(RecurrenceKind.Weekly, daysOfWeek: Weekdays.Wednesday));

        var next = RecurrenceRunner.Skip(task, Moment("2026-09-16"), Stempel);

        task.State.Should().Be(TaskState.Trashed, "pominięte nie jest zrobione");
        task.Recurrence.Should().BeNull("regułę niesie zawsze najnowsze wystąpienie");

        next!.DoDate.Should().Be(D("2026-09-23"));
        next.Recurrence.Should().NotBeNull();
    }

    [Fact]
    public void Pominiecie_zadania_bez_rytmu_niczego_nie_zaklada()
    {
        var task = Zaplanowane("2026-09-16");

        RecurrenceRunner.Skip(task, Moment("2026-09-16"), Stempel).Should().BeNull();
        task.State.Should().Be(TaskState.Trashed);
    }
}
