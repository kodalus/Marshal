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

    private static Hlc Znacznik() => new(Teraz.ToUnixTimeMilliseconds(), _licznik++, "biurko");

    private static TaskItem Zadanie(string tytul = "cokolwiek")
    {
        var z = TaskItem.Capture(tytul, Teraz, Znacznik());
        z.MakeNext(Obszar, Znacznik());
        return z;
    }

    private static FilterSubject Podmiot(TaskItem zadanie, params Guid[] tagi) =>
        new(zadanie, tagi);

    [Fact]
    public void Filtr_bez_warunkow_nie_pasuje_do_niczego()
    {
        // Logika mówi co innego, interfejs mówi to. Zob. FilterQuery.IsEmpty.
        FilterQuery.Empty.Matches(Podmiot(Zadanie()), Dzis).Should().BeFalse();
        FilterQuery.Empty.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Warunki_lacza_sie_spojnikiem_i()
    {
        var pasuje = Zadanie();
        pasuje.SetPriority(Priority.High, Znacznik());

        var niepasuje = Zadanie();

        var filtr = new FilterQuery([
            FilterCondition.States(TaskState.Next),
            FilterCondition.Priorities(Priority.High),
        ]);

        filtr.Matches(Podmiot(pasuje), Dzis).Should().BeTrue();
        filtr.Matches(Podmiot(niepasuje), Dzis).Should().BeFalse();
    }

    [Fact]
    public void Wartosci_wewnatrz_warunku_lacza_sie_spojnikiem_albo()
    {
        var nastepne = Zadanie();

        var zaplanowane = Zadanie();
        zaplanowane.Schedule(Obszar, Dzis, Znacznik());

        var kiedys = Zadanie();
        kiedys.Postpone(Obszar, null, Znacznik());

        var filtr = new FilterQuery([FilterCondition.States(TaskState.Next, TaskState.Scheduled)]);

        filtr.Matches(Podmiot(nastepne), Dzis).Should().BeTrue();
        filtr.Matches(Podmiot(zaplanowane), Dzis).Should().BeTrue();
        filtr.Matches(Podmiot(kiedys), Dzis).Should().BeFalse();
    }

    [Fact]
    public void Drugi_warunek_na_to_samo_pole_zastepuje_pierwszy()
    {
        // Dwa warunki na jedno pole połączone „i" dawałyby zbiór pusty. Konstruktor,
        // w którym da się kliknąć warunek gwarantujący zero wyników, uczy nieufności.
        var filtr = new FilterQuery([
            FilterCondition.States(TaskState.Waiting),
            FilterCondition.States(TaskState.Next),
        ]);

        filtr.Conditions.Should().HaveCount(1);
        filtr.Matches(Podmiot(Zadanie()), Dzis).Should().BeTrue();
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
    public void Okna_czasowe_liczone_wzgledem_dzisiaj(int przesuniecie, DateWindow okno, bool oczekiwane)
    {
        var zadanie = Zadanie();
        zadanie.SetDeadline(Dzis.AddDays(przesuniecie), Znacznik());

        new FilterQuery([FilterCondition.Deadline(okno)])
            .Matches(Podmiot(zadanie), Dzis).Should().Be(oczekiwane);
    }

    [Fact]
    public void Puste_pole_daty_pasuje_tylko_do_okna_bez_daty()
    {
        var zadanie = Zadanie();

        new FilterQuery([FilterCondition.DoDate(DateWindow.None)])
            .Matches(Podmiot(zadanie), Dzis).Should().BeTrue();

        new FilterQuery([FilterCondition.DoDate(DateWindow.Any)])
            .Matches(Podmiot(zadanie), Dzis).Should().BeFalse();
    }

    [Fact]
    public void Tydzien_liczy_sie_od_dzisiaj_a_nie_kalendarzowo()
    {
        // Sobota. Tydzień kalendarzowy zostawiłby jeden dzień — czyli filtr „na ten
        // tydzień" byłby najbardziej pusty wtedy, kiedy planuje się weekend.
        var sobota = new DateOnly(2026, 9, 19);

        var zadanie = Zadanie();
        zadanie.SetDeadline(sobota.AddDays(4), Znacznik());

        new FilterQuery([FilterCondition.Deadline(DateWindow.ThisWeek)])
            .Matches(Podmiot(zadanie), sobota).Should().BeTrue();
    }

    [Fact]
    public void Nieoszacowane_nie_miesci_sie_w_zadnym_kwadransie()
    {
        // Ta sama zasada co w widoku „Teraz" (8.1): bez oszacowania nie ma jak ocenić,
        // czy się zmieści, a domyślne „pewnie tak" byłoby zgadywaniem podanym jako fakt.
        var bez = Zadanie();

        var z = Zadanie();
        z.SetEstimate(10, Energy.Low, Znacznik());

        var filtr = new FilterQuery([FilterCondition.Estimate(15)]);

        filtr.Matches(Podmiot(bez), Dzis).Should().BeFalse();
        filtr.Matches(Podmiot(z), Dzis).Should().BeTrue();
    }

    [Fact]
    public void Tag_pasuje_gdy_zadanie_ma_ktorykolwiek_ze_wskazanych()
    {
        var dom = Guid.CreateVersion7();
        var telefon = Guid.CreateVersion7();
        var zakupy = Guid.CreateVersion7();

        var filtr = new FilterQuery([FilterCondition.Tags(dom, telefon)]);

        filtr.Matches(Podmiot(Zadanie(), zakupy, telefon), Dzis).Should().BeTrue();
        filtr.Matches(Podmiot(Zadanie(), zakupy), Dzis).Should().BeFalse();
    }

    [Fact]
    public void Pusty_identyfikator_znaczy_bez_projektu()
    {
        // Zadania bez projektu są dopuszczalne na stałe (spec 5.3, rozstrzygnięcie 5),
        // więc „pokaż luzem leżące" musi być wykonalne.
        var luzem = Zadanie();

        var wProjekcie = Zadanie();
        wProjekcie.MoveTo(Obszar, Guid.CreateVersion7(), Znacznik());

        var filtr = new FilterQuery([FilterCondition.Projects(Guid.Empty)]);

        filtr.Matches(Podmiot(luzem), Dzis).Should().BeTrue();
        filtr.Matches(Podmiot(wProjekcie), Dzis).Should().BeFalse();
    }

    [Fact]
    public void Szukanie_tekstu_nie_rozroznia_wielkosci_liter()
    {
        var zadanie = Zadanie("Zadzwonić do Żłobka");

        new FilterQuery([FilterCondition.Contains("żłobka")])
            .Matches(Podmiot(zadanie), Dzis).Should().BeTrue();
    }

    [Fact]
    public void Szukanie_tekstu_siega_takze_do_notatki()
    {
        var zadanie = Zadanie("Zadzwonić");
        zadanie.SetNote("numer w kalendarzu na lodówce", Znacznik());

        new FilterQuery([FilterCondition.Contains("lodówce")])
            .Matches(Podmiot(zadanie), Dzis).Should().BeTrue();
    }

    [Fact]
    public void Wykonane_i_wyrzucone_nie_wchodza_dopoki_filtr_o_nie_nie_poprosi()
    {
        var zrobione = Zadanie();
        zrobione.Complete(Teraz, Znacznik());

        var wykosz = Zadanie();
        wykosz.Trash(Znacznik());

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
        var skasowane = Zadanie();
        skasowane.MarkDeleted(Znacznik());

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

        var filtr = new FilterQuery([
            FilterCondition.States(TaskState.Next, TaskState.Scheduled),
            FilterCondition.Tags(tag),
            FilterCondition.Deadline(DateWindow.ThisWeek),
            FilterCondition.Estimate(15),
            FilterCondition.Contains("telefon"),
        ]);

        var odczytany = FilterQuery.FromJson(filtr.ToJson());

        odczytany.Should().NotBeNull();
        odczytany!.ToJson().Should().Be(filtr.ToJson());
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

        var filtr = FilterQuery.FromJson(json);

        filtr.Should().NotBeNull();
        filtr!.Conditions.Should().HaveCount(1);
        filtr.Conditions[0].Field.Should().Be(FilterField.State);
    }

    [Fact]
    public void Zapis_calkiem_nieczytelny_daje_puste_zamiast_wyjatku()
    {
        FilterQuery.FromJson("{to nie jest json").Should().BeNull();
        FilterQuery.FromJson("[]").Should().BeNull();
        FilterQuery.FromJson(null).Should().BeNull();
    }
}
