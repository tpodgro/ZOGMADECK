using System.Windows.Input;
using ZogmaDeck.Core.Model;
using ZogmaDeck.Helpers;

namespace ZogmaDeck.ViewModels;

/// <summary>Ligne de la grille virtualisée du sélecteur (la virtualisation WPF fonctionne par lignes).</summary>
public sealed record IconRow(IReadOnlyList<IconChoice> Items);

/// <summary>Recherche et choix d'une icône parmi les symboles Fluent, les emoji ou les logos.</summary>
public sealed class IconPickerViewModel : ObservableObject
{
    private const int PerRow = 8;
    private readonly Action<IconChoice> _onPick;
    private IconKind _kind = IconKind.Glyph;
    private string _query = "";
    private string _category = AllCategories;
    private IReadOnlyList<IconRow> _rows = [];
    private int _count;

    public const string AllCategories = "Toutes";

    public IconPickerViewModel(Action<IconChoice> onPick)
    {
        _onPick = onPick;
        PickCommand = new RelayCommand(p =>
        {
            if (p is IconChoice c) _onPick(c);
        });
    }

    public ICommand PickCommand { get; }

    public IconKind Kind
    {
        get => _kind;
        set
        {
            if (!Set(ref _kind, value)) return;
            _category = AllCategories;
            _query = "";
            OnPropertyChanged(nameof(Query));
            OnPropertyChanged(nameof(Category));
            OnPropertyChanged(nameof(Categories));
            OnPropertyChanged(nameof(Placeholder));
            Refresh();
        }
    }

    public string Query
    {
        get => _query;
        set
        {
            if (Set(ref _query, value ?? "")) Refresh();
        }
    }

    public IReadOnlyList<string> Categories => _kind switch
    {
        IconKind.Emoji => [AllCategories, .. IconLibrary.EmojiGroups],
        IconKind.Glyph => [AllCategories, "Sélection", "Tous les symboles"],
        _ => [AllCategories],
    };

    public string Category
    {
        get => _category;
        set
        {
            if (Set(ref _category, value ?? AllCategories)) Refresh();
        }
    }

    public string Placeholder => _kind switch
    {
        IconKind.Emoji => "Rechercher un emoji (ex. : chat, fusée, micro, cœur…)",
        IconKind.Brand => "Rechercher une application (ex. : Spotify, Discord, OBS…)",
        _ => "Rechercher un symbole (ex. : micro, lecture, dossier, play, mail…)",
    };

    public IReadOnlyList<IconRow> Rows
    {
        get => _rows;
        private set => Set(ref _rows, value);
    }

    public string CountText => _count == 0 ? "Aucune icône ne correspond." : $"{_count} icône(s)";

    public void Refresh()
    {
        IEnumerable<IconChoice> source = _kind switch
        {
            IconKind.Emoji => IconLibrary.Emoji,
            IconKind.Brand => IconLibrary.BrandChoices,
            _ => IconLibrary.Fluent,
        };
        if (_category != AllCategories) source = source.Where(c => c.Category == _category);
        var list = IconLibrary.Search(source, _query).ToList();
        _count = list.Count;
        Rows = list.Chunk(PerRow).Select(c => new IconRow(c)).ToList();
        OnPropertyChanged(nameof(CountText));
    }
}
