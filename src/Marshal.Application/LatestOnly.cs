namespace Marshal.Application;

/// <summary>
/// Odświeżanie sterowane zmianą pola: **jeden przebieg naraz, na końcu zawsze najnowszy**.
/// </summary>
/// <remarks>
/// <para>
/// Bez tego wpisanie „telefon" w pole szukania rusza siedem odczytów — po jednym na
/// znak — i wszystkie trafiają w **ten sam kontekst bazy**, bo kontekst jest pojedynczy
/// na całą aplikację. EF Core nie dopuszcza dwóch operacji naraz na jednym kontekście
/// i rzuca wyjątkiem, a że wołanie idzie z obsługi zdarzenia bez oczekiwania, wyjątek
/// nie ma dokąd trafić: ekran po prostu przestaje się odświeżać.
/// </para>
/// <para>
/// Zamiast kolejkować wszystkie przebiegi zapamiętujemy tylko, że w trakcie coś się
/// zmieniło, i powtarzamy raz po zakończeniu. Wyników pośrednich i tak nikt nie widzi,
/// a zapamiętanie ich wszystkich znaczyłoby siedem odczytów zamiast dwóch.
/// </para>
/// <para>
/// <b>Oczekiwanie kończy się wtedy, gdy odświeżenie naprawdę się wydarzyło</b>, także
/// wtedy, gdy zgłoszenie trafiło w trwający przebieg i zostało z nim złożone. Wcześniej
/// zgłoszenie w trakcie wracało natychmiast, więc <c>await</c> na poleceniu odświeżenia
/// kończył się, zanim cokolwiek się odświeżyło. Dopóki wszystko szło wątkiem okna,
/// a odczyt z bazy kończył się bez oddania sterowania, nie dawało się tego zauważyć —
/// przebieg i tak zdążył przed następną linijką. Odkąd odczyt schodzi na wątek z puli,
/// zaczęło dawać się zauważyć, i to najpierw w teście: polecenie odświeżenia wracało,
/// a lista była jeszcze pusta.
/// </para>
/// <para>
/// Pod zamkiem, choć wołane jest z wątku okna. Zamek jest tani, a bez niego ta klasa
/// obowiązywała tylko tam — i trzeba by o tym pamiętać w każdym nowym miejscu, które
/// po nią sięgnie. Oczekiwania są poza zamkiem, więc nie ma czego blokować.
/// </para>
/// </remarks>
public sealed class LatestOnly
{
    private readonly Lock _zamek = new();

    private bool _biegnie;

    /// <summary>Zgłoszenie, które przyszło w trakcie przebiegu. Jedno na wszystkich.</summary>
    /// <remarks>
    /// Jedno, bo wszyscy czekają na to samo: na przebieg, który zobaczy już ich zmianę.
    /// Siedem znaków w polu szukania to siedem oczekiwań i dwa odczyty.
    /// </remarks>
    private TaskCompletionSource? _czekajacy;

    public async Task RunAsync(Func<Task> praca)
    {
        ArgumentNullException.ThrowIfNull(praca);

        TaskCompletionSource? czekam = null;

        lock (_zamek)
        {
            if (_biegnie)
            {
                // Bez kontynuacji w miejscu zakończenia: całe wznowienie czekających
                // poszłoby wtedy wewnątrz przebiegu, który właśnie kończy pracę.
                _czekajacy ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                czekam = _czekajacy;
            }
            else
            {
                _biegnie = true;
            }
        }

        if (czekam is not null)
        {
            await czekam.Task;
            return;
        }

        try
        {
            await praca();

            while (Nastepny() is { } kolejny)
            {
                try
                {
                    await praca();
                    kolejny.SetResult();
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    kolejny.SetException(e);
                    throw;
                }
            }
        }
        finally
        {
            // Zgłoszenie, które trafiło w ostatnią szczelinę — między sprawdzeniem
            // a wyjściem — zostałoby bez odpowiedzi, a razem z nim wszyscy, którzy
            // na nie czekają. Kończymy je powodzeniem, nie błędem: ich odświeżenie
            // się nie wydarzyło, ale wyjątek na drodze, której nikt nie oczekuje,
            // byłby wyjątkiem nieobserwowanym.
            TaskCompletionSource? zostal;

            lock (_zamek)
            {
                zostal = _czekajacy;
                _czekajacy = null;
                _biegnie = false;
            }

            zostal?.TrySetResult();
        }
    }

    /// <summary>Zgłoszenie do obsłużenia w tym przebiegu. Puste, gdy nie ma na co czekać.</summary>
    private TaskCompletionSource? Nastepny()
    {
        lock (_zamek)
        {
            var kolejny = _czekajacy;
            _czekajacy = null;

            return kolejny;
        }
    }
}
