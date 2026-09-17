using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Marshal.Application;
using Marshal.Application.UseCases;
using Marshal.Domain.Notes;
using Marshal.Infrastructure.Notes;

namespace Marshal.UI.ViewModels;

/// <summary>
/// Notatki: lista, edytor i podgląd (spec 11).
/// </summary>
/// <remarks>
/// Edytor i podgląd obok siebie, nie na przemian. Przełącznik „pisz / oglądaj" każe
/// pamiętać, w którym trybie się jest, a przy notatce, którą pisze się raz i czyta
/// dziesięć razy, to jest pytanie zadawane bez potrzeby.
/// </remarks>
public sealed partial class NotesViewModel(NoteService notes) : ObservableObject
{
    private Guid _openId;

    /// <summary>Szukanie rusza na każdy znak, a kontekst bazy jest jeden — zob. LatestOnly.</summary>
    private readonly LatestOnly _kolejka = new();

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Content { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsPinned { get; set; }

    [ObservableProperty]
    public partial string NewTitle { get; set; } = string.Empty;

    public ObservableCollection<Note> Items { get; } = [];

    public ObservableCollection<BlockView> Preview { get; } = [];

    public bool HasItems => Items.Count > 0;

    public string Empty => string.IsNullOrWhiteSpace(Query)
        ? "Jeszcze nic tu nie ma. Notatka to rzecz, której nie trzeba robić, tylko mieć pod ręką."
        : "Nic nie pasuje do szukanego.";

    public async Task LoadAsync() => await SearchAsync();

    [RelayCommand]
    private Task SearchAsync() => _kolejka.RunAsync(SzukajAsync);

    private async Task SzukajAsync()
    {
        Items.Clear();
        foreach (var notatka in await notes.SearchAsync(Query))
        {
            Items.Add(notatka);
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(Empty));
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(NewTitle))
        {
            return;
        }

        var notatka = await notes.CreateAsync(NewTitle);
        NewTitle = string.Empty;

        await SearchAsync();
        Open(notatka);
    }

    [RelayCommand]
    private void Open(Note? note)
    {
        if (note is null)
        {
            return;
        }

        _openId = note.Id;
        Title = note.Title;
        Content = note.Content;
        IsPinned = note.IsPinned;
        IsEditing = true;
        RenderPreview();
    }

    [RelayCommand]
    private void Close() => IsEditing = false;

    [RelayCommand]
    private async Task SaveAsync()
    {
        await notes.SaveAsync(_openId, Title, Content, IsPinned);
        IsEditing = false;
        await SearchAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        await notes.DeleteAsync(_openId);
        IsEditing = false;
        await SearchAsync();
    }

    /// <summary>
    /// Podgląd przeliczany przy każdej zmianie treści.
    /// </summary>
    /// <remarks>
    /// Przy notatce osobistej to kilkadziesiąt bloków, więc przeliczanie na każdy znak
    /// jest tańsze niż jakikolwiek mechanizm odkładania go w czasie — a odkładanie
    /// znaczyłoby podgląd chwilami nieaktualny, czyli dokładnie to, przed czym podgląd
    /// obok edytora ma chronić.
    /// </remarks>
    private void RenderPreview()
    {
        Preview.Clear();
        foreach (var blok in MarkdownReader.Read(Content).Blocks)
        {
            Preview.Add(BlockView.From(blok));
        }

        OnPropertyChanged(nameof(HasMarkup));
    }

    /// <summary>
    /// Czy notatka ma cokolwiek do pokazania w podglądzie.
    /// </summary>
    /// <remarks>
    /// Podgląd stał dotąd zawsze i przy zwykłej notatce był dosłownie drugą kopią tego
    /// samego tekstu — dwie połówki okna z tą samą treścią. Podgląd ma sens dopiero
    /// wtedy, gdy pokazuje coś, czego w polu edycji nie widać: nagłówek, punkt listy,
    /// cytat, wyróżnienie, odnośnik. Bez tego zabiera połowę szerokości za nic.
    /// </remarks>
    public bool HasMarkup =>
        Preview.Any(b =>
            b.IsListItem || b.IsQuote || b.IsCode || b.Indent > 0
            || b.Weight != FontWeight.Normal
            || b.Spans.Any(w => w.IsLink || w.IsCode
                || w.Weight != FontWeight.Normal
                || w.Style != FontStyle.Normal));

    partial void OnContentChanged(string value) => RenderPreview();

    partial void OnQueryChanged(string value) => _ = SearchAsync();
}
