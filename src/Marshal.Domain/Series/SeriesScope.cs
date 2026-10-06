namespace Marshal.Domain.Series;

/// <summary>
/// Znacznik „to seria przepisuje swoje dni, a nie człowiek je zmienia".
/// </summary>
/// <remarks>
/// <para>
/// Wystąpienie postawione z okna serii nie jedzie synchronizacją, bo drugie urządzenie
/// policzy je sobie samo z serii i z dnia. Ta zasada jest prawdziwa dokładnie tak długo,
/// jak długo wiersz jest tym, co okno wylicza — a przestaje nim być w chwili, w której
/// ktoś go dotknie. Zmiana wiersza pominięta w dzienniku nie dochodzi nigdzie i nie
/// daje żadnego objawu u siebie: na drugim urządzeniu po prostu nigdy się nie zdarzyła.
/// </para>
/// <para>
/// Stąd pytanie zadawane w przechwytywaczu zapisu: wiersz <b>dopisany</b> wolno pominąć,
/// wiersz <b>zmieniony</b> — nie. Jest jeden wyjątek i to jest ten znacznik: przepisanie
/// wystąpień z szablonu po zmianie serii. Wtedy zmianę niesie sama seria, która jedzie
/// synchronizacją normalnie, a drugie urządzenie przepisze swoje dni z niej samo.
/// </para>
/// <para>
/// Zasięg lokalny dla przepływu asynchronicznego, nie globalny — tak jak przy scalaniu
/// zmian zdalnych i z tego samego powodu: zwykły zapis z interfejsu, wykonywany w tej
/// samej chwili, nie ma stracić swojego wpisu w dzienniku.
/// </para>
/// </remarks>
public static class SeriesScope
{
    private static readonly AsyncLocal<bool> Rewriting = new();

    public static bool IsRestamping => Rewriting.Value;

    public static IDisposable Begin()
    {
        Rewriting.Value = true;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => Rewriting.Value = false;
    }
}
