using Marshal.Domain.Tasks;

namespace Marshal.UI.ViewModels;

/// <summary>
/// Jeden dzień na liście dni serii.
/// </summary>
/// <remarks>
/// <para>
/// Lista jest po to, żeby po założeniu rytmu było <b>widać, co z niego wyszło</b>.
/// Dotąd nie było tego widać nigdzie: rytm opisywało jedno zdanie, a dni z niego
/// stały rozsypane po kalendarzu, każdy w swoim tygodniu. Pytanie „czy to faktycznie
/// wypada w te dni, o które mi chodziło" wymagało przewinięcia dwóch miesięcy siatki.
/// </para>
/// <para>
/// Razem z nagrobkami i odhaczonymi, bo one są odpowiedzią na to samo pytanie. Dzień
/// odwołany pokazany jako brak wyglądałby na dzień, którego nigdy nie zaplanowano —
/// a to jest różnica między „odwołałam tę środę" i „rytm pominął środę", czyli między
/// własną decyzją i usterką.
/// </para>
/// </remarks>
public sealed record SeriesDayRow(Guid Id, string When, string Mark, bool IsGone, bool IsPast)
{
    private static readonly string[] DayNames =
        ["pon", "wt", "śr", "czw", "pt", "sob", "niedz"];

    public bool HasMark => Mark.Length > 0;

    public static SeriesDayRow From(TaskItem task, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(task);

        var day = task.DoDate;

        // Składane z liczb, nie formatem daty: separator dnia i godziny bierze się
        // w formacie z ustawień systemu, a tu ma być zawsze to samo.
        var hour = task.DoTime is { } at
            ? $", {at.Hour:00}:{at.Minute:00}"
            : string.Empty;

        var when = day is { } date
            ? $"{DayNames[((int)date.DayOfWeek + 6) % 7]} {date.Day:00}.{date.Month:00}{hour}"
            : "bez dnia";

        // Jedno słowo na dzień i wybrane w tej kolejności: najpierw to, co się z dniem
        // stało, potem to, co ktoś z nim zrobił. Dzień odwołany bywa jednocześnie
        // zmieniony z ręki — bo odwołanie jest zmianą z ręki — a napis „zmienione"
        // na dniu, którego nie będzie, mówi o nim najmniej.
        var mark = task.State switch
        {
            TaskState.Trashed => "odwołane",
            TaskState.Done => "odhaczone",
            _ when task.Overridden => "zmienione z ręki",
            _ => string.Empty,
        };

        return new SeriesDayRow(
            task.Id,
            when,
            mark,
            task.State == TaskState.Trashed,
            day is { } was && was < today);
    }
}
