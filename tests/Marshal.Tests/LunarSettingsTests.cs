using FluentAssertions;
using Marshal.Application.Abstractions;
using Marshal.Domain.Lunar;
using Marshal.Infrastructure;
using Marshal.Infrastructure.Data;
using Marshal.UI;
using Marshal.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Ekran ustawień kalendarza księżycowego: dwa przełączniki i trzynaście pól.
/// </summary>
/// <remarks>
/// <para>
/// Sprawdzane na <b>pełnym grafie zależności</b>, tym samym, który składa okno — bo
/// połowa tego, co tu może się zepsuć, siedzi nie w rachunku, a w kolejności: wejście
/// na ekran, które zapisuje do bazy wartość, której nikt nie zmieniał, albo wypełnienie
/// pól, które samo wywołuje zapis i gasi kursor w polu, w którym ktoś pisze.
/// </para>
/// <para>
/// Druga rzecz, której pilnują te testy: <b>wyłączenie niczego nie kasuje</b>. Nazwa
/// wpisana ręcznie w trzynaście pól jest tu najdroższą rzeczą, jaką użytkownik może
/// stracić — i jedyną, którą te ustawienia w ogóle mogą zgubić.
/// </para>
/// </remarks>
public sealed class LunarSettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "marshal-luna-" + Guid.NewGuid().ToString("N"));

    private async Task<ServiceProvider> OpenAsync()
    {
        Directory.CreateDirectory(_folder);

        var services = new ServiceCollection()
            .AddMarshal(Path.Combine(_folder, "marshal.db"))
            .AddMarshalViewModels()
            .BuildServiceProvider();

        await DependencyInjection.PrepareAsync(services);

        return services;
    }

    [Fact]
    public async Task Domyslnie_wylaczony_a_przelacznik_nazw_niedostepny()
    {
        using var services = await OpenAsync();
        var screen = services.GetRequiredService<SettingsViewModel>();

        screen.Load();

        screen.LunarShown.Should().BeFalse();
        screen.LunarNamesShown.Should().BeFalse();

        // Niedostępny, nie ukryty: ukryty kazałby się domyślać, że istnieje.
        screen.LunarNamesAvailable.Should().BeFalse();
        screen.LunarNamesEditable.Should().BeFalse();
    }

    [Fact]
    public async Task Wejscie_na_ekran_nie_zapisuje_niczego_do_bazy()
    {
        // Wypełnienie pól idzie przez właściwości, a te zapisują. Bez blokady samo
        // otwarcie ustawień zapisywałoby trzy wiersze, których nikt nie dotknął —
        // i od tej chwili „domyślnie wyłączone" znaczyłoby „wyłączone, bo tak wybrano".
        using var services = await OpenAsync();
        var screen = services.GetRequiredService<SettingsViewModel>();
        var db = services.GetRequiredService<MarshalDbContext>();

        screen.Load();

        db.LocalSettings
            .Where(z => z.Key == LocalSettings.LunarKey
                || z.Key == LocalSettings.LunarNamesKey
                || z.Key == LocalSettings.LunarMonthNamesKey)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Nazwy_staja_sie_dostepne_dopiero_po_wlaczeniu_kalendarza()
    {
        using var services = await OpenAsync();
        var screen = services.GetRequiredService<SettingsViewModel>();
        screen.Load();

        screen.LunarShown = true;

        screen.LunarNamesAvailable.Should().BeTrue();

        // Dostępny to jeszcze nie włączony — lista pól czeka na drugą decyzję.
        screen.LunarNamesEditable.Should().BeFalse();

        screen.LunarNamesShown = true;

        screen.LunarNamesEditable.Should().BeTrue();
        screen.LunarMonths.Should().HaveCount(LunarNames.Count);
    }

    [Fact]
    public async Task Wpisana_nazwa_przezywa_wylaczenie_i_wlaczenie()
    {
        using var services = await OpenAsync();
        var screen = services.GetRequiredService<SettingsViewModel>();
        var settings = services.GetRequiredService<ISettings>();
        screen.Load();

        screen.LunarShown = true;
        screen.LunarNamesShown = true;
        screen.LunarMonths[0].Name = "Luna Mrozu";

        settings.LunarMonthNames[0].Should().Be("Luna Mrozu", "zapis idzie od razu, bez przycisku");

        screen.LunarShown = false;

        settings.LunarMonthNames[0].Should().Be("Luna Mrozu");
        settings.LunarNamesShown.Should().BeTrue("to osobna decyzja, nie skutek pierwszej");

        // Wyjście z ekranu i powrót — czyli dokładnie to, co robi okno.
        screen.Load();
        screen.LunarShown = true;

        screen.LunarMonths[0].Name.Should().Be("Luna Mrozu");
        screen.LunarNamesShown.Should().BeTrue();
    }

    [Fact]
    public async Task Propozycja_wypelnia_wszystkie_trzynascie_pol()
    {
        using var services = await OpenAsync();
        var screen = services.GetRequiredService<SettingsViewModel>();
        var settings = services.GetRequiredService<ISettings>();
        screen.Load();

        screen.LunarShown = true;
        screen.LunarNamesShown = true;
        screen.LunarMonths[3].Name = "Moja własna";

        screen.PresetLunarNamesCommand.Execute(null);

        // Nadpisuje też wypełnione. Scalanie dałoby listę w połowie swoją, w połowie
        // cudzą, po której nie wiadomo już, co się wpisało samemu.
        screen.LunarMonths.Select(z => z.Name).Should().Equal(LunarNames.Preset);
        settings.LunarMonthNames.Should().Equal(LunarNames.Preset);
    }

    [Fact]
    public async Task Wyczyszczenie_zostawia_numery()
    {
        using var services = await OpenAsync();
        var screen = services.GetRequiredService<SettingsViewModel>();
        var settings = services.GetRequiredService<ISettings>();
        screen.Load();

        screen.LunarShown = true;
        screen.LunarNamesShown = true;
        screen.PresetLunarNamesCommand.Execute(null);

        screen.ClearLunarNamesCommand.Execute(null);

        screen.LunarMonths.Should().HaveCount(LunarNames.Count);
        screen.LunarMonths.Should().OnlyContain(z => z.Name.Length == 0);
        settings.LunarMonthNames.Should().OnlyContain(z => z.Length == 0);

        // Numer zostaje zawsze — podpis pola jest tym, co nazwa zastępuje.
        screen.LunarMonths[0].Label.Should().Be("Luna 1");
        screen.LunarMonths[12].Label.Should().Be("Luna 13");
        screen.LunarMonths[12].IsThirteenth.Should().BeTrue();
    }

    [Fact]
    public async Task Podglad_mowi_co_bedzie_widac()
    {
        using var services = await OpenAsync();
        var screen = services.GetRequiredService<SettingsViewModel>();
        var clock = services.GetRequiredService<IClock>();
        var settings = services.GetRequiredService<ISettings>();
        screen.Load();

        screen.LunarState.Should().Contain("Wyłączony");

        screen.LunarShown = true;

        var today = LunarCalendar.Of(clock.Today, settings.Zone);

        // Bez nazw: numer, bo numer jest zawsze.
        screen.LunarState.Should().Contain($"Luna {today.Month}");
        screen.LunarState.Should().Contain($"dzień {today.Day}");

        screen.LunarNamesShown = true;
        screen.PresetLunarNamesCommand.Execute(null);

        // Z nazwami: nazwa zamiast numeru, ale dzień ten sam.
        screen.LunarState.Should().Contain(LunarNames.Preset[today.Month - 1]);
        screen.LunarState.Should().Contain($"dzień {today.Day}");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Plik bazy bywa jeszcze trzymany przez sterownik. Nieposprzątany katalog
            // tymczasowy nie jest wynikiem testu i nie ma go przewracać.
        }
    }
}
