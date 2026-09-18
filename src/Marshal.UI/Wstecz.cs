namespace Marshal.UI;

/// <summary>
/// Cofnięcie zgłoszone przez system — na Androidzie przycisk albo gest wstecz.
/// </summary>
/// <remarks>
/// <para>
/// Haczyk w warstwie współdzielonej, tak samo jak przy powiadomieniach systemowych
/// i wybierakach daty: okno wie, <b>co</b> jest otwarte, a nie wie, na czym stoi.
/// Projekt Androida podstawia tu odpowiedź, pulpit zostawia pusto i używa klawisza
/// Escape, bo tam cofnięcia systemowego po prostu nie ma.
/// </para>
/// <para>
/// Zwraca prawdę, gdy okno miało co zamknąć. Fałsz znaczy „nie mam nic do cofnięcia"
/// i wtedy cofnięcie należy do systemu — czyli zwykle zamyka aplikację. Odpowiedź
/// domyślnie twierdząca zamieniłaby przycisk wstecz w przycisk, który nic nie robi,
/// a to jest gorsze od zamknięcia aplikacji: z zamknięcia widać, że coś się stało.
/// </para>
/// </remarks>
public static class Wstecz
{
    /// <summary>Odpowiedź okna na cofnięcie. Pusta, gdy okna jeszcze nie ma.</summary>
    public static Func<bool>? Obsluga { get; set; }

    /// <summary>Czy okno zajęło się cofnięciem.</summary>
    public static bool Zajeto() => Obsluga?.Invoke() ?? false;
}
