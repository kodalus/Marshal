namespace Marshal.Application.Calendar;

/// <summary>
/// Ptaszek przy nazwie wydarzenia w kalendarzu zewnętrznym.
/// </summary>
/// <remarks>
/// <para>
/// Wydarzenie z cudzego kalendarza nie ma gdzie trzymać stanu „zrobione”: Google nie
/// zna takiego pola, a własna kolumna u nas znaczyłaby, że ptaszek widać wyłącznie
/// w Marshalu — czyli w jedynym miejscu, w którym i tak się patrzy najrzadziej.
/// Dlatego stanem jest sama nazwa: znak na jej początku widać w Google, na telefonie
/// i w powiadomieniu, a po odświeżeniu wraca do nas bez żadnego dodatkowego zapisu.
/// </para>
/// <para>
/// Cena jest uczciwa do nazwania: zmieniamy cudzą nazwę wydarzenia. Dlatego znak jest
/// jeden, stoi na początku i daje się zdjąć dokładnie tak samo, jak został postawiony.
/// </para>
/// </remarks>
public static class EventMark
{
    /// <summary>Znak stawiany przed nazwą.</summary>
    public const char Mark = '✓';

    private const string Prefix = "✓ ";

    public static bool IsDone(string? title) =>
        !string.IsNullOrWhiteSpace(title) && title.TrimStart().StartsWith(Mark);

    /// <summary>
    /// Nazwa bez ptaszka.
    /// </summary>
    /// <remarks>
    /// Zdejmuje wszystkie znaki z początku, nie jeden. Dwa razy odhaczone wydarzenie
    /// — na przykład tu i na telefonie — ma wracać do swojej nazwy za jednym kliknięciem,
    /// a nie odsłaniać kolejny ptaszek.
    /// </remarks>
    public static string Strip(string? title)
    {
        var name = (title ?? string.Empty).TrimStart();

        while (name.StartsWith(Mark))
        {
            name = name[1..].TrimStart();
        }

        return name;
    }

    /// <summary>Nazwa z ptaszkiem — dokładnie jednym, niezależnie od tego, co było.</summary>
    public static string Apply(string? title) => Prefix + Strip(title);

    /// <summary>Nazwa po ustawieniu stanu.</summary>
    public static string Set(string? title, bool done) =>
        done ? Apply(title) : Strip(title);
}
