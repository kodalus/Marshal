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
/// Przez bramę idą <b>także odczyty</b>. Początkowo obejmowała tylko zapisy, bo odczyt
/// trwa milisekundy i nie zmienia stanu śledzenia — ale kontekstowi jest wszystko jedno,
/// co robi: dwie czynności naraz to dwie czynności naraz, a objawem był błąd o drugiej
/// operacji zaczętej przed końcem pierwszej, wyskakujący przy zapisie zadania na
/// telefonie. Brama pilnująca połowy dróg nie jest bramą — jest tylko rzadszym zderzeniem.
/// </para>
/// <para>
/// Poza bramą zostają trzy miejsca sięgające po kontekst <b>synchronicznie</b>:
/// ustawienia urządzenia, jego identyfikator i zapamiętany stan zegara logicznego.
/// Czekanie na semafor z wątku okna zawiesiłoby okno, a zapamiętany stan zegara i tak
/// czytany jest wyłącznie ze środka synchronizacji, czyli spod bramy. Ustawienia
/// i identyfikator wczytują się raz i zostają w pamięci.
/// </para>
/// </remarks>
public interface IKolejkaBazy
{
    Task WykonajAsync(Func<Task> praca, CancellationToken ct = default);

    Task<T> WykonajAsync<T>(Func<Task<T>> praca, CancellationToken ct = default);
}
