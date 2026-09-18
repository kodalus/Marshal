namespace Marshal.Application.Abstractions;

/// <summary>
/// Brama na bazę: przepuszcza jedną pracę naraz.
/// </summary>
/// <remarks>
/// <para>
/// Kontekst bazy jest w tej aplikacji **pojedynczy na cały proces** i nie jest
/// bezpieczny dla dwóch rzeczy naraz. Dopóki wszystko robiło się z okna, wystarczało
/// to samo z siebie: okno ma jeden wątek i jedną rzecz na raz. Odkąd synchronizacja
/// rusza sama, druga strona pojawiła się naprawdę — i pierwszym objawem był błąd
/// o dwóch instancjach tego samego znacznika pola, wyskakujący przy zapisie zadania,
/// który z zadaniem nie miał nic wspólnego.
/// </para>
/// <para>
/// Brama, a nie wątek: praca zostaje tam, gdzie była, tyle że czeka na swoją kolej.
/// Trzymana jest przez **całość** jednej czynności, łącznie z jej oczekiwaniami —
/// puszczona na czas oczekiwania wpuszczałaby drugą pracę dokładnie w tę szczelinę,
/// którą ma zamykać.
/// </para>
/// <para>
/// Czego brama <b>nie</b> obejmuje: odczytów, które repozytoria robią wprost na
/// kontekście. Zapisy są tu ujęte w całości, a odczyt trwa milisekundy i nie zmienia
/// stanu śledzenia, więc zderzenie jest możliwe, ale rzadkie. Domknięcie tego znaczy
/// przeprowadzenie każdego zapytania przez tę samą bramę — i to jest praca do zrobienia
/// wtedy, gdy okaże się potrzebna, a nie na zapas.
/// </para>
/// </remarks>
public interface IKolejkaBazy
{
    Task WykonajAsync(Func<Task> praca, CancellationToken ct = default);

    Task<T> WykonajAsync<T>(Func<Task<T>> praca, CancellationToken ct = default);
}
