namespace Marshal.Application.Abstractions;

/// <summary>
/// Znak, że okno właśnie coś zapisało.
/// </summary>
/// <remarks>
/// <para>
/// Po to, żeby synchronizacja mogła ruszyć **po zmianie**, a nie tylko co jakiś czas.
/// Sama zmiana jest jedyną chwilą, w której wiadomo, że jest co wysyłać; wszystko inne
/// to zgadywanie z zegarkiem w ręku.
/// </para>
/// <para>
/// Znak jest podnoszony po udanym zapisie z okna — czyli przez jednostkę pracy, przez
/// którą przechodzi każdy z nich. Zapisy samej synchronizacji idą obok niej i znaku nie
/// podnoszą: inaczej każdy przyjęty odcinek prosiłby o kolejny przebieg i pętla nie
/// miałaby końca.
/// </para>
/// <para>
/// Znak może przyjść z dowolnego wątku — zapis kończy się tam, gdzie skończyła się
/// baza. Odbiorca ma z tego wyciągnąć tyle, ile wolno wyciągnąć bez okna: odłożyć sobie
/// notatkę na później. Wchodzenie stąd na listy widoczne w oknie jest błędem.
/// </para>
/// </remarks>
public interface ISygnalZapisu
{
    /// <summary>Podniesiony po zapisie z okna. Może przyjść spoza wątku okna.</summary>
    event Action? Zapisano;

    /// <summary>Podnosi znak.</summary>
    void Zglos();
}
