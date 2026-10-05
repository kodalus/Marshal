using Marshal.Domain.Recurrence;

namespace Marshal.Domain.Series;

/// <summary>
/// Tożsamość serii powstającej z przeniesienia starego rytmu — <b>wyliczana, nie losowana</b>.
/// </summary>
/// <remarks>
/// <para>
/// Przeniesienie rytmów do serii odbywa się <b>na każdym urządzeniu osobno</b>, bo jest
/// powtarzalne bez skutków ubocznych i nie wymaga umawiania się, które ma to zrobić.
/// Przy losowanej tożsamości ta sama zaleta stawała się wadą: telefon i pulpit zakładały
/// dla <b>tego samego</b> starego rytmu dwie różne serie, każda liczyła własne okno
/// z własnych identyfikatorów, i rytm pojawiał się dwa razy. Ten sam błąd, przed którym
/// miała chronić cała przebudowa, tylko o warstwę wyżej — o tożsamość serii, nie
/// wystąpienia.
/// </para>
/// <para>
/// Liczona z zadania, które regułę niosło: jest ono jedno i oba urządzenia je widzą.
/// Dzień zaczepienia do tego <b>nie służy</b> — stare przejście dnia przesuwało datę
/// wykonania na dzisiejszą, więc urządzenia otwarte w różnych dniach miałyby różne daty
/// i znów różne tożsamości.
/// </para>
/// </remarks>
public static class SeriesId
{
    /// <summary>
    /// Dzień-zaczepienie dla skrótu. Data, w której nie wypada żadne wystąpienie, więc
    /// tożsamość serii nie może zderzyć się z tożsamością któregoś z jej dni.
    /// </summary>
    private static readonly DateOnly Salt = DateOnly.MinValue;

    /// <summary>Tożsamość serii przeniesionej z rytmu, który niosło to zadanie.</summary>
    public static Guid Of(Guid carrier) => OccurrenceId.For(carrier, Salt);
}
