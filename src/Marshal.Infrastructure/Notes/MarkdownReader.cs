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

        var blocks = new List<MarkdownBlock>();
        Flatten(Markdig.Markdown.Parse(markdown), blocks, listLevel: 0);

        return new NoteDocument(blocks);
    }

    private static void Flatten(ContainerBlock container, List<MarkdownBlock> output, int listLevel)
    {
        foreach (var block in container)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    output.Add(Block(MarkdownBlockKind.Heading, heading.Level, heading.Inline));
                    break;

                case ParagraphBlock paragraph:
                    output.Add(Block(MarkdownBlockKind.Paragraph, listLevel, paragraph.Inline));
                    break;

                case QuoteBlock quote:
                    // Cytat rozkładany na akapity oznaczone jako cytat: cytat w cytacie
                    // istnieje w formacie, ale nie w notatce osobistej.
                    var before = output.Count;
                    Flatten(quote, output, listLevel);

                    for (var i = before; i < output.Count; i++)
                    {
                        output[i] = output[i] with { Kind = MarkdownBlockKind.Quote };
                    }

                    break;

                case ListBlock list:
                    foreach (var item in list.OfType<ListItemBlock>())
                    {
                        var start = output.Count;
                        Flatten(item, output, listLevel + 1);

                        // Pierwszy akapit pozycji staje się punktem listy; dalsze zostają
                        // akapitami z tym samym wcięciem, bo tym właśnie są.
                        if (output.Count > start)
                        {
                            output[start] = output[start] with
                            {
                                Kind = list.IsOrdered
                                    ? MarkdownBlockKind.Numbered
                                    : MarkdownBlockKind.Bullet,
                                Level = listLevel + 1,
                            };
                        }
                    }

                    break;

                case CodeBlock code:
                    var content = Lines(code);
                    output.Add(new MarkdownBlock(
                        MarkdownBlockKind.Code,
                        listLevel,
                        [new MarkdownSpan(content, false, false, true, null)],
                        content));
                    break;

                case ContainerBlock nested:
                    Flatten(nested, output, listLevel);
                    break;
            }
        }
    }

    private static MarkdownBlock Block(MarkdownBlockKind kind, int level, ContainerInline? inline)
    {
        var pieces = new List<MarkdownSpan>();
        Collect(inline, pieces, bold: false, italic: false, link: null);

        return new MarkdownBlock(kind, level, pieces, string.Concat(pieces.Select(s => s.Text)));
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
                case LiteralInline text:
                    Append(output, text.Content.ToString(), bold, italic, false, link);
                    break;

                case CodeInline code:
                    Append(output, code.Content ?? string.Empty, bold, italic, true, link);
                    break;

                case EmphasisInline emphasis:
                    // Dwa znaki to pogrubienie, jeden kursywa — tak mówi format
                    // i tak to widać w każdym edytorze.
                    Collect(
                        emphasis,
                        output,
                        bold || emphasis.DelimiterCount >= 2,
                        italic || emphasis.DelimiterCount == 1,
                        link);
                    break;

                case LinkInline link:
                    Collect(link, output, bold, italic, link.Url);
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
            && output[^1] is var last
            && last.Bold == bold
            && last.Italic == italic
            && last.Code == code
            && last.Link == link)
        {
            output[^1] = last with { Text = last.Text + text };
            return;
        }

        output.Add(new MarkdownSpan(text, bold, italic, code, link));
    }

    private static string Lines(CodeBlock block)
    {
        var text = new StringBuilder();

        foreach (var row in block.Lines.Lines)
        {
            if (row.Slice.Text is not null)
            {
                text.AppendLine(row.Slice.ToString());
            }
        }

        return text.ToString().TrimEnd('\n', '\r');
    }
}
