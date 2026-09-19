using FluentAssertions;
using Marshal.Domain.Primitives;
using Marshal.Domain.Tasks;
using Xunit;

namespace Marshal.Tests;

public class TaskItemTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(2));
    private static readonly Guid NewArea = Guid.CreateVersion7();

    private static Hlc Stamp(long ms = 1000) => new(ms, 0, "a");

    private static TaskItem NewCapture(string title = "Zadzwonić do przychodni") =>
        TaskItem.Capture(title, Now, Stamp());

    [Fact]
    public void Wrzut_ma_tylko_tytul_i_laduje_w_skrzynce()
    {
        var task = NewCapture();

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
        NewCapture("  kupić mleko  ").Title.Should().Be("kupić mleko");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Wrzut_bez_tresci_jest_odrzucany(string title)
    {
        var wrzuc = () => NewCapture(title);

        wrzuc.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Przetworzenie_na_nastepna_akcje_wymaga_obszaru()
    {
        var task = NewCapture();

        var withoutArea = () => task.MakeNext(Guid.Empty, Stamp(2000));

        withoutArea.Should().Throw<ArgumentException>();
        task.State.Should().Be(TaskState.Inbox);
    }

    [Fact]
    public void Nastepna_akcja_dostaje_obszar_i_opuszcza_skrzynke()
    {
        var task = NewCapture();

        task.MakeNext(NewArea, Stamp(2000));

        task.State.Should().Be(TaskState.Next);
        task.AreaId.Should().Be(NewArea);
    }

    [Fact]
    public void Zaplanowanie_zapisuje_dzien_wykonania()
    {
        var task = NewCapture();
        var day = new DateOnly(2026, 9, 21);

        task.Schedule(NewArea, day, Stamp(2000));

        task.State.Should().Be(TaskState.Scheduled);
        task.DoDate.Should().Be(day);
    }

    [Fact]
    public void Oddelegowanie_wymaga_wskazania_osoby()
    {
        var task = NewCapture();

        var withoutPerson = () => task.Delegate(NewArea, "  ", new DateOnly(2026, 9, 16), null, Stamp(2000));

        withoutPerson.Should().Throw<ArgumentException>();
        task.State.Should().Be(TaskState.Inbox);
    }

    [Fact]
    public void Oddelegowanie_zapisuje_kogo_i_od_kiedy()
    {
        var task = NewCapture();
        var od = new DateOnly(2026, 9, 16);

        task.Delegate(NewArea, " urząd miasta ", od, nudgeDays: 21, Stamp(2000));

        task.State.Should().Be(TaskState.Waiting);
        task.WaitingForWho.Should().Be("urząd miasta");
        task.WaitingSince.Should().Be(od);
        task.WaitingNudgeDays.Should().Be(21);
    }

    [Fact]
    public void Prog_ponaglenia_musi_byc_dodatni()
    {
        var task = NewCapture();

        var zeroDays = () => task.Delegate(NewArea, "ktoś", new DateOnly(2026, 9, 16), 0, Stamp(2000));

        zeroDays.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Wyjscie_z_oczekiwania_czysci_dane_oczekiwania()
    {
        var task = NewCapture();
        task.Delegate(NewArea, "ktoś", new DateOnly(2026, 9, 16), 7, Stamp(2000));

        task.MakeNext(NewArea, Stamp(3000));

        task.WaitingForWho.Should().BeNull();
        task.WaitingSince.Should().BeNull();
        task.WaitingNudgeDays.Should().BeNull();
    }

    [Fact]
    public void Kiedys_moze_moze_miec_date_powrotu()
    {
        var task = NewCapture();
        var back = new DateOnly(2027, 1, 1);

        task.Postpone(NewArea, back, Stamp(2000));

        task.State.Should().Be(TaskState.Someday);
        task.DeferUntil.Should().Be(back);
    }

    [Fact]
    public void Wykonanie_zapisuje_moment()
    {
        var task = NewCapture();
        task.MakeNext(NewArea, Stamp(2000));

        task.Complete(Now, Stamp(3000));

        task.State.Should().Be(TaskState.Done);
        task.CompletedAt.Should().Be(Now);
    }

    [Fact]
    public void Otwarcie_na_nowo_wraca_do_nastepnych_gdy_obszar_jest_znany()
    {
        var task = NewCapture();
        task.MakeNext(NewArea, Stamp(2000));
        task.Complete(Now, Stamp(3000));

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
        var task = NewCapture();
        task.Schedule(NewArea, new DateOnly(2026, 9, 17), Stamp(2000));
        task.Complete(Now, Stamp(3000));

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
        var task = NewCapture();
        task.Schedule(NewArea, new DateOnly(2026, 9, 17), Stamp(2000));
        task.Complete(Now, Stamp(3000));

        task.MoveDoDate(new DateOnly(2026, 9, 18), Stamp(4000));

        task.State.Should().Be(TaskState.Done);
        task.DoDate.Should().Be(new DateOnly(2026, 9, 18));
        task.CompletedAt.Should().Be(Now);
    }

    [Fact]
    public void Otwarcie_na_nowo_niewykonanego_jest_odrzucane()
    {
        var task = NewCapture();

        var open = () => task.Reopen(Stamp(2000));

        open.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Kosz_to_stan_a_nie_usuniecie_rekordu()
    {
        var task = NewCapture();

        task.Trash(Stamp(2000));

        task.State.Should().Be(TaskState.Trashed);
        task.Deleted.Should().BeFalse();
    }

    [Fact]
    public void Powrot_do_skrzynki_cofa_przetworzenie()
    {
        var task = NewCapture();
        task.Schedule(NewArea, new DateOnly(2026, 9, 21), Stamp(2000));

        task.ReturnToInbox(Stamp(3000));

        task.State.Should().Be(TaskState.Inbox);
        task.DoDate.Should().BeNull();
    }

    [Fact]
    public void Zadanie_nie_moze_byc_wlasnym_podzadaniem()
    {
        var task = NewCapture();

        var samo = () => task.Reparent(task.Id, Stamp(2000));

        samo.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Kazde_przejscie_podnosi_znacznik_zmiany()
    {
        var task = NewCapture();
        var before = task.UpdatedAt;

        task.MakeNext(NewArea, Stamp(2000));

        task.UpdatedAt.Should().BeGreaterThan(before);
    }
}
