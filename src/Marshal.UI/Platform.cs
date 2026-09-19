namespace Marshal.UI;

/// <summary>
/// Czym się w tę aplikację celuje: palcem czy wskaźnikiem.
/// </summary>
/// <remarks>
/// <para>
/// Haczyk w warstwie współdzielonej, w tej samej postaci co <see cref="Back"/>
/// i <see cref="Sleep"/>: projekt platformy podstawia odpowiedź, pulpit zostawia
/// domyślną. Pytanie jest o platformę, a nie o rozmiar okna — i to jest tu całe
/// rozstrzygnięcie.
/// </para>
/// <para>
/// Kusiło, żeby wziąć do tego szerokość okna, bo taka miara już w kalendarzu jest.
/// Byłaby jednak odpowiedzią na inne pytanie: wąskie okno na pulpicie dalej ma mysz,
/// a szeroki tablet dalej ma palec. Rzeczy, które tu rozstrzygamy — czy pokazywać
/// kwadracik wielkości dziesięciu punktów, czy liczyć na najechanie kursorem — zależą
/// od tego, co jest w ręku, a nie od tego, ile jest miejsca.
/// </para>
/// <para>
/// Fałsz domyślnie, czyli „wskaźnik". Pulpit nie musi niczego ustawiać, a platforma
/// dotykowa, która zapomni to zrobić, dostanie zachowanie sprzed tej zmiany — czyli
/// gorsze, ale nie zepsute.
/// </para>
/// </remarks>
public static class Platform
{
    /// <summary>Czy to platforma dotykowa: bez najechania, z palcem zamiast kursora.</summary>
    public static bool Touch { get; set; }
}
