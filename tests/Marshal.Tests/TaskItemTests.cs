using FluentAssertions;
using Marshal.Domain.Primitives;
using Marshal.Domain.Tasks;
using Xunit;

namespace Marshal.Tests;

public class TaskItemTests
{
    private static readonly DateTimeOffset Teraz = new(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(2));
    private static readonly Guid Obszar = Guid.CreateVersion7();

    private static Hlc Stamp(long ms = 1000) => new(ms, 0, "a");

    private static TaskItem Wrzut(string tytul = "Zadzwonić do przychodni") =>
        TaskItem.Capture(tytul, Teraz, Stamp());

    [Fact]
    public void Wrzut_ma_tylko_tytul_i_laduje_w_skrzynce()
    {
        var zadanie = Wrzut();

        zadanie.State.Should().Be(TaskState.Inbox);
        zadanie.Title.Should().Be("Zadzwonić do przychodni");
        zadanie.AreaId.Should().BeNull();
        zadanie.ProjectId.Should().BeNull();
        zadanie.Deadline.Should().BeNull();
        zadanie.DoDate.Should().BeNull();
    }

    [Fact]
    public void Wrzut_przycina_biale_znaki()
    {
        Wrzut("  kupić mleko  ").Title.Should().Be("kupić mleko");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Wrzut_bez_tresci_jest_odrzucany(string tytul)
    {
        var wrzuc = () => Wrzut(tytul);

        wrzuc.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Przetworzenie_na_nastepna_akcje_wymaga_obszaru()
    {
        var zadanie = Wrzut();

        var bezObszaru = () => zadanie.MakeNext(Guid.Empty, Stamp(2000));

        bezObszaru.Should().Throw<ArgumentException>();
        zadanie.State.Should().Be(TaskState.Inbox);
    }

    [Fact]
    public void Nastepna_akcja_dostaje_obszar_i_opuszcza_skrzynke()
    {
        var zadanie = Wrzut();

        zadanie.MakeNext(Obszar, Stamp(2000));

        zadanie.State.Should().Be(TaskState.Next);
        zadanie.AreaId.Should().Be(Obszar);
    }

    [Fact]
    public void Zaplanowanie_zapisuje_dzien_wykonania()
    {
        var zadanie = Wrzut();
        var dzien = new DateOnly(2026, 9, 21);

        zadanie.Schedule(Obszar, dzien, Stamp(2000));

        zadanie.State.Should().Be(TaskState.Scheduled);
        zadanie.DoDate.Should().Be(dzien);
    }

    [Fact]
    public void Oddelegowanie_wymaga_wskazania_osoby()
    {
        var zadanie = Wrzut();

        var bezOsoby = () => zadanie.Delegate(Obszar, "  ", new DateOnly(2026, 9, 16), null, Stamp(2000));

        bezOsoby.Should().Throw<ArgumentException>();
        zadanie.State.Should().Be(TaskState.Inbox);
    }

    [Fact]
    public void Oddelegowanie_zapisuje_kogo_i_od_kiedy()
    {
        var zadanie = Wrzut();
        var od = new DateOnly(2026, 9, 16);

        zadanie.Delegate(Obszar, " urząd miasta ", od, nudgeDays: 21, Stamp(2000));

        zadanie.State.Should().Be(TaskState.Waiting);
        zadanie.WaitingForWho.Should().Be("urząd miasta");
        zadanie.WaitingSince.Should().Be(od);
        zadanie.WaitingNudgeDays.Should().Be(21);
    }

    [Fact]
    public void Prog_ponaglenia_musi_byc_dodatni()
    {
        var zadanie = Wrzut();

        var zeroDni = () => zadanie.Delegate(Obszar, "ktoś", new DateOnly(2026, 9, 16), 0, Stamp(2000));

        zeroDni.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Wyjscie_z_oczekiwania_czysci_dane_oczekiwania()
    {
        var zadanie = Wrzut();
        zadanie.Delegate(Obszar, "ktoś", new DateOnly(2026, 9, 16), 7, Stamp(2000));

        zadanie.MakeNext(Obszar, Stamp(3000));

        zadanie.WaitingForWho.Should().BeNull();
        zadanie.WaitingSince.Should().BeNull();
        zadanie.WaitingNudgeDays.Should().BeNull();
    }

    [Fact]
    public void Kiedys_moze_moze_miec_date_powrotu()
    {
        var zadanie = Wrzut();
        var powrot = new DateOnly(2027, 1, 1);

        zadanie.Postpone(Obszar, powrot, Stamp(2000));

        zadanie.State.Should().Be(TaskState.Someday);
        zadanie.DeferUntil.Should().Be(powrot);
    }

    [Fact]
    public void Wykonanie_zapisuje_moment()
    {
        var zadanie = Wrzut();
        zadanie.MakeNext(Obszar, Stamp(2000));

        zadanie.Complete(Teraz, Stamp(3000));

        zadanie.State.Should().Be(TaskState.Done);
        zadanie.CompletedAt.Should().Be(Teraz);
    }

    [Fact]
    public void Otwarcie_na_nowo_wraca_do_nastepnych_gdy_obszar_jest_znany()
    {
        var zadanie = Wrzut();
        zadanie.MakeNext(Obszar, Stamp(2000));
        zadanie.Complete(Teraz, Stamp(3000));

        zadanie.Reopen(Stamp(4000));

        zadanie.State.Should().Be(TaskState.Next);
        zadanie.CompletedAt.Should().BeNull();
    }

    [Fact]
    public void Otwarcie_na_nowo_niewykonanego_jest_odrzucane()
    {
        var zadanie = Wrzut();

        var otworz = () => zadanie.Reopen(Stamp(2000));

        otworz.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Kosz_to_stan_a_nie_usuniecie_rekordu()
    {
        var zadanie = Wrzut();

        zadanie.Trash(Stamp(2000));

        zadanie.State.Should().Be(TaskState.Trashed);
        zadanie.Deleted.Should().BeFalse();
    }

    [Fact]
    public void Powrot_do_skrzynki_cofa_przetworzenie()
    {
        var zadanie = Wrzut();
        zadanie.Schedule(Obszar, new DateOnly(2026, 9, 21), Stamp(2000));

        zadanie.ReturnToInbox(Stamp(3000));

        zadanie.State.Should().Be(TaskState.Inbox);
        zadanie.DoDate.Should().BeNull();
    }

    [Fact]
    public void Zadanie_nie_moze_byc_wlasnym_podzadaniem()
    {
        var zadanie = Wrzut();

        var samo = () => zadanie.Reparent(zadanie.Id, Stamp(2000));

        samo.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Kazde_przejscie_podnosi_znacznik_zmiany()
    {
        var zadanie = Wrzut();
        var przed = zadanie.UpdatedAt;

        zadanie.MakeNext(Obszar, Stamp(2000));

        zadanie.UpdatedAt.Should().BeGreaterThan(przed);
    }
}
