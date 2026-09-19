using FluentAssertions;
using Marshal.Domain.Filters;
using Marshal.Domain.Primitives;
using Marshal.Domain.Tasks;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Filtry łączone: sprawdzanie warunków jako czysta funkcja (spec 11.5).
/// </summary>
public sealed class FilterQueryTests
{
    private static readonly DateOnly Dzis = new(2026, 9, 16);
    private static readonly DateTimeOffset Teraz = new(2026, 9, 16, 9, 0, 0, TimeSpan.FromHours(2));
    private static readonly Guid Obszar = Guid.CreateVersion7();

    private static int _licznik;

    private static Hlc Stamp() => new(Teraz.ToUnixTimeMilliseconds(), _licznik++, "biurko");

    private static TaskItem TaskId(string title = "cokolwiek")
    {
        var z = TaskItem.Capture(title, Teraz, Stamp());
        z.MakeNext(Obszar, Stamp());
        return z;
    }

    private static FilterSubject Podmiot(TaskItem task, params Guid[] tagi) =>
        new(task, tagi);

    [Fact]
    public void Filtr_bez_warunkow_nie_pasuje_do_niczego()
    {
        // Logika mówi co innego, interfejs mówi to. Zob. FilterQuery.IsEmpty.
        FilterQuery.Empty.Matches(Podmiot(TaskId()), Dzis).Should().BeFalse();
        FilterQuery.Empty.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Warunki_lacza_sie_spojnikiem_i()
    {
        var pasuje = TaskId();
        pasuje.SetPriority(Priority.High, Stamp());

        var niepasuje = TaskId();

        var filter = new FilterQuery([
            FilterCondition.States(TaskState.Next),
            FilterCondition.Priorities(Priority.High),
        ]);

        filter.Matches(Podmiot(pasuje), Dzis).Should().BeTrue();
        filter.Matches(Podmiot(niepasuje), Dzis).Should().BeFalse();
    }

    [Fact]
    public void Wartosci_wewnatrz_warunku_lacza_sie_spojnikiem_albo()
    {
        var next = TaskId();

        var zaplanowane = TaskId();
        zaplanowane.Schedule(Obszar, Dzis, Stamp());

        var kiedys = TaskId();
        kiedys.Postpone(Obszar, null, Stamp());

        var filter = new FilterQuery([FilterCondition.States(TaskState.Next, TaskState.Scheduled)]);

        filter.Matches(Podmiot(next), Dzis).Should().BeTrue();
        filter.Matches(Podmiot(zaplanowane), Dzis).Should().BeTrue();
        filter.Matches(Podmiot(kiedys), Dzis).Should().BeFalse();
    }

    [Fact]
    public void Drugi_warunek_na_to_samo_pole_zastepuje_pierwszy()
    {
        // Dwa warunki na jedno pole połączone „i" dawałyby zbiór pusty. Konstruktor,
        // w którym da się kliknąć warunek gwarantujący zero wyników, uczy nieufności.
        var filter = new FilterQuery([
            FilterCondition.States(TaskState.Waiting),
            FilterCondition.States(TaskState.Next),
        ]);

        filter.Conditions.Should().HaveCount(1);
        filter.Matches(Podmiot(TaskId()), Dzis).Should().BeTrue();
    }

    [Theory]
    [InlineData(-1, DateWindow.Overdue, true)]
    [InlineData(-1, DateWindow.Today, false)]
    [InlineData(0, DateWindow.Today, true)]
    [InlineData(0, DateWindow.ThisWeek, true)]
    [InlineData(6, DateWindow.ThisWeek, true)]
    [InlineData(7, DateWindow.ThisWeek, false)]
    [InlineData(7, DateWindow.Next30Days, true)]
    [InlineData(31, DateWindow.Next30Days, false)]
    [InlineData(1, DateWindow.Future, true)]
    [InlineData(0, DateWindow.Future, false)]
    [InlineData(3, DateWindow.Any, true)]
    [InlineData(3, DateWindow.None, false)]
    public void Okna_czasowe_liczone_wzgledem_dzisiaj(int przesuniecie, DateWindow okno, bool waiting)
    {
        var task = TaskId();
        task.SetDeadline(Dzis.AddDays(przesuniecie), Stamp());

        new FilterQuery([FilterCondition.Deadline(okno)])
            .Matches(Podmiot(task), Dzis).Should().Be(waiting);
    }

    [Fact]
    public void Puste_pole_daty_pasuje_tylko_do_okna_bez_daty()
    {
        var task = TaskId();

        new FilterQuery([FilterCondition.DoDate(DateWindow.None)])
            .Matches(Podmiot(task), Dzis).Should().BeTrue();

        new FilterQuery([FilterCondition.DoDate(DateWindow.Any)])
            .Matches(Podmiot(task), Dzis).Should().BeFalse();
    }

    [Fact]
    public void Tydzien_liczy_sie_od_dzisiaj_a_nie_kalendarzowo()
    {
        // Sobota. Tydzień kalendarzowy zostawiłby jeden dzień — czyli filtr „na ten
        // tydzień" byłby najbardziej pusty wtedy, kiedy planuje się weekend.
        var sobota = new DateOnly(2026, 9, 19);

        var task = TaskId();
        task.SetDeadline(sobota.AddDays(4), Stamp());

        new FilterQuery([FilterCondition.Deadline(DateWindow.ThisWeek)])
            .Matches(Podmiot(task), sobota).Should().BeTrue();
    }

    [Fact]
    public void Nieoszacowane_nie_miesci_sie_w_zadnym_kwadransie()
    {
        // Ta sama zasada co w widoku „Teraz" (8.1): bez oszacowania nie ma jak ocenić,
        // czy się zmieści, a domyślne „pewnie tak" byłoby zgadywaniem podanym jako fakt.
        var bez = TaskId();

        var z = TaskId();
        z.SetEstimate(10, Energy.Low, Stamp());

        var filter = new FilterQuery([FilterCondition.Estimate(15)]);

        filter.Matches(Podmiot(bez), Dzis).Should().BeFalse();
        filter.Matches(Podmiot(z), Dzis).Should().BeTrue();
    }

    [Fact]
    public void Tag_pasuje_gdy_zadanie_ma_ktorykolwiek_ze_wskazanych()
    {
        var dom = Guid.CreateVersion7();
        var telefon = Guid.CreateVersion7();
        var zakupy = Guid.CreateVersion7();

        var filter = new FilterQuery([FilterCondition.Tags(dom, telefon)]);

        filter.Matches(Podmiot(TaskId(), zakupy, telefon), Dzis).Should().BeTrue();
        filter.Matches(Podmiot(TaskId(), zakupy), Dzis).Should().BeFalse();
    }

    [Fact]
    public void Pusty_identyfikator_znaczy_bez_projektu()
    {
        // Zadania bez projektu są dopuszczalne na stałe (spec 5.3, rozstrzygnięcie 5),
        // więc „pokaż luzem leżące" musi być wykonalne.
        var luzem = TaskId();

        var wProjekcie = TaskId();
        wProjekcie.MoveTo(Obszar, Guid.CreateVersion7(), Stamp());

        var filter = new FilterQuery([FilterCondition.Projects(Guid.Empty)]);

        filter.Matches(Podmiot(luzem), Dzis).Should().BeTrue();
        filter.Matches(Podmiot(wProjekcie), Dzis).Should().BeFalse();
    }

    [Fact]
    public void Szukanie_tekstu_nie_rozroznia_wielkosci_liter()
    {
        var task = TaskId("Zadzwonić do Żłobka");

        new FilterQuery([FilterCondition.Contains("żłobka")])
            .Matches(Podmiot(task), Dzis).Should().BeTrue();
    }

    [Fact]
    public void Szukanie_tekstu_siega_takze_do_notatki()
    {
        var task = TaskId("Zadzwonić");
        task.SetNote("numer w kalendarzu na lodówce", Stamp());

        new FilterQuery([FilterCondition.Contains("lodówce")])
            .Matches(Podmiot(task), Dzis).Should().BeTrue();
    }

    [Fact]
    public void Wykonane_i_wyrzucone_nie_wchodza_dopoki_filtr_o_nie_nie_poprosi()
    {
        var zrobione = TaskId();
        zrobione.Complete(Teraz, Stamp());

        var wykosz = TaskId();
        wykosz.Trash(Stamp());

        var poObszarze = new FilterQuery([FilterCondition.Areas(Obszar)]);
        poObszarze.Matches(Podmiot(zrobione), Dzis).Should().BeFalse();
        poObszarze.Matches(Podmiot(wykosz), Dzis).Should().BeFalse();

        var poStanie = new FilterQuery([
            FilterCondition.Areas(Obszar),
            FilterCondition.States(TaskState.Done),
        ]);

        poStanie.Matches(Podmiot(zrobione), Dzis).Should().BeTrue();
        poStanie.Matches(Podmiot(wykosz), Dzis).Should().BeFalse();
    }

    [Fact]
    public void Nagrobek_nie_wchodzi_nawet_gdy_filtr_pyta_o_jego_stan()
    {
        var skasowane = TaskId();
        skasowane.MarkDeleted(Stamp());

        new FilterQuery([FilterCondition.States(TaskState.Next)])
            .Matches(Podmiot(skasowane), Dzis).Should().BeFalse();
    }

    [Fact]
    public void Warunek_bez_wartosci_nie_da_sie_zbudowac()
    {
        var zbudowanie = () => FilterCondition.States();
        zbudowanie.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Zapis_i_odczyt_oddaja_ten_sam_filtr()
    {
        var tag = Guid.CreateVersion7();

        var filter = new FilterQuery([
            FilterCondition.States(TaskState.Next, TaskState.Scheduled),
            FilterCondition.Tags(tag),
            FilterCondition.Deadline(DateWindow.ThisWeek),
            FilterCondition.Estimate(15),
            FilterCondition.Contains("telefon"),
        ]);

        var odczytany = FilterQuery.FromJson(filter.ToJson());

        odczytany.Should().NotBeNull();
        odczytany!.ToJson().Should().Be(filter.ToJson());
        odczytany.Conditions.Should().HaveCount(5);
    }

    [Fact]
    public void Zapis_trzyma_wyliczenia_jako_nazwy()
    {
        // Filtr siedzi w bazie i w dzienniku zmian jako tekst. „Next" da się przeczytać
        // przy diagnostyce, a „1" nie — i nie rozjedzie się, gdy do wyliczenia dojdzie
        // kiedyś wartość w środku.
        new FilterQuery([FilterCondition.States(TaskState.Next)]).ToJson()
            .Should().Contain("\"Next\"");
    }

    [Fact]
    public void Kolejnosc_warunkow_nie_zmienia_zapisu()
    {
        // Ta sama decyzja musi dawać ten sam tekst, bo po tekście rozpoznajemy,
        // czy zapisany widok w ogóle się zmienił — zob. FilterService.UpdateAsync.
        var a = new FilterQuery([
            FilterCondition.Contains("telefon"),
            FilterCondition.States(TaskState.Next),
        ]);

        var b = new FilterQuery([
            FilterCondition.States(TaskState.Next),
            FilterCondition.Contains("telefon"),
        ]);

        a.ToJson().Should().Be(b.ToJson());
    }

    [Fact]
    public void Nieczytelny_warunek_odpada_pojedynczo_a_reszta_zostaje()
    {
        // Widok ułożony w nowszej wersji aplikacji ma tu zadziałać w tej części,
        // którą ta wersja rozumie — zamiast zniknąć w całości (spec 9.4).
        const string json = """
            [{"Field":"State","Values":["Next"]},{"Field":"Estimate","MaxMinutes":0}]
            """;

        var filter = FilterQuery.FromJson(json);

        filter.Should().NotBeNull();
        filter!.Conditions.Should().HaveCount(1);
        filter.Conditions[0].Field.Should().Be(FilterField.State);
    }

    [Fact]
    public void Zapis_calkiem_nieczytelny_daje_puste_zamiast_wyjatku()
    {
        FilterQuery.FromJson("{to nie jest json").Should().BeNull();
        FilterQuery.FromJson("[]").Should().BeNull();
        FilterQuery.FromJson(null).Should().BeNull();
    }
}
