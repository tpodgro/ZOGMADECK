using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using ZogmaDeck.Core.Actions;
using ZogmaDeck.Core.Model;
using ZogmaDeck.Core.Storage;
using ZogmaDeck.Helpers;
using ZogmaDeck.Services;

namespace ZogmaDeck.ViewModels;

/// <summary>Personnalisation du panneau : fond, image, tailles, espacement, arrondi, position. Appliquée en direct.</summary>
public sealed class AppearanceViewModel : ObservableObject
{
    private readonly DeckRuntime _rt;
    private string _colorText = "";

    public AppearanceViewModel(DeckRuntime runtime)
    {
        _rt = runtime;
        Positions = Enum.GetValues<PanelPositionMode>().Select(v => new Option(v, EnumLabels.Get(v))).ToList();
        TileStyles = Enum.GetValues<TileStyle>().Select(v => new Option(v, EnumLabels.Get(v))).ToList();
        SizeModes = Enum.GetValues<PanelSizeMode>().Select(v => new Option(v, EnumLabels.Get(v))).ToList();
        SetColorCommand = new RelayCommand(p =>
        {
            if (p is string hex) BackgroundColorText = hex;
        });
        BrowseImageCommand = new RelayCommand(BrowseImage);
        ClearImageCommand = new RelayCommand(() =>
        {
            L.BackgroundImage = null;
            Changed(nameof(BackgroundImage), nameof(HasImage));
        }, () => HasImage);
        PreviewCommand = new RelayCommand(() => ((App)Application.Current).ShowPanelPreview());
        RecenterCommand = new RelayCommand(() =>
        {
            L.Position = PanelPositionMode.Centered;
            Changed(nameof(SelectedPosition));
        });
        ResetCommand = new RelayCommand(() =>
        {
            _rt.Config.Settings.Panel = new PanelAppearance();
            Reload();
            _rt.NotifySettingsChanged();
        });
        Reload();
    }

    private PanelAppearance L => _rt.Config.Settings.Panel;

    /// <summary>Voiles proposés : blanc (verre clair, par défaut), sombres et teintés.</summary>
    public static IReadOnlyList<string> Palette { get; } =
        ["#FFFFFF", "#F3F3F3", "#DCE8FF", "#E9DDFF", "#FFE1EC", "#DFF5EA", "#1E1E1E", "#000000", "#0F2A47", "#1E1B3A", "#24123A", "#0E3B2E"];

    public IReadOnlyList<Option> Positions { get; }
    public IReadOnlyList<Option> TileStyles { get; }
    public IReadOnlyList<Option> SizeModes { get; }

    public Option SelectedSizeMode
    {
        get => SizeModes.First(o => Equals(o.Value, L.SizeMode));
        set
        {
            if (value?.Value is not PanelSizeMode m || m == L.SizeMode) return;
            L.SizeMode = m;
            Changed(nameof(SelectedSizeMode));
        }
    }

    public bool BlurBehind
    {
        get => L.BlurBehind;
        set
        {
            L.BlurBehind = value;
            Changed(nameof(BlurBehind));
        }
    }

    public Option SelectedTileStyle
    {
        get => TileStyles.First(o => Equals(o.Value, L.TileStyle));
        set
        {
            if (value?.Value is not TileStyle t || t == L.TileStyle) return;
            L.TileStyle = t;
            Changed(nameof(SelectedTileStyle));
        }
    }
    public ICommand SetColorCommand { get; }
    public ICommand BrowseImageCommand { get; }
    public ICommand ClearImageCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand RecenterCommand { get; }
    public ICommand ResetCommand { get; }

    public void Reload()
    {
        _colorText = L.BackgroundColor;
        foreach (var p in GetType().GetProperties().Where(p => p.CanRead && p.DeclaringType == GetType()))
            OnPropertyChanged(p.Name);
    }

    private void Changed(params string[] names)
    {
        foreach (var n in names) OnPropertyChanged(n);
        _rt.NotifySettingsChanged();
    }

    public string BackgroundColorText
    {
        get => _colorText;
        set
        {
            if (!Set(ref _colorText, value)) return;
            var v = value.Trim();
            if (!v.StartsWith('#')) v = "#" + v;
            OnPropertyChanged(nameof(ColorError));
            if (!ConfigValidator.IsValidColor(v)) return;
            L.BackgroundColor = v.ToUpperInvariant();
            Changed(nameof(ColorBrush));
        }
    }

    public string? ColorError
    {
        get
        {
            var v = _colorText.Trim();
            if (!v.StartsWith('#')) v = "#" + v;
            return ConfigValidator.IsValidColor(v) ? null : "Format attendu : #RRGGBB";
        }
    }

    public Brush ColorBrush => ColorHelper.Brush(ColorHelper.Parse(L.BackgroundColor, Colors.Black));

    public int BackgroundOpacity
    {
        get => L.BackgroundOpacity;
        set
        {
            L.BackgroundOpacity = Math.Clamp(value, 0, 100);
            Changed(nameof(BackgroundOpacity), nameof(OpacityText));
        }
    }

    public string OpacityText => L.BackgroundOpacity == 0 ? "Transparent" : $"{L.BackgroundOpacity} %";

    public string? BackgroundImage => L.BackgroundImage;
    public bool HasImage => !string.IsNullOrWhiteSpace(L.BackgroundImage);

    public int BackgroundImageOpacity
    {
        get => L.BackgroundImageOpacity;
        set
        {
            L.BackgroundImageOpacity = Math.Clamp(value, 0, 100);
            Changed(nameof(BackgroundImageOpacity));
        }
    }

    public int IconScale
    {
        get => L.IconScale;
        set
        {
            L.IconScale = Math.Clamp(value, 40, 200);
            Changed(nameof(IconScale));
        }
    }

    public int LabelScale
    {
        get => L.LabelScale;
        set
        {
            L.LabelScale = Math.Clamp(value, 60, 180);
            Changed(nameof(LabelScale));
        }
    }

    public int TileSpacing
    {
        get => L.TileSpacing;
        set
        {
            L.TileSpacing = Math.Clamp(value, 0, 32);
            Changed(nameof(TileSpacing));
        }
    }

    public int TileCornerRadius
    {
        get => L.TileCornerRadius;
        set
        {
            L.TileCornerRadius = Math.Clamp(value, 0, 40);
            Changed(nameof(TileCornerRadius));
        }
    }

    public bool ShowShortcutNumbers
    {
        get => L.ShowShortcutNumbers;
        set
        {
            L.ShowShortcutNumbers = value;
            Changed(nameof(ShowShortcutNumbers));
        }
    }

    public Option SelectedPosition
    {
        get => Positions.First(o => Equals(o.Value, L.Position));
        set
        {
            if (value?.Value is not PanelPositionMode m || m == L.Position) return;
            L.Position = m;
            Changed(nameof(SelectedPosition));
        }
    }

    private void BrowseImage()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Image de fond du panneau",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|Tous les fichiers|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        if (ImageLoader.Load(dlg.FileName, "", 64) is null)
        {
            MessageBox.Show("Cette image ne peut pas être lue.", "ZogmaDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            L.BackgroundImage = ImageLoader.ImportToAssets(dlg.FileName, _rt.Paths.AssetsDirectory);
        }
        catch (IOException ex)
        {
            MessageBox.Show("Copie de l'image impossible : " + ex.Message, "ZogmaDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Changed(nameof(BackgroundImage), nameof(HasImage));
    }
}
