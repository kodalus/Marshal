namespace Marshal.Application.Abstractions;

/// <summary>Motyw okna.</summary>
public enum ThemeChoice
{
    /// <summary>Za systemem. Telefon przełącza się wieczorem sam i to jest dobra domyślna.</summary>
    System,

    Light,

    Dark,
}

/// <summary>
/// Ustawienia tego urządzenia — **niesynchronizowane**.
/// </summary>
/// <remarks>
/// <para>
/// Motyw i strefa nie są decyzjami o danych, tylko o tym, jak to urządzenie ma się
/// zachowywać. Zsynchronizowany motyw znaczyłby, że ciemny włączony wieczorem przy
/// komputerze zapala się rano na telefonie — czyli że ustawienie jednego urządzenia
/// psuje drugie. Strefa tym bardziej: telefon jedzie z Tobą, komputer zostaje.
/// </para>
/// <para>
/// Leżą w tej samej tabeli co identyfikator urządzenia i ostatni znacznik zegara,
/// bo są tego samego rodzaju: należą do sprzętu, a nie do systemu zadań.
/// </para>
/// </remarks>
public interface ISettings
{
    /// <summary>
    /// Strefa, w której liczone są **dni** (spec 3.4).
    /// </summary>
    /// <remarks>
    /// Jawna, nie brana z systemu. To nie jest nadmiar ostrożności: dzień wykonania
    /// i termin są bez strefy, bo „wtorek jest wtorkiem" — ale żeby wiedzieć, który
    /// dzień jest dzisiaj, strefa jest potrzebna. Gdyby brać ją z systemu, wyjazd na
    /// zachód przestawiłby „dzisiaj" o dobę i zadania jutrzejsze zrobiłyby się
    /// dzisiejsze albo odwrotnie — w chwili, w której najmniej chce się to prostować.
    /// </remarks>
    TimeZoneInfo Zone { get; }

    ThemeChoice Theme { get; }

    /// <summary>Identyfikator klienta OAuth z konsoli Google. Pusty, dopóki nie podany.</summary>
    string? GoogleClientId { get; }

    /// <summary>
    /// Tajemnica klienta OAuth.
    /// </summary>
    /// <remarks>
    /// Leży w bazie jawnym tekstem i tak ma być. W aplikacji instalowanej u użytkownika
    /// tajemnica klienta **nie jest tajemnicą** — da się ją wyjąć z pliku programu
    /// i Google o tym wie, dlatego dla tego typu aplikacji nie traktuje jej jako
    /// zabezpieczenia; chroni zgoda w przeglądarce, nie ona. Szyfrowanie jej tutaj
    /// dawałoby poczucie ochrony, której nie ma, a klucz i tak leżałby obok.
    ///
    /// Co z tego wynika naprawdę: to ustawienie jest **lokalne i niesynchronizowane**.
    /// Nie dlatego, że jest tajne, tylko dlatego, że dziennik zmian jest zwykłym tekstem
    /// na Dysku — poświadczenia do Dysku nie mają jechać przez Dysk.
    /// </remarks>
    string? GoogleClientSecret { get; }

    void SetZone(string id);

    void SetTheme(ThemeChoice theme);

    void SetGoogle(string? clientId, string? clientSecret);
}
