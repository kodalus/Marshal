namespace Marshal.Application.Notes;

public enum MarkdownBlockKind
{
    Heading,
    Paragraph,
    Bullet,
    Numbered,
    Code,
    Quote,
}

/// <summary>
/// Kawałek tekstu z jednym wyróżnieniem.
/// </summary>
/// <remarks>
/// Wyróżnienia nie zagnieżdżają się, i to jest rozstrzygnięcie. Pogrubiony kursywny
/// odsyłacz jest w notatce osobistej rzeczą, której nie ma — a jego obsługa oznaczałaby
/// drzewo zamiast listy i rysowanie rekurencyjne zamiast jednego wiersza.
/// </remarks>
public sealed record MarkdownSpan(string Text, bool Bold, bool Italic, bool Code, string? Link)
{
    public bool IsLink => Link is not null;
}

public sealed record MarkdownBlock(
    MarkdownBlockKind Kind, int Level, IReadOnlyList<MarkdownSpan> Spans, string PlainText)
{
    public bool IsHeading => Kind == MarkdownBlockKind.Heading;

    public bool IsCode => Kind == MarkdownBlockKind.Code;

    public bool IsQuote => Kind == MarkdownBlockKind.Quote;

    public bool IsListItem => Kind is MarkdownBlockKind.Bullet or MarkdownBlockKind.Numbered;

    /// <summary>Wcięcie zagnieżdżonej listy w punktach.</summary>
    public double Indent => IsListItem ? Level * 18.0 : 0;

    /// <summary>Rozmiar tekstu nagłówka. Poziom pierwszy największy, dalej maleje.</summary>
    public double FontSize => Kind switch
    {
        MarkdownBlockKind.Heading => Level switch { 1 => 22, 2 => 18, 3 => 16, _ => 14 },
        MarkdownBlockKind.Code => 12,
        _ => 14,
    };
}

/// <summary>
/// Notatka rozłożona na bloki do narysowania.
/// </summary>
/// <remarks>
/// <para>
/// Płaska lista, nie drzewo. Podgląd notatki osobistej nie potrzebuje pełnego modelu
/// dokumentu — potrzebuje czegoś, co da się przelecieć jednym <c>ItemsControl</c>,
/// a zagnieżdżenie listy niesie sam poziom wcięcia.
/// </para>
/// <para>
/// Nazwa <c>NoteDocument</c>, nie <c>MarkdownDocument</c>: tak nazywa się typ Markdiga
/// i dwie takie nazwy w jednym pliku znaczą kwalifikowanie każdego użycia. Przy okazji
/// ta jest trafniejsza — to jest postać **notatki** do narysowania, a nie dokument
/// Markdown w ogóle.
/// </para>
/// </remarks>
public sealed record NoteDocument(IReadOnlyList<MarkdownBlock> Blocks)
{
    public static readonly NoteDocument Empty = new([]);

    public bool IsEmpty => Blocks.Count == 0;
}
