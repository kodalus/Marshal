using System.Text;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Marshal.Application.Notes;

namespace Marshal.Infrastructure.Notes;

/// <summary>
/// Markdown na bloki do narysowania (spec 11, ekran „Notatki").
/// </summary>
/// <remarks>
/// <para>
/// Rozbiór robi Markdig. Pisanie własnego parsera Markdown kończy się tym, że po roku
/// obsługuje osiemdziesiąt procent formatu, a każdy kolejny przypadek jest poprawką.
/// Tutaj zostaje wyłącznie spłaszczenie jego drzewa do listy bloków — i to jest ta część,
/// którą da się sprawdzić testem.
/// </para>
/// <para>
/// Obsługiwany podzbiór: nagłówki, akapity, listy punktowane i numerowane, cytaty, kod,
/// pogrubienie, kursywa, odsyłacze. Tabele, obrazy i przypisy nie — notatka osobista ich
/// nie potrzebuje, a każdy z nich to osobny sposób rysowania.
/// </para>
/// </remarks>
public static class MarkdownReader
{
    public static NoteDocument Read(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return NoteDocument.Empty;
        }

        var bloki = new List<MarkdownBlock>();
        Flatten(Markdig.Markdown.Parse(markdown), bloki, listLevel: 0);

        return new NoteDocument(bloki);
    }

    private static void Flatten(ContainerBlock container, List<MarkdownBlock> output, int listLevel)
    {
        foreach (var blok in container)
        {
            switch (blok)
            {
                case HeadingBlock naglowek:
                    output.Add(Block(MarkdownBlockKind.Heading, naglowek.Level, naglowek.Inline));
                    break;

                case ParagraphBlock akapit:
                    output.Add(Block(MarkdownBlockKind.Paragraph, listLevel, akapit.Inline));
                    break;

                case QuoteBlock cytat:
                    // Cytat rozkładany na akapity oznaczone jako cytat: cytat w cytacie
                    // istnieje w formacie, ale nie w notatce osobistej.
                    var przed = output.Count;
                    Flatten(cytat, output, listLevel);

                    for (var i = przed; i < output.Count; i++)
                    {
                        output[i] = output[i] with { Kind = MarkdownBlockKind.Quote };
                    }

                    break;

                case ListBlock lista:
                    foreach (var pozycja in lista.OfType<ListItemBlock>())
                    {
                        var poczatek = output.Count;
                        Flatten(pozycja, output, listLevel + 1);

                        // Pierwszy akapit pozycji staje się punktem listy; dalsze zostają
                        // akapitami z tym samym wcięciem, bo tym właśnie są.
                        if (output.Count > poczatek)
                        {
                            output[poczatek] = output[poczatek] with
                            {
                                Kind = lista.IsOrdered
                                    ? MarkdownBlockKind.Numbered
                                    : MarkdownBlockKind.Bullet,
                                Level = listLevel + 1,
                            };
                        }
                    }

                    break;

                case CodeBlock kod:
                    var tresc = Lines(kod);
                    output.Add(new MarkdownBlock(
                        MarkdownBlockKind.Code,
                        listLevel,
                        [new MarkdownSpan(tresc, false, false, true, null)],
                        tresc));
                    break;

                case ContainerBlock zagniezdzony:
                    Flatten(zagniezdzony, output, listLevel);
                    break;
            }
        }
    }

    private static MarkdownBlock Block(MarkdownBlockKind kind, int level, ContainerInline? inline)
    {
        var kawalki = new List<MarkdownSpan>();
        Collect(inline, kawalki, bold: false, italic: false, link: null);

        return new MarkdownBlock(kind, level, kawalki, string.Concat(kawalki.Select(s => s.Text)));
    }

    private static void Collect(
        ContainerInline? container, List<MarkdownSpan> output, bool bold, bool italic, string? link)
    {
        if (container is null)
        {
            return;
        }

        foreach (var element in container)
        {
            switch (element)
            {
                case LiteralInline tekst:
                    Append(output, tekst.Content.ToString(), bold, italic, false, link);
                    break;

                case CodeInline kod:
                    Append(output, kod.Content ?? string.Empty, bold, italic, true, link);
                    break;

                case EmphasisInline wyroznienie:
                    // Dwa znaki to pogrubienie, jeden kursywa — tak mówi format
                    // i tak to widać w każdym edytorze.
                    Collect(
                        wyroznienie,
                        output,
                        bold || wyroznienie.DelimiterCount >= 2,
                        italic || wyroznienie.DelimiterCount == 1,
                        link);
                    break;

                case LinkInline odsylacz:
                    Collect(odsylacz, output, bold, italic, odsylacz.Url);
                    break;

                case LineBreakInline:
                    Append(output, " ", bold, italic, false, link);
                    break;
            }
        }
    }

    /// <summary>
    /// Doklejanie do poprzedniego kawałka, gdy ma te same wyróżnienia.
    /// </summary>
    /// <remarks>
    /// Markdig dzieli tekst tam, gdzie jemu wygodnie, a nie tam, gdzie zmienia się wygląd.
    /// Bez sklejania jedno zdanie potrafi rozpaść się na kilkanaście osobnych napisów,
    /// a każdy z nich to osobna kontrolka do narysowania.
    /// </remarks>
    private static void Append(
        List<MarkdownSpan> output, string text, bool bold, bool italic, bool code, string? link)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (output.Count > 0
            && output[^1] is var ostatni
            && ostatni.Bold == bold
            && ostatni.Italic == italic
            && ostatni.Code == code
            && ostatni.Link == link)
        {
            output[^1] = ostatni with { Text = ostatni.Text + text };
            return;
        }

        output.Add(new MarkdownSpan(text, bold, italic, code, link));
    }

    private static string Lines(CodeBlock block)
    {
        var tekst = new StringBuilder();

        foreach (var wiersz in block.Lines.Lines)
        {
            if (wiersz.Slice.Text is not null)
            {
                tekst.AppendLine(wiersz.Slice.ToString());
            }
        }

        return tekst.ToString().TrimEnd('\n', '\r');
    }
}
