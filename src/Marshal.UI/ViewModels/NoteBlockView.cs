using Avalonia.Media;
using Marshal.Application.Notes;

namespace Marshal.UI.ViewModels;

/// <summary>
/// Kawałek tekstu gotowy do narysowania.
/// </summary>
/// <remarks>
/// Przeliczenie na typy Avalonii siedzi tutaj, a nie w konwerterze przy powiązaniu.
/// Konwerter to trzecie miejsce do zajrzenia przy czytaniu jednego wiersza XAML-a;
/// rekord widoczny obok modelu jest jednym.
/// </remarks>
public sealed record SpanView(
    string Text, FontWeight Weight, FontStyle Style, bool IsCode, string? Link)
{
    public static SpanView From(MarkdownSpan span) => new(
        span.Text,
        span.Bold ? FontWeight.Bold : FontWeight.Normal,
        span.Italic ? FontStyle.Italic : FontStyle.Normal,
        span.Code,
        span.Link);

    public double Opacity => IsLink ? 0.9 : 1.0;

    public bool IsLink => Link is not null;
}

/// <summary>Blok notatki w podglądzie.</summary>
public sealed record BlockView(
    IReadOnlyList<SpanView> Spans,
    double FontSize,
    FontWeight Weight,
    double Indent,
    bool IsListItem,
    bool IsQuote,
    bool IsCode)
{
    public static BlockView From(MarkdownBlock block) => new(
        block.Spans.Select(SpanView.From).ToList(),
        block.FontSize,

        // Nagłówek pogrubiony w całości, niezależnie od wyróżnień w środku:
        // nagłówek pisany zwykłą grubością przestaje być nagłówkiem.
        block.IsHeading ? FontWeight.SemiBold : FontWeight.Normal,
        block.Indent,
        block.IsListItem,
        block.IsQuote,
        block.IsCode);

    /// <summary>Odstęp nad blokiem. Nagłówek dostaje więcej, bo otwiera nową myśl.</summary>
    public string Margin => IsListItem ? "0,2,0,2" : "0,8,0,2";
}
