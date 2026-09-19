using Android.App;
using Marshal.UI;

namespace Marshal.Android;

/// <summary>
/// Okienka systemu do wybierania daty i godziny.
/// </summary>
/// <remarks>
/// <para>
/// To są te same okna, które palec zna z każdej innej aplikacji: okrągła tarcza zegara
/// i kalendarz miesiąca. Avalonia rysuje własne wybieraki i na myszy są w porządku,
/// bo da się w nie wpisać — na telefonie trzy kolumny do kręcenia trzeba nauczyć się
/// od nowa w jednej jedynej aplikacji.
/// </para>
/// <para>
/// Wymagają okna, nie kontekstu aplikacji: okno dialogowe musi mieć nad czym stanąć.
/// Stąd przypięcie do okna, a nie ustawienie raz na proces — okno bywa zamykane
/// i zakładane od nowa przy obrocie telefonu, a haczyk wskazujący na poprzednie
/// byłby wskazaniem na coś, czego już nie ma.
/// </para>
/// <para>
/// Zamknięcie okna bez wyboru oddaje wartość, która była. Nie puste: „anuluj" znaczy
/// „nie zmieniaj", a nie „skasuj" — od kasowania jest osobny przycisk obok pola.
/// </para>
/// </remarks>
internal static class NativePickers
{
    public static void Podepnij(Activity window)
    {
        Pickers.Data = now => PokazAsync<DateOnly?>(window, zrobione =>
        {
            var od = now ?? DateOnly.FromDateTime(DateTime.Now);

            var okienko = new DatePickerDialog(
                window,
                (_, e) => zrobione(DateOnly.FromDateTime(e.Date)),
                od.Year,

                // Miesiące liczone od zera — jedyne miejsce w tym pliku, w którym
                // pomyłka o jeden nie rzuca wyjątkiem, tylko wybiera zły miesiąc.
                od.Month - 1,
                od.Day);

            okienko.CancelEvent += (_, _) => zrobione(now);
            okienko.DismissEvent += (_, _) => zrobione(now);
            okienko.Show();
        });

        Pickers.Hour = now => PokazAsync<TimeOnly?>(window, zrobione =>
        {
            var od = now ?? new TimeOnly(9, 0);

            var okienko = new TimePickerDialog(
                window,
                (_, e) => zrobione(new TimeOnly(e.HourOfDay, e.Minute)),
                od.Hour,
                od.Minute,

                // Doba, nie dwunastka z dopiskiem: kalendarz w aplikacji liczy godziny
                // tak samo, a dwa zapisy tej samej godziny to jeden za dużo.
                true);

            okienko.CancelEvent += (_, _) => zrobione(now);
            okienko.DismissEvent += (_, _) => zrobione(now);
            okienko.Show();
        });
    }

    /// <summary>Odpięcie przy zamykaniu okna. Haczyk na nieistniejące okno jest gorszy od pustego.</summary>
    public static void Odepnij()
    {
        Pickers.Data = null;
        Pickers.Hour = null;
    }

    /// <summary>
    /// Okno dialogowe jako zadanie do doczekania.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pokazywane na wątku okna, bo tam mieszkają okna; wołane bywa z obsługi
    /// dotknięcia, która jest tam już i tak, ale to nie jest wiedza, na której wolno
    /// się opierać.
    /// </para>
    /// <para>
    /// Odpowiedź przez „spróbuj ustawić", bo przychodzi dwiema drogami naraz: wybór
    /// i zamknięcie okna to dwa osobne zdarzenia i po wyborze przychodzi jeszcze
    /// zamknięcie. Wygrywa pierwsza.
    /// </para>
    /// </remarks>
    private static Task<T> PokazAsync<T>(Activity window, Action<Action<T>> pokaz)
    {
        var response = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        window.RunOnUiThread(() =>
        {
            try
            {
                pokaz(result => response.TrySetResult(result));
            }
            catch (Exception e)
            {
                response.TrySetException(e);
            }
        });

        return response.Task;
    }
}
