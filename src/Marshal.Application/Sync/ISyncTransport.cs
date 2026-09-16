namespace Marshal.Application.Sync;

/// <summary>Jedna porcja dziennika jednego urządzenia.</summary>
/// <remarks>
/// Nazwa porcji jest liczbą uzupełnioną zerami, więc porządek leksykograficzny
/// pokrywa się z chronologicznym — ta sama sztuczka co przy zegarze logicznym
/// i z tego samego powodu: składnice sortują nazwy jako tekst.
/// </remarks>
public sealed record LogSegment(string DeviceId, string Name);

/// <summary>
/// Składnica porcji dziennika (spec 9.2).
/// </summary>
/// <remarks>
/// <para>
/// Porcja raz zapisana **nigdy się nie zmienia**. To jest warunek całego rozwiązania:
/// każde urządzenie tylko dokłada własne porcje i nigdy nie dotyka cudzych ani
/// swoich wcześniejszych, więc konflikt zapisu nie powstaje.
/// </para>
/// <para>
/// Pierwsza wersja tego interfejsu operowała przesunięciem w bajtach w jednym
/// pliku na urządzenie. Odpadła, bo **Dysk Google nie ma operacji dopisania** —
/// da się wgrać nową wersję całego pliku albo utworzyć nowy. Pobieranie całości
/// i wgrywanie z powrotem przy każdej synchronizacji odbierałoby tę jedną
/// własność, na której wszystko stoi: przerwane wgranie niszczyłoby własny
/// dziennik, zamiast tylko uciąć jego ogon.
/// </para>
/// </remarks>
public interface ISyncTransport
{
    /// <summary>Wszystkie porcje wszystkich urządzeń, w porządku nazw.</summary>
    Task<IReadOnlyList<LogSegment>> ListSegmentsAsync(CancellationToken ct = default);

    Task<string> ReadSegmentAsync(LogSegment segment, CancellationToken ct = default);

    /// <summary>Zapisuje nową porcję własnego urządzenia. Nigdy nie nadpisuje istniejącej.</summary>
    Task WriteSegmentAsync(string deviceId, string name, string content, CancellationToken ct = default);
}
