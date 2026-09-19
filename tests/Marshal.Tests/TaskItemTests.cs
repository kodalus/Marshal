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

    private static TaskItem Wrzut(string title = "Zadzwonić do przychodni") =>
        TaskItem.Capture(title, Teraz, Stamp());

    [Fact]
    public void Wrzut_ma_tylko_tytul_i_laduje_w_skrzynce()
    {
        var task = Wrzut();

        task.State.Should().Be(TaskState.Inbox);
        task.Title.Should().Be("Zadzwonić do przychodni");
        task.AreaId.Should().BeNull();
        task.ProjectId.Should().BeNull();
        task.Deadline.Should().BeNull();
        task.DoDate.Should().BeNull();
    }

    [Fact]
    public void Wrzut_przycina_biale_znaki()
    {
        Wrzut("  kupić mleko  ").Title.Should().Be("kupić mleko");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Wrzut_bez_tresci_jest_odrzucany(string title)
    {
        var wrzuc = () => Wrzut(title);

        wrzuc.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Przetworzenie_na_nastepna_akcje_wymaga_obszaru()
    {
        var task = Wrzut();

        var bezObszaru = () => task.MakeNext(Guid.Empty, Stamp(2000));

        bezObszaru.Should().Throw<ArgumentException>();
        task.State.Should().Be(TaskState.Inbox);
    }

    [Fact]
    public void Nastepna_akcja_dostaje_obszar_i_opuszcza_skrzynke()
    {
        var task = Wrzut();

        task.MakeNext(Obszar, Stamp(2000));

        task.State.Should().Be(TaskState.Next);
        task.AreaId.Should().Be(Obszar);
    }

    [Fact]
    public void Zaplanowanie_zapisuje_dzien_wykonania()
    {
        var task = Wrzut();
        var day = new DateOnly(2026, 9, 21);

        task.Schedule(Obszar, day, Stamp(2000));

        task.State.Should().Be(TaskState.Scheduled);
        task.DoDate.Should().Be(day);
    }

    [Fact]
    public void Oddelegowanie_wymaga_wskazania_osoby()
    {
        var task = Wrzut();

        var bezOsoby = () => task.Delegate(Obszar, "  ", new DateOnly(2026, 9, 16), null, Stamp(2000));

        bezOsoby.Should().Throw<ArgumentException>();
        task.State.Should().Be(TaskState.Inbox);
    }

    [Fact]
    public void Oddelegowanie_zapisuje_kogo_i_od_kiedy()
    {
        var task = Wrzut();
        var od = new DateOnly(2026, 9, 16);

        task.Delegate(Obszar, " urząd miasta ", od, nudgeDays: 21, Stamp(2000));

        task.State.Should().Be(TaskState.Waiting);
        task.WaitingForWho.Should().Be("urząd miasta");
        task.WaitingSince.Should().Be(od);
        task.WaitingNudgeDays.Should().Be(21);
    }

    [Fact]
    public void Prog_ponaglenia_musi_byc_dodatni()
    {
        var task = Wrzut();

        var zeroDni = () => task.Delegate(Obszar, "ktoś", new DateOnly(2026, 9, 16), 0, Stamp(2000));

        zeroDni.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Wyjscie_z_oczekiwania_czysci_dane_oczekiwania()
    {
        var task = Wrzut();
        task.Delegate(Obszar, "ktoś", new DateOnly(2026, 9, 16), 7, Stamp(2000));

        task.MakeNext(Obszar, Stamp(3000));

        task.WaitingForWho.Should().BeNull();
        task.WaitingSince.Should().BeNull();
        task.WaitingNudgeDays.Should().BeNull();
    }

    [Fact]
    public void Kiedys_moze_moze_miec_date_powrotu()
    {
        var task = Wrzut();
        var powrot = new DateOnly(2027, 1, 1);

        task.Postpone(Obszar, powrot, Stamp(2000));

        task.State.Should().Be(TaskState.Someday);
        task.DeferUntil.Should().Be(powrot);
    }

    [Fact]
    public void Wykonanie_zapisuje_moment()
    {
        var task = Wrzut();
        task.MakeNext(Obszar, Stamp(2000));

        task.Complete(Teraz, Stamp(3000));

        task.State.Should().Be(TaskState.Done);
        task.CompletedAt.Should().Be(Teraz);
    }

    [Fact]
    public void Otwarcie_na_nowo_wraca_do_nastepnych_gdy_obszar_jest_znany()
    {
        var task = Wrzut();
        task.MakeNext(Obszar, Stamp(2000));
        task.Complete(Teraz, Stamp(3000));

        task.Reopen(Stamp(4000));

        task.State.Should().Be(TaskState.Next);
        task.CompletedAt.Should().BeNull();
    }

    /// <summary>
    /// Zadanie z dniem wykonania wraca do zaplanowanych, nie do następnych.
    /// </summary>
    /// <remarks>
    /// Stałe „następne" dawałoby zadanie z datą, które nie jest zaplanowane — stan,
    /// którego N8 zabrania, i który znaczy, że data przestaje cokolwiek znaczyć.
    /// </remarks>
    [Fact]
    public void Otwarcie_na_nowo_z_dniem_wraca_do_zaplanowanych()
    {
        var task = Wrzut();
        task.Schedule(Obszar, new DateOnly(2026, 9, 17), Stamp(2000));
        task.Complete(Teraz, Stamp(3000));

        task.Reopen(Stamp(4000));

        task.State.Should().Be(TaskState.Scheduled);
        task.DoDate.Should().Be(new DateOnly(2026, 9, 17));
        task.CompletedAt.Should().BeNull();
    }

    /// <summary>
    /// Przesunięcie dnia nie rusza stanu — inaczej przeciągnięcie wskrzeszałoby zadanie.
    /// </summary>
    [Fact]
    public void Przesuniecie_dnia_nie_zdejmuje_ptaszka()
    {
        var task = Wrzut();
        task.Schedule(Obszar, new DateOnly(2026, 9, 17), Stamp(2000));
        task.Complete(Teraz, Stamp(3000));

        task.MoveDoDate(new DateOnly(2026, 9, 18), Stamp(4000));

        task.State.Should().Be(TaskState.Done);
        task.DoDate.Should().Be(new DateOnly(2026, 9, 18));
        task.CompletedAt.Should().Be(Teraz);
    }

    [Fact]
    public void Otwarcie_na_nowo_niewykonanego_jest_odrzucane()
    {
        var task = Wrzut();

        var otworz = () => task.Reopen(Stamp(2000));

        otworz.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Kosz_to_stan_a_nie_usuniecie_rekordu()
    {
        var task = Wrzut();

        task.Trash(Stamp(2000));

        task.State.Should().Be(TaskState.Trashed);
        task.Deleted.Should().BeFalse();
    }

    [Fact]
    public void Powrot_do_skrzynki_cofa_przetworzenie()
    {
        var task = Wrzut();
        task.Schedule(Obszar, new DateOnly(2026, 9, 21), Stamp(2000));

        task.ReturnToInbox(Stamp(3000));

        task.State.Should().Be(TaskState.Inbox);
        task.DoDate.Should().BeNull();
    }

    [Fact]
    public void Zadanie_nie_moze_byc_wlasnym_podzadaniem()
    {
        var task = Wrzut();

        var samo = () => task.Reparent(task.Id, Stamp(2000));

        samo.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Kazde_przejscie_podnosi_znacznik_zmiany()
    {
        var task = Wrzut();
        var before = task.UpdatedAt;

        task.MakeNext(Obszar, Stamp(2000));

        task.UpdatedAt.Should().BeGreaterThan(before);
    }
}
