namespace Marshal.Application.Abstractions;

/// <summary>
/// Czy to urządzenie zdążyło się dowiedzieć, co zdarzyło się na drugim.
/// </summary>
/// <remarks>
/// <para>
/// Przejście dnia jest <b>powtarzalne bez skutków ubocznych</b> i liczy się z zastanej
/// różnicy między datą zapisaną a dzisiejszą — ale liczy się <b>z tego, co wie to
/// urządzenie</b>. A zaraz po uruchomieniu nie wie jeszcze nic o wczorajszym dniu
/// spędzonym z telefonem w ręku: scalanie dopiero rusza.
/// </para>
/// <para>
/// Skutek, gdy przejście dnia idzie przed scaleniem, widać po dwóch rzeczach, które
/// wyglądają na zupełnie różne usterki, a są tą samą:
/// </para>
/// <list type="bullet">
/// <item>zadanie odhaczone wczoraj na telefonie wraca na pulpicie <b>na dziś</b> —
/// bo lokalnie było jeszcze otwarte i zaległe, więc przejście dnia przeniosło je,
/// zapisując świeższy znacznik niż ten z odhaczenia; scalanie idzie po polach, więc
/// stan wygrywa odhaczenie, a dzień wykonania wygrywa przeniesienie;</item>
/// <item>wystąpienie serii odhaczone wczoraj <b>znika</b> — bo lokalnie wyglądało na
/// przegapione, a dzisiejsze wystąpienie już stało, więc przejście dnia postawiło na
/// nim nagrobek. Nagrobek jest świeższy od odhaczenia i wygrywa.</item>
/// </list>
/// <para>
/// Stąd ta bramka. Dopóki scalanie nie doszło do skutku, przejście dnia nie rusza —
/// bo rusza rzeczy nieodwracalne, a odpowiedź, którą by na nich oparło, jest
/// nieprawdziwa. Urządzenie bez poświadczeń do synchronizacji jest ustalone od razu:
/// nie ma drugiego źródła prawdy, na które mogłoby czekać.
/// </para>
/// </remarks>
public sealed class SyncState
{
    private volatile bool _settled;

    public bool Settled => _settled;

    public void Settle() => _settled = true;
}
