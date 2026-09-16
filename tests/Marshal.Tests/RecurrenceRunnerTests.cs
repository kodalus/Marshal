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
    private readonly Guid _obszar = Guid.CreateVersion7();
    private Hlc _zegar = Hlc.Zero("test");

    private Hlc Stempel() => _zegar = Hlc.Next(_zegar, _zegar.WallMs + 1);

    private static DateOnly D(string iso) => DateOnly.Parse(iso);

    private static DateTimeOffset Chwila(string iso, int godzina = 9) =>
        new(DateOnly.Parse(iso).ToDateTime(new TimeOnly(godzina, 0)), TimeSpan.FromHours(2));

    private TaskItem Zaplanowane(string doDate, RecurrenceRule? regula = null)
    {
        var zadanie = TaskItem.Capture("Wynieść śmieci", Chwila(doDate), Stempel());
        zadanie.Schedule(_obszar, D(doDate), Stempel());

        if (regula is not null)
        {
            zadanie.SetRecurrence(regula, Stempel());
        }

        return zadanie;
    }

    // --- odhaczenie (8.4) ----------------------------------------------------

    [Fact]
    public void Odhaczenie_zwyklego_zadania_nie_rodzi_nastepnika()
    {
        var zadanie = Zaplanowane("2026-09-16");

        RecurrenceRunner.Complete(zadanie, Chwila("2026-09-16"), Stempel).Should().BeNull();
        zadanie.State.Should().Be(TaskState.Done);
    }

    [Fact]
    public void Odhaczenie_z_zaczepieniem_na_planie_nie_przesuwa_rytmu()
    {
        // „Co poniedziałek śmieci" odhaczone we wtorek: kolejny poniedziałek zostaje
        // poniedziałkiem. To jest cała różnica między rytmem świata a rytmem moich rąk.
        var zadanie = Zaplanowane(
            "2026-09-14", new RecurrenceRule(RecurrenceKind.Weekly, daysOfWeek: Weekdays.Monday));

        var nastepne = RecurrenceRunner.Complete(zadanie, Chwila("2026-09-15"), Stempel);

        nastepne!.DoDate.Should().Be(D("2026-09-21"));
    }

    [Fact]
    public void Odhaczenie_z_zaczepieniem_na_wykonaniu_przesuwa_caly_rytm()
    {
        // „Co 3 dni podlewanie" odhaczone z dwudniowym opóźnieniem: kolejne trzy dni
        // liczą się od podlania, a nie od tego, co było obiecane.
        var zadanie = Zaplanowane(
            "2026-09-14", new RecurrenceRule(RecurrenceKind.EveryNDays, interval: 3));

        var nastepne = RecurrenceRunner.Complete(zadanie, Chwila("2026-09-16"), Stempel);

        nastepne!.DoDate.Should().Be(D("2026-09-19"));
    }

    [Fact]
    public void Odhaczone_wystapienie_zostaje_odhaczone_a_nie_wedruje_w_przyszlosc()
    {
        // Historia „robiłam to w każdy poniedziałek prócz jednego" jest całą wartością
        // powtarzalności; zadanie przestawiane w przyszłość jej nie niesie.
        var zadanie = Zaplanowane(
            "2026-09-14", new RecurrenceRule(RecurrenceKind.Weekly, daysOfWeek: Weekdays.Monday));

        var nastepne = RecurrenceRunner.Complete(zadanie, Chwila("2026-09-14"), Stempel);

        zadanie.State.Should().Be(TaskState.Done);
        zadanie.DoDate.Should().Be(D("2026-09-14"));
        nastepne!.Id.Should().NotBe(zadanie.Id);
        nastepne.State.Should().Be(TaskState.Scheduled);
    }

    [Fact]
    public void Nastepne_wystapienie_dziedziczy_obszar_projekt_i_wage()
    {
        var zadanie = Zaplanowane("2026-09-14", new RecurrenceRule(RecurrenceKind.Daily));
        var projekt = Guid.CreateVersion7();
        zadanie.MoveTo(_obszar, projekt, Stempel());
        zadanie.SetPriority(Priority.High, Stempel());
        zadanie.SetNote("z kluczem do piwnicy", Stempel());

        var nastepne = RecurrenceRunner.Complete(zadanie, Chwila("2026-09-14"), Stempel)!;

        nastepne.AreaId.Should().Be(_obszar);
        nastepne.ProjectId.Should().Be(projekt);
        nastepne.Priority.Should().Be(Priority.High);
        nastepne.Note.Should().Be("z kluczem do piwnicy");
        nastepne.Title.Should().Be("Wynieść śmieci");
    }

    [Fact]
    public void Termin_przenosi_sie_z_zachowaniem_odstepu()
    {
        // „Zapłacić do 10-go" przy racie robionej 5-go to pięć dni zapasu, co miesiąc
        // tyle samo. Skopiowany wprost byłby od razu przeterminowany i N5 zacząłby kłamać.
        var zadanie = Zaplanowane(
            "2026-09-05", new RecurrenceRule(RecurrenceKind.Monthly, dayOfMonth: 5));
        zadanie.SetDeadline(D("2026-09-10"), Stempel());

        var nastepne = RecurrenceRunner.Complete(zadanie, Chwila("2026-09-05"), Stempel)!;

        nastepne.DoDate.Should().Be(D("2026-10-05"));
        nastepne.Deadline.Should().Be(D("2026-10-10"));
    }

    [Fact]
    public void Regule_nosi_zawsze_najnowsze_wystapienie()
    {
        var zadanie = Zaplanowane("2026-09-14", new RecurrenceRule(RecurrenceKind.Daily));

        var nastepne = RecurrenceRunner.Complete(zadanie, Chwila("2026-09-14"), Stempel)!;

        zadanie.Recurrence.Should().BeNull();
        nastepne.Recurrence.Should().NotBeNull();
    }

    [Fact]
    public void Odhaczenie_dwa_razy_tego_samego_dnia_nie_robi_dwoch_nastepnikow()
    {
        var zadanie = Zaplanowane("2026-09-14", new RecurrenceRule(RecurrenceKind.Daily));

        RecurrenceRunner.Complete(zadanie, Chwila("2026-09-14"), Stempel).Should().NotBeNull();
        RecurrenceRunner.Complete(zadanie, Chwila("2026-09-14"), Stempel).Should().BeNull();
    }

    [Fact]
    public void Odhaczenie_zadania_z_przyszlosci_liczy_od_jego_daty()
    {
        // Zrobione z wyprzedzeniem. Przy zaczepieniu na planie rytm zostaje nietknięty.
        var zadanie = Zaplanowane(
            "2026-09-21", new RecurrenceRule(RecurrenceKind.Weekly, daysOfWeek: Weekdays.Monday));

        var nastepne = RecurrenceRunner.Complete(zadanie, Chwila("2026-09-16"), Stempel)!;

        nastepne.DoDate.Should().Be(D("2026-09-28"));
    }

    [Fact]
    public void Wyczerpana_seria_nie_rodzi_nastepnika()
    {
        var zadanie = Zaplanowane("2026-09-14", new RecurrenceRule(RecurrenceKind.Daily, count: 1));

        RecurrenceRunner.Complete(zadanie, Chwila("2026-09-14"), Stempel).Should().BeNull();
    }

    // --- przejście dnia (8.4 tabela, 8.7) ------------------------------------

    [Fact]
    public void Zwykle_zaplanowane_przesuwa_sie_na_dzis_z_licznikiem()
    {
        var zadanie = Zaplanowane("2026-09-10");

        RecurrenceRunner.Rollover(zadanie, Chwila("2026-09-16"), Stempel).Should().BeNull();

        zadanie.DoDate.Should().Be(D("2026-09-16"));
        zadanie.RollCount.Should().Be(1);
        zadanie.State.Should().Be(TaskState.Scheduled);
    }

    [Fact]
    public void Przesuniecie_nie_rusza_terminu()
    {
        // Termin to fakt zewnętrzny — świat się nie przesunął, więc minięcie zostaje
        // przeterminowaniem (N5). Data wykonania to obietnica dana sobie, i to ona idzie.
        var zadanie = Zaplanowane("2026-09-10");
        zadanie.SetDeadline(D("2026-09-12"), Stempel());

        RecurrenceRunner.Rollover(zadanie, Chwila("2026-09-16"), Stempel);

        zadanie.Deadline.Should().Be(D("2026-09-12"));
    }

    [Fact]
    public void Przejscie_dnia_puszczone_dwa_razy_liczy_raz()
    {
        var zadanie = Zaplanowane("2026-09-10");

        RecurrenceRunner.Rollover(zadanie, Chwila("2026-09-16"), Stempel);
        RecurrenceRunner.Rollover(zadanie, Chwila("2026-09-16"), Stempel);

        zadanie.RollCount.Should().Be(1);
    }

    [Fact]
    public void Zadanie_na_dzis_nie_jest_ruszane()
    {
        var zadanie = Zaplanowane("2026-09-16");

        RecurrenceRunner.Rollover(zadanie, Chwila("2026-09-16"), Stempel).Should().BeNull();

        zadanie.RollCount.Should().Be(0);
    }

    [Fact]
    public void Wykonane_zadanie_z_przeszlosci_nie_jest_ruszane()
    {
        var zadanie = Zaplanowane("2026-09-10");
        zadanie.Complete(Chwila("2026-09-10"), Stempel());

        RecurrenceRunner.Rollover(zadanie, Chwila("2026-09-16"), Stempel).Should().BeNull();

        zadanie.DoDate.Should().Be(D("2026-09-10"));
    }

    [Fact]
    public void Pominiete_z_Skip_trafia_do_kosza_i_rodzi_nastepne()
    {
        var zadanie = Zaplanowane(
            "2026-09-14",
            new RecurrenceRule(RecurrenceKind.Daily, onMissed: OnMissed.Skip));

        var nastepne = RecurrenceRunner.Rollover(zadanie, Chwila("2026-09-16"), Stempel);

        zadanie.State.Should().Be(TaskState.Trashed);
        nastepne!.DoDate.Should().Be(D("2026-09-15"));
    }

    [Fact]
    public void Pominiete_z_Carry_przenosi_sie_na_dzis_i_nie_rodzi_nastepnego()
    {
        var zadanie = Zaplanowane("2026-09-14", new RecurrenceRule(RecurrenceKind.Daily));

        var nastepne = RecurrenceRunner.Rollover(zadanie, Chwila("2026-09-16"), Stempel);

        nastepne.Should().BeNull();
        zadanie.DoDate.Should().Be(D("2026-09-16"));
        zadanie.CarriedSince.Should().Be(D("2026-09-14"));
        zadanie.Recurrence.Should().NotBeNull();
    }

    [Fact]
    public void Carry_przez_trzy_tygodnie_z_rzedu_pamieta_pierwszy_dzien()
    {
        // „Zaległe od 14 września", nie „od wczoraj". Bez tego N12 nigdy nie doliczyłby
        // trzydziestu dni i strażnik rytmu nie odezwałby się nigdy.
        var zadanie = Zaplanowane("2026-09-14", new RecurrenceRule(RecurrenceKind.Daily));

        foreach (var dzien in new[] { "2026-09-15", "2026-09-22", "2026-10-05" })
        {
            RecurrenceRunner.Rollover(zadanie, Chwila(dzien), Stempel);
        }

        zadanie.CarriedSince.Should().Be(D("2026-09-14"));
        zadanie.DoDate.Should().Be(D("2026-10-05"));
    }

    [Fact]
    public void Pominiete_z_Accumulate_zostaje_zalegloscia_a_rytm_idzie_dalej()
    {
        // Rozstrzygnięcie sprzeczności 8.4 z 8.7: gdyby zaległe wystąpienie zostało
        // zaplanowane na swoją dawną datę, nazajutrz przesunęłoby się na dziś, potem
        // znowu, i po czterech dniach odpaliłoby N15. Zostaje więc następną akcją
        // bez dnia, a dzień, na który było umówione, siedzi w „zaległe od".
        var zadanie = Zaplanowane(
            "2026-09-14",
            new RecurrenceRule(RecurrenceKind.Daily, onMissed: OnMissed.Accumulate));

        var nastepne = RecurrenceRunner.Rollover(zadanie, Chwila("2026-09-16"), Stempel);

        zadanie.State.Should().Be(TaskState.Next);
        zadanie.DoDate.Should().BeNull();
        zadanie.CarriedSince.Should().Be(D("2026-09-14"));
        zadanie.Recurrence.Should().BeNull();
        nastepne!.DoDate.Should().Be(D("2026-09-15"));
    }

    [Fact]
    public void Zalegle_wystapienie_z_Accumulate_nie_jest_juz_przesuwane()
    {
        var zadanie = Zaplanowane(
            "2026-09-14",
            new RecurrenceRule(RecurrenceKind.Daily, onMissed: OnMissed.Accumulate));

        RecurrenceRunner.Rollover(zadanie, Chwila("2026-09-16"), Stempel);
        RecurrenceRunner.Rollover(zadanie, Chwila("2026-09-20"), Stempel);

        zadanie.RollCount.Should().Be(0);
        zadanie.CarriedSince.Should().Be(D("2026-09-14"));
    }

    [Fact]
    public void Skip_po_tygodniu_nieobecnosci_daje_jedno_wystapienie_a_nie_siedem()
    {
        // Nagrobek każdego przeskoczonego dnia nie jest niczyją informacją, a rozjechałby
        // się po wszystkich urządzeniach.
        var zadanie = Zaplanowane(
            "2026-09-09",
            new RecurrenceRule(RecurrenceKind.Daily, onMissed: OnMissed.Skip));

        var nastepne = RecurrenceRunner.Rollover(zadanie, Chwila("2026-09-16"), Stempel);

        nastepne!.DoDate.Should().Be(D("2026-09-16"));
        RecurrenceRunner.Rollover(nastepne, Chwila("2026-09-16"), Stempel).Should().BeNull();
    }

    [Fact]
    public void Skip_zuzywa_licznik_takze_za_dni_przeskoczone()
    {
        // Seria „pięć razy" przespana przez pięć dni jest serią skończoną, a nie serią,
        // która czeka na kolejne pięć okazji.
        var zadanie = Zaplanowane(
            "2026-09-09",
            new RecurrenceRule(RecurrenceKind.Daily, onMissed: OnMissed.Skip, count: 3));

        RecurrenceRunner.Rollover(zadanie, Chwila("2026-09-16"), Stempel).Should().BeNull();
    }

    [Fact]
    public void Accumulate_puszczony_dwa_razy_nie_robi_dwoch_kopii()
    {
        // Najważniejszy test przejścia dnia: bez zabrania reguły poprzednikowi każde
        // uruchomienie dokładałoby po jednej pozycji, a aplikacja startuje wiele razy
        // dziennie i na dwóch urządzeniach.
        var zadanie = Zaplanowane(
            "2026-09-14",
            new RecurrenceRule(RecurrenceKind.Daily, onMissed: OnMissed.Accumulate));

        RecurrenceRunner.Rollover(zadanie, Chwila("2026-09-16"), Stempel).Should().NotBeNull();
        RecurrenceRunner.Rollover(zadanie, Chwila("2026-09-16"), Stempel).Should().BeNull();
    }
}
