namespace Marshal.Infrastructure.Sync;

/// <summary>
/// Znacznik „to jest scalanie, nie zmiana użytkownika".
/// </summary>
/// <remarks>
/// Bez niego synchronizacja wpada w pętlę: zastosowanie zdalnej zmiany przechodzi
/// przez <c>SaveChanges</c>, przechwytywacz zapisuje ją jako zmianę lokalną, wysyłka
/// odsyła ją z powrotem, drugie urządzenie stosuje ponownie i odsyła — i tak bez
/// końca, przy czym **każdy obieg wygląda na poprawny z osobna**.
///
/// Zasięg jest lokalny dla przepływu asynchronicznego, nie globalny, żeby zwykły
/// zapis z interfejsu wykonywany w tym samym czasie co synchronizacja nie stracił
/// swojego wpisu w dzienniku.
/// </remarks>
public static class SyncScope
{
    private static readonly AsyncLocal<bool> Applying = new();

    public static bool IsApplyingRemote => Applying.Value;

    public static IDisposable Begin()
    {
        Applying.Value = true;
        return new Zakres();
    }

    private sealed class Zakres : IDisposable
    {
        public void Dispose() => Applying.Value = false;
    }
}
