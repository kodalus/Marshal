namespace Marshal.Application.Sync;

/// <summary>
/// Przenoszenie treści załączników (spec 9.2, katalog <c>files/</c>).
/// </summary>
/// <remarks>
/// <para>
/// Osobna droga od dziennika zmian, bo dziennik jest tekstem, a zdjęcie nie. Wrzucenie
/// pliku w wiersz JSON rozsadziłoby format co do zasady — i to nie ze względu na
/// rozmiar, tylko dlatego, że porcja dziennika ma dać się przeczytać notatnikiem.
/// </para>
/// <para>
/// Plik adresowany jest skrótem swojej treści, więc jest **niezmienny**: raz zapisany
/// nigdy się nie zmienia i nigdy nie wymaga scalania. Ta sama własność, na której stoją
/// porcje dziennika, tylko tu wynika wprost z adresowania, a nie z umowy.
/// </para>
/// </remarks>
public interface IFileTransport
{
    Task<bool> ExistsAsync(string sha256, CancellationToken ct = default);

    /// <summary>Wgrywa treść. Plik już obecny zostaje nietknięty — treść jest ta sama.</summary>
    Task PutAsync(string sha256, Stream content, CancellationToken ct = default);

    /// <summary>Pobiera treść albo <c>null</c>, gdy jeszcze nie dotarła.</summary>
    Task<Stream?> OpenAsync(string sha256, CancellationToken ct = default);
}
