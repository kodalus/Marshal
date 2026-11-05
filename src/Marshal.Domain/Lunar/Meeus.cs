namespace Marshal.Domain.Lunar;

/// <summary>
/// Dwa zjawiska nieba, z których składa się kalendarz księżycowy: nów i przesilenie
/// zimowe. Rachunki z Meeusa, <i>Astronomical Algorithms</i>, rozdziały 49 i 27.
/// </summary>
/// <remarks>
/// <para>
/// Cały moduł księżycowy sprowadza się do jednego pytania: <b>kiedy dokładnie</b>
/// wypada nów. Dzień miesiąca księżycowego to data kalendarzowa tej chwili w strefie
/// użytkownika — a to znaczy, że nów o 23:50 i nów o 00:10 dają różne daty, choć dzieli
/// je dwadzieścia minut. Dlatego nie wystarczy tu żaden średni okres synodyczny:
/// prawdziwe nowie chodzą wokół średniej w zakresie ponad pół doby, więc rachunek
/// „co 29,53 dnia” myliłby się o cały dzień kilka razy w roku.
/// </para>
/// <para>
/// <b>Czas dynamiczny, nie uniwersalny.</b> Meeus oddaje chwilę w czasie ziemskim (TT) —
/// jednostajnej skali, w której liczy się ruch ciał. Zegary chodzą w czasie uniwersalnym,
/// a Ziemia obraca się nierówno, więc różnica (ΔT) nie jest zerem: dziś około 69 sekund,
/// około 1900 roku prawie zero, pod koniec wieku prawdopodobnie ponad 200. To więcej niż
/// minuta, o którą tu chodzi, i to właśnie ta różnica rozstrzyga przypadki tuż przy
/// północy. Dlatego <see cref="Utc"/> odejmuje ΔT, a nie zakłada, że TT to UTC.
/// </para>
/// <para>
/// Wzory na ΔT są wielomianowe (Espenak–Meeus). Do około 2050 roku są dopasowaniem do
/// pomiarów, dalej już ekstrapolacją — za sto lat ΔT nie jest znane nikomu, bo zależy od
/// tego, jak Ziemia będzie się obracać. Przyjęta tu dokładność — minuta — mieści tę
/// niepewność aż do końca obsługiwanego zakresu.
/// </para>
/// </remarks>
internal static class Meeus
{
    /// <summary>Południe 1 stycznia 2000 — chwila, której odpowiada dzień juliański 2451545,0.</summary>
    private static readonly DateTime Epoch = new(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Dzień juliański tej samej chwili, licząc od <see cref="Epoch"/>.</summary>
    /// <remarks>
    /// Zwykły rachunek Meeusa (rozdział 7) przelicza składowe daty; tutaj wystarczy
    /// różnica względem jednej znanej chwili, bo <see cref="DateTime"/> i tak liczy czas
    /// od swojego zera. Mniej miejsc, w których można się pomylić, a wynik jest
    /// dokładniejszy: przy wielkości rzędu 2,45 miliona dni liczba podwójnej precyzji
    /// rozróżnia jeszcze setne części milisekundy.
    /// </remarks>
    internal static double JulianDay(DateTime utc) => 2451545.0 + (utc - Epoch).TotalDays;

    /// <summary>Chwila odpowiadająca dniu juliańskiemu <paramref name="jd"/>.</summary>
    internal static DateTime Moment(double jd) => Epoch.AddDays(jd - 2451545.0);

    /// <summary>
    /// Nów o numerze <paramref name="k"/> — w czasie uniwersalnym.
    /// </summary>
    /// <remarks>
    /// Numeracja Meeusa: <c>k = 0</c> to nów z 6 stycznia 2000, liczby ujemne cofają
    /// wstecz. Ułamek 0,5 daje pełnię, 0,25 i 0,75 kwadry — tutaj używamy tylko nowiu
    /// i pełni.
    /// </remarks>
    internal static DateTime NewMoonUtc(double k) => Utc(Phase(k));

    /// <summary>Pełnia następująca po nowiu <paramref name="k"/> — w czasie uniwersalnym.</summary>
    internal static DateTime FullMoonUtc(double k) => Utc(Phase(k + 0.5));

    /// <summary>
    /// Przesilenie zimowe roku <paramref name="year"/> — w czasie uniwersalnym.
    /// </summary>
    /// <remarks>
    /// Rozdział 27 Meeusa, wariant dla lat 1000–3000. Wypada między 20 a 23 grudnia;
    /// to ta chwila wyznacza początek roku księżycowego — pierwszy nów <b>po</b> niej.
    /// </remarks>
    internal static DateTime WinterSolsticeUtc(int year) => Utc(Solstice(year));

    /// <summary>Chwila faz w czasie ziemskim, jako dzień juliański (JDE).</summary>
    private static double Phase(double k)
    {
        var t = k / 1236.85;

        // Średnia faza: tyle wyszłoby, gdyby Księżyc chodził równo.
        var jde = 2451550.09766
            + (29.530588861 * k)
            + (0.00015437 * t * t)
            - (0.000000150 * t * t * t)
            + (0.00000000073 * t * t * t * t);

        // Mimośród orbity Ziemi — maleje, więc poprawki zależne od Słońca słabną.
        var e = 1 - (0.002516 * t) - (0.0000074 * t * t);

        // Anomalia średnia Słońca.
        var sun = Radians(2.5534
            + (29.10535670 * k)
            - (0.0000014 * t * t)
            - (0.00000011 * t * t * t));

        // Anomalia średnia Księżyca.
        var moon = Radians(201.5643
            + (385.81693528 * k)
            + (0.0107582 * t * t)
            + (0.00001238 * t * t * t)
            - (0.000000058 * t * t * t * t));

        // Argument szerokości Księżyca.
        var latitude = Radians(160.7108
            + (390.67050284 * k)
            - (0.0016118 * t * t)
            - (0.00000227 * t * t * t)
            + (0.000000011 * t * t * t * t));

        // Długość wstępującego węzła orbity Księżyca.
        var node = Radians(124.7746
            - (1.56375588 * k)
            + (0.0020672 * t * t)
            + (0.00000215 * t * t * t));

        // Czy nów, czy pełnia. Tablice poprawek różnią się tylko pierwszymi
        // współczynnikami, ale różnią się — nie można wziąć jednej na oba.
        var full = Math.Abs(k - Math.Floor(k) - 0.5) < 0.01;

        jde += (full ? -0.40614 : -0.40720) * Math.Sin(moon);
        jde += (full ? 0.17302 : 0.17241) * e * Math.Sin(sun);
        jde += (full ? 0.01614 : 0.01608) * Math.Sin(2 * moon);
        jde += (full ? 0.01043 : 0.01039) * Math.Sin(2 * latitude);
        jde += (full ? 0.00734 : 0.00739) * e * Math.Sin(moon - sun);
        jde += (full ? -0.00515 : -0.00514) * e * Math.Sin(moon + sun);
        jde += (full ? 0.00209 : 0.00208) * e * e * Math.Sin(2 * sun);
        jde += -0.00111 * Math.Sin(moon - (2 * latitude));
        jde += -0.00057 * Math.Sin(moon + (2 * latitude));
        jde += 0.00056 * e * Math.Sin((2 * moon) + sun);
        jde += -0.00042 * Math.Sin(3 * moon);
        jde += 0.00042 * e * Math.Sin(sun + (2 * latitude));
        jde += 0.00038 * e * Math.Sin(sun - (2 * latitude));
        jde += -0.00024 * e * Math.Sin((2 * moon) - sun);
        jde += -0.00017 * Math.Sin(node);
        jde += -0.00007 * Math.Sin(moon + (2 * sun));
        jde += 0.00004 * Math.Sin((2 * moon) - (2 * latitude));
        jde += 0.00004 * Math.Sin(3 * sun);
        jde += 0.00003 * Math.Sin(moon + sun - (2 * latitude));
        jde += 0.00003 * Math.Sin((2 * moon) + (2 * latitude));
        jde += -0.00003 * Math.Sin(moon + sun + (2 * latitude));
        jde += 0.00003 * Math.Sin(moon - sun + (2 * latitude));
        jde += -0.00002 * Math.Sin(moon - sun - (2 * latitude));
        jde += -0.00002 * Math.Sin((3 * moon) + sun);
        jde += 0.00002 * Math.Sin(4 * moon);

        // Poprawki planetarne: Wenus i Jowisz przesuwają chwilę fazy o kilkadziesiąt
        // sekund. Bez nich rachunek myli się o tyle, ile tu w ogóle liczymy.
        jde += 0.000325 * Math.Sin(Radians(299.77 + (0.107408 * k) - (0.009173 * t * t)));
        jde += 0.000165 * Math.Sin(Radians(251.88 + (0.016321 * k)));
        jde += 0.000164 * Math.Sin(Radians(251.83 + (26.651886 * k)));
        jde += 0.000126 * Math.Sin(Radians(349.42 + (36.412478 * k)));
        jde += 0.000110 * Math.Sin(Radians(84.66 + (18.206239 * k)));
        jde += 0.000062 * Math.Sin(Radians(141.74 + (53.303771 * k)));
        jde += 0.000060 * Math.Sin(Radians(207.14 + (2.453732 * k)));
        jde += 0.000056 * Math.Sin(Radians(154.84 + (7.306860 * k)));
        jde += 0.000047 * Math.Sin(Radians(34.52 + (27.261239 * k)));
        jde += 0.000042 * Math.Sin(Radians(207.19 + (0.121824 * k)));
        jde += 0.000040 * Math.Sin(Radians(291.34 + (1.844379 * k)));
        jde += 0.000037 * Math.Sin(Radians(161.72 + (24.198154 * k)));
        jde += 0.000035 * Math.Sin(Radians(239.56 + (25.513099 * k)));
        jde += 0.000023 * Math.Sin(Radians(331.55 + (3.592518 * k)));

        return jde;
    }

    /// <summary>Współczynniki szeregu poprawkowego z rozdziału 27: amplituda, faza i tempo.</summary>
    private static readonly (double A, double B, double C)[] SolsticeTerms =
    [
        (485, 324.96, 1934.136),
        (203, 337.23, 32964.467),
        (199, 342.08, 20.186),
        (182, 27.85, 445267.112),
        (156, 73.14, 45036.886),
        (136, 171.52, 22518.443),
        (77, 222.54, 65928.934),
        (74, 296.72, 3034.906),
        (70, 243.58, 9037.513),
        (58, 119.81, 33718.147),
        (52, 297.17, 150.678),
        (50, 21.02, 2281.226),
        (45, 247.54, 29929.562),
        (44, 325.15, 31555.956),
        (29, 60.93, 4443.417),
        (18, 155.12, 67555.328),
        (17, 288.79, 4562.452),
        (16, 198.04, 62894.029),
        (14, 199.76, 31436.921),
        (12, 95.39, 14577.848),
        (12, 287.11, 31931.756),
        (12, 320.81, 34777.259),
        (9, 227.73, 1222.114),
        (8, 15.45, 16859.074),
    ];

    /// <summary>Przesilenie zimowe w czasie ziemskim, jako dzień juliański (JDE).</summary>
    private static double Solstice(int year)
    {
        var y = (year - 2000) / 1000.0;

        // Przybliżenie: samo przesilenie bez nierówności ruchu Ziemi.
        var jde = 2451900.05952
            + (365242.74049 * y)
            - (0.06223 * y * y)
            - (0.00823 * y * y * y)
            + (0.00032 * y * y * y * y);

        var t = (jde - 2451545.0) / 36525.0;

        var s = 0.0;
        foreach (var (a, b, c) in SolsticeTerms)
        {
            s += a * Math.Cos(Radians(b + (c * t)));
        }

        // Dzielnik: ruch Słońca w długości nie jest równomierny, więc ta sama poprawka
        // w długości odpowiada różnej poprawce w czasie.
        var w = Radians((35999.373 * t) - 2.47);
        var pace = 1 + (0.0334 * Math.Cos(w)) + (0.0007 * Math.Cos(2 * w));

        return jde + (0.00001 * s / pace);
    }

    /// <summary>Czas uniwersalny chwili podanej w czasie ziemskim.</summary>
    private static DateTime Utc(double jde) => Moment(jde).AddSeconds(-DeltaT(Moment(jde)));

    /// <summary>
    /// ΔT, czyli o ile czas ziemski wyprzedza uniwersalny — w sekundach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wielomiany Espenaka i Meeusa, przedział po przedziale. Poza rokiem 1900 i poza
    /// 2150 wzór jest wyciągnięty za swój zakres: dla lat przed 1900 ΔT jest i tak bliskie
    /// zera (kilka sekund błędu), a za rokiem 2150 nie wie tego nikt. W obu końcach błąd
    /// zostaje pod minutą, czyli pod przyjętą dokładnością.
    /// </para>
    /// <para>
    /// Dla lat po 2005 wzór jest przewidywaniem sprzed dwóch dekad i zawyża ΔT o około
    /// pięć sekund. Zostaje, bo pochodzi ze źródła, które można sprawdzić — a pięć sekund
    /// mieści się w minucie.
    /// </para>
    /// </remarks>
    private static double DeltaT(DateTime moment)
    {
        // Rok ułamkowy: wzory są dopasowane do środków miesięcy, nie do 1 stycznia.
        var year = moment.Year + ((moment.Month - 0.5) / 12);

        if (year < 1920)
        {
            var t = year - 1900;
            return -2.79
                + (1.494119 * t)
                - (0.0598939 * t * t)
                + (0.0061966 * t * t * t)
                - (0.000197 * t * t * t * t);
        }

        if (year < 1941)
        {
            var t = year - 1920;
            return 21.20 + (0.84493 * t) - (0.076100 * t * t) + (0.0020936 * t * t * t);
        }

        if (year < 1961)
        {
            var t = year - 1950;
            return 29.07 + (0.407 * t) - (t * t / 233) + (t * t * t / 2547);
        }

        if (year < 1986)
        {
            var t = year - 1975;
            return 45.45 + (1.067 * t) - (t * t / 260) - (t * t * t / 718);
        }

        if (year < 2005)
        {
            var t = year - 2000;
            return 63.86
                + (0.3345 * t)
                - (0.060374 * t * t)
                + (0.0017275 * t * t * t)
                + (0.000651814 * t * t * t * t)
                + (0.00002373599 * t * t * t * t * t);
        }

        if (year < 2050)
        {
            var t = year - 2000;
            return 62.92 + (0.32217 * t) + (0.005589 * t * t);
        }

        var century = (Math.Min(year, 2150) - 1820) / 100;
        return -20 + (32 * century * century) - (0.5628 * (2150 - Math.Min(year, 2150)));
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180;
}
