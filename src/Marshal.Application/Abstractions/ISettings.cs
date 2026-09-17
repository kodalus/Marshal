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

    /// <summary>
    /// Dlaczego strefa jest inna, niż zapisano. Puste, gdy jest ta zapisana.
    /// </summary>
    /// <remarks>
    /// Strefa nie do rozpoznania na tym systemie kończyła się po cichu przejściem na
    /// czas uniwersalny. Objaw: **wszystkie** godziny w kalendarzu przesunięte o dwie
    /// (zimą o jedną) i kreska bieżącej godziny w złym miejscu, bez niczego na ekranie,
    /// co by na to wskazywało. Przesunięte wszystko naraz wygląda dokładnie tak samo
    /// jak źle pobrane dane — a to dwie zupełnie różne rzeczy do zrobienia.
    /// </remarks>
    string? ZoneProblem { get; }

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

    /// <summary>
    /// Czy przy logowaniu prosić także o odczyt kalendarza Google.
    /// </summary>
    /// <remarks>
    /// Osobno i domyślnie wyłączone, bo te dwa uprawnienia **różnią się ceną**.
    /// <c>drive.file</c> nie jest wrażliwe, więc aplikację z samą synchronizacją da się
    /// opublikować bez weryfikacji i żeton nie wygasa. <c>calendar.readonly</c> jest
    /// wrażliwe: z nim publikacja wymagałaby przeglądu Google, czyli w praktyce trzeba
    /// zostać w trybie testowym i logować się raz w tygodniu. Wciągnięcie kalendarza
    /// na siłę do jednej zgody zabierałoby tę decyzję bez pytania.
    /// </remarks>
    bool GoogleCalendarEnabled { get; }

    /// <summary>
    /// Kalendarz, w którym domyślnie lądują zadania z godziną. Puste = w żadnym.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zadanie zapisane w Marshalu ma być widoczne na telefonie i w kalendarzu, do
    /// którego zagląda się tak czy owak. Bez tego każde trzeba było udostępniać ręcznie,
    /// jedno po drugim — a synchronizacja, która wymaga pamiętania o niej przy każdym
    /// zadaniu, nie jest synchronizacją.
    /// </para>
    /// <para>
    /// Zadanie stoi w **jednym** kalendarzu naraz. Przeniesienie go do innego —
    /// na przykład wspólnego z drugą osobą — zabiera je z głównego, a cofnięcie wraca
    /// tam z powrotem. Stanie w dwóch naraz znaczyłoby dwa wpisy na jedną rzecz
    /// w jednym widoku telefonu.
    /// </para>
    /// </remarks>
    Guid? MainCalendarId { get; }

    void SetMainCalendar(Guid? calendarId);

    void SetGoogleCalendarEnabled(bool enabled);

    void SetZone(string id);

    void SetTheme(ThemeChoice theme);

    void SetGoogle(string? clientId, string? clientSecret);
}
