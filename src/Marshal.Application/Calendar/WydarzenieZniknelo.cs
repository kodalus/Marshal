namespace Marshal.Application.Calendar;

/// <summary>
/// Wydarzenia nie ma już po drugiej stronie.
/// </summary>
/// <remarks>
/// <para>
/// Skasowane w Google — w telefonie, w przeglądarce, przez kogoś, z kim kalendarz jest
/// dzielony. Nasza kopia jest wtedy duchem: widać ją na siatce, bo pobraliśmy ją, gdy
/// jeszcze istniała, a każda próba zapisu wraca odmową.
/// </para>
/// <para>
/// Osobny rodzaj wyjątku, a nie komunikat Google przepisany na ekran. „HttpStatusCode
/// is Gone. Resource has been deleted" jest prawdziwe i bezużyteczne: nie mówi,
/// czego dotyczy, i nie mówi, co z tym zrobić. A zrobić trzeba jedną rzecz i da się ją
/// zrobić samemu — zdjąć ducha z siatki.
/// </para>
/// </remarks>
public sealed class WydarzenieZniknelo(string? komunikat = null)
    : InvalidOperationException(komunikat ?? "Tego wydarzenia nie ma już w kalendarzu Google.");
