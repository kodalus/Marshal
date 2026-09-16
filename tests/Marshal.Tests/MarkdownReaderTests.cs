using FluentAssertions;
using Marshal.Application.Notes;
using Marshal.Infrastructure.Notes;
using Xunit;

namespace Marshal.Tests;

/// <summary>
/// Spłaszczanie drzewa Markdown do bloków. Rozbiór robi Markdig — tutaj sprawdzamy
/// wyłącznie to, co dokładamy sami, bo tylko to możemy popsuć.
/// </summary>
public sealed class MarkdownReaderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void Pusta_tresc_daje_pusty_dokument(string? tekst)
    {
        MarkdownReader.Read(tekst).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Akapit_wraca_jako_akapit()
    {
        var dokument = MarkdownReader.Read("Zwykły tekst notatki.");

        dokument.Blocks.Should().ContainSingle();
        dokument.Blocks[0].Kind.Should().Be(MarkdownBlockKind.Paragraph);
        dokument.Blocks[0].PlainText.Should().Be("Zwykły tekst notatki.");
    }

    [Theory]
    [InlineData("# Wizja", 1)]
    [InlineData("## Zasady", 2)]
    [InlineData("### Szczegół", 3)]
    public void Naglowek_zachowuje_poziom(string tekst, int poziom)
    {
        var blok = MarkdownReader.Read(tekst).Blocks.Single();

        blok.Kind.Should().Be(MarkdownBlockKind.Heading);
        blok.Level.Should().Be(poziom);
    }

    [Fact]
    public void Naglowki_malejacego_poziomu_maja_malejacy_rozmiar()
    {
        var dokument = MarkdownReader.Read("# Raz\n\n## Dwa\n\n### Trzy");

        dokument.Blocks.Select(b => b.FontSize).Should().BeInDescendingOrder();
    }

    [Fact]
    public void Lista_punktowana_daje_punkty()
    {
        var dokument = MarkdownReader.Read("- pierwsze\n- drugie\n- trzecie");

        dokument.Blocks.Should().HaveCount(3);
        dokument.Blocks.Should().AllSatisfy(b => b.Kind.Should().Be(MarkdownBlockKind.Bullet));
        dokument.Blocks.Select(b => b.PlainText).Should().Equal("pierwsze", "drugie", "trzecie");
    }

    [Fact]
    public void Lista_numerowana_jest_odrozniana_od_punktowanej()
    {
        MarkdownReader.Read("1. pierwsze\n2. drugie").Blocks
            .Should().AllSatisfy(b => b.Kind.Should().Be(MarkdownBlockKind.Numbered));
    }

    [Fact]
    public void Zagniezdzona_lista_ma_wieksze_wciecie()
    {
        var dokument = MarkdownReader.Read("- pierwsze\n    - zagnieżdżone");

        var zewnetrzne = dokument.Blocks.Single(b => b.PlainText == "pierwsze");
        var wewnetrzne = dokument.Blocks.Single(b => b.PlainText == "zagnieżdżone");

        wewnetrzne.Indent.Should().BeGreaterThan(zewnetrzne.Indent);
    }

    [Fact]
    public void Pogrubienie_i_kursywa_sa_rozrozniane()
    {
        var blok = MarkdownReader.Read("zwykły **gruby** i *pochyły*").Blocks.Single();

        blok.Spans.Should().Contain(s => s.Text == "gruby" && s.Bold);
        blok.Spans.Should().Contain(s => s.Text == "pochyły" && s.Italic);
        blok.Spans.Should().Contain(s => s.Text.StartsWith("zwykły", StringComparison.Ordinal) && !s.Bold);
    }

    [Fact]
    public void Odsylacz_niesie_adres()
    {
        var blok = MarkdownReader.Read("zobacz [tutaj](https://example.test/a)").Blocks.Single();
        var odsylacz = blok.Spans.Single(s => s.IsLink);

        odsylacz.Text.Should().Be("tutaj");
        odsylacz.Link.Should().Be("https://example.test/a");
    }

    [Fact]
    public void Kod_w_wierszu_jest_oznaczony()
    {
        MarkdownReader.Read("weź `wartość` stąd").Blocks.Single()
            .Spans.Should().Contain(s => s.Text == "wartość" && s.Code);
    }

    [Fact]
    public void Blok_kodu_zachowuje_wiersze()
    {
        var blok = MarkdownReader.Read("```\npierwsza\ndruga\n```").Blocks.Single();

        blok.IsCode.Should().BeTrue();
        blok.PlainText.Should().Be("pierwsza\ndruga");
    }

    [Fact]
    public void Cytat_jest_oznaczony()
    {
        MarkdownReader.Read("> Przypomnienie dla siebie").Blocks.Single()
            .IsQuote.Should().BeTrue();
    }

    [Fact]
    public void Jedno_zdanie_nie_rozpada_sie_na_kilkanascie_napisow()
    {
        // Markdig dzieli tekst tam, gdzie jemu wygodnie. Bez sklejania każdy kawałek
        // byłby osobną kontrolką do narysowania.
        var blok = MarkdownReader.Read("Zwykłe zdanie bez żadnych wyróżnień w środku.")
            .Blocks.Single();

        blok.Spans.Should().ContainSingle();
    }

    [Fact]
    public void Tekst_wokol_wyroznienia_sklada_sie_w_calosc()
    {
        var blok = MarkdownReader.Read("przed **środek** po").Blocks.Single();

        blok.Spans.Should().HaveCount(3);
        blok.PlainText.Should().Be("przed środek po");
    }

    [Fact]
    public void Dluzsza_notatka_zachowuje_kolejnosc_blokow()
    {
        var dokument = MarkdownReader.Read(
            "# Wizja\n\nChcę, żeby dni miały kształt.\n\n- spokój rano\n- praca w blokach\n\n> Bez pośpiechu.");

        dokument.Blocks.Select(b => b.Kind).Should().Equal(
            MarkdownBlockKind.Heading,
            MarkdownBlockKind.Paragraph,
            MarkdownBlockKind.Bullet,
            MarkdownBlockKind.Bullet,
            MarkdownBlockKind.Quote);
    }
}
