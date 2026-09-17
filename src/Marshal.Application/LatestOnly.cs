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
/// Bez blokad: wszystko dzieje się na wątku interfejsu, a sprawdzenia wypadają między
/// oczekiwaniami, więc dwa przebiegi nigdy nie zaglądają tu jednocześnie. Poza wątkiem
/// interfejsu ta klasa **nie daje żadnej gwarancji** — i nie jest do tego.
/// </para>
/// </remarks>
public sealed class LatestOnly
{
    private bool _biegnie;

    private bool _znowu;

    public async Task RunAsync(Func<Task> praca)
    {
        if (_biegnie)
        {
            _znowu = true;
            return;
        }

        _biegnie = true;

        try
        {
            do
            {
                _znowu = false;
                await praca();
            }
            while (_znowu);
        }
        finally
        {
            _biegnie = false;
        }
    }
}
