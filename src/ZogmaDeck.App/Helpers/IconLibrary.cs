using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using ZogmaDeck.Core.Diagnostics;
using ZogmaDeck.Core.Model;

namespace ZogmaDeck.Helpers;

/// <summary>Icône proposée dans le sélecteur.</summary>
public sealed record IconChoice(IconKind Kind, string Value, string Name, string Category, string SearchText)
{
    public Geometry? Geometry => Kind == IconKind.Brand ? IconLibrary.BrandGeometry(Value) : null;
    public string? Text => Kind is IconKind.Glyph or IconKind.Emoji ? Value : null;
    public FontFamily? Font => Kind == IconKind.Emoji ? IconLibrary.EmojiFont : IconCatalog.Font;
}

public sealed record BrandInfo(string Slug, string Title, string Hex, Geometry Geometry);

/// <summary>
/// Catalogues d'icônes intégrés, chargés à la première utilisation :
/// tous les symboles Segoe Fluent Icons présents sur ce Windows, les emoji (noms et mots-clés français CLDR)
/// et une sélection de logos d'applications (Simple Icons, CC0).
/// </summary>
public static class IconLibrary
{
    public static readonly FontFamily EmojiFont = new("Segoe UI Emoji");

    public static readonly string[] EmojiGroups =
        ["Smileys et émotions", "Personnes et corps", "Animaux et nature", "Nourriture et boissons", "Voyages et lieux", "Activités", "Objets", "Symboles"];

    private static readonly Lazy<IReadOnlyList<IconChoice>> FluentLazy = new(LoadFluent);
    private static readonly Lazy<IReadOnlyList<IconChoice>> EmojiLazy = new(LoadEmoji);
    private static readonly Lazy<Dictionary<string, BrandInfo>> BrandsLazy = new(LoadBrands);

    public static IReadOnlyList<IconChoice> Fluent => FluentLazy.Value;
    public static IReadOnlyList<IconChoice> Emoji => EmojiLazy.Value;
    public static IReadOnlyCollection<BrandInfo> Brands => BrandsLazy.Value.Values;

    public static IReadOnlyList<IconChoice> BrandChoices =>
        BrandsLazy.Value.Values.OrderBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(b => new IconChoice(IconKind.Brand, b.Slug, b.Title, "Logos", Normalize(b.Title + " " + b.Slug))).ToList();

    public static BrandInfo? Brand(string? slug) => slug is not null && BrandsLazy.Value.TryGetValue(slug, out var b) ? b : null;

    public static Geometry? BrandGeometry(string? slug) => Brand(slug)?.Geometry;

    /// <summary>Recherche insensible à la casse et aux accents, sur le nom et les mots-clés (français et anglais).</summary>
    public static IEnumerable<IconChoice> Search(IEnumerable<IconChoice> source, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return source;
        var terms = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return source.Where(c => terms.All(t => c.SearchText.Contains(t, StringComparison.Ordinal)));
    }

    public static string Normalize(string s)
    {
        var d = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString();
    }

    private static IEnumerable<string> ReadLines(string name)
    {
        var info = Application.GetResourceStream(new Uri($"pack://application:,,,/ZogmaDeck;component/Assets/Icons/{name}"));
        if (info is null) yield break;
        using var reader = new StreamReader(info.Stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
            if (line.Length > 0 && line[0] != '#') yield return line;
    }

    private static IReadOnlyList<IconChoice> LoadFluent()
    {
        try
        {
            // Noms français de la sélection courante, prioritaires dans l'affichage et la recherche.
            var french = IconCatalog.Glyphs.ToDictionary(g => g.Glyph, g => g.Name);
            var names = new Dictionary<string, string>();
            foreach (var line in ReadLines("fluent-icons.tsv"))
            {
                var p = line.Split('\t');
                if (p.Length >= 2 && int.TryParse(p[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                    names[char.ConvertFromUtf32(code)] = p[1];
            }

            // Uniquement les symboles réellement présents dans la police installée.
            var available = new HashSet<string>();
            foreach (var family in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            {
                if (new Typeface(new FontFamily(family), default, default, default).TryGetGlyphTypeface(out var face))
                {
                    foreach (var c in face.CharacterToGlyphMap.Keys)
                        if (c is >= 0xE000 and <= 0xF8FF) available.Add(char.ConvertFromUtf32(c));
                    break;
                }
            }

            var list = new List<IconChoice>();
            foreach (var g in IconCatalog.Glyphs)
                list.Add(new IconChoice(IconKind.Glyph, g.Glyph, g.Name, "Sélection",
                    Normalize($"{g.Name} {names.GetValueOrDefault(g.Glyph)}")));
            foreach (var g in available.OrderBy(s => char.ConvertToUtf32(s, 0)))
            {
                if (french.ContainsKey(g)) continue;
                var name = names.GetValueOrDefault(g) ?? $"U+{char.ConvertToUtf32(g, 0):X4}";
                list.Add(new IconChoice(IconKind.Glyph, g, name, "Tous les symboles", Normalize(name + " " + SplitWords(name))));
            }
            return list;
        }
        catch (Exception ex)
        {
            Log.Warn("Catalogue de symboles indisponible", ex);
            return IconCatalog.Glyphs.Select(g => new IconChoice(IconKind.Glyph, g.Glyph, g.Name, "Sélection", Normalize(g.Name))).ToList();
        }
    }

    /// <summary>« GlobalNavButton » → « global nav button » pour la recherche.</summary>
    private static string SplitWords(string name)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) sb.Append(' ');
            sb.Append(name[i]);
        }
        return sb.ToString();
    }

    private static IReadOnlyList<IconChoice> LoadEmoji()
    {
        try
        {
            // Seuls les emoji dont tous les caractères existent dans la police installée (évite les rectangles vides
            // pour les emoji plus récents que Windows).
            IDictionary<int, ushort>? map = new Typeface(EmojiFont, default, default, default).TryGetGlyphTypeface(out var face)
                ? face.CharacterToGlyphMap
                : null;
            var list = new List<IconChoice>();
            foreach (var line in ReadLines("emoji.tsv"))
            {
                var p = line.Split('\t');
                if (p.Length < 5 || !int.TryParse(p[1], out var g)) continue;
                if (map is not null && !IsSupported(p[0], map)) continue;
                list.Add(new IconChoice(IconKind.Emoji, p[0], p[2], EmojiGroups[Math.Clamp(g, 0, EmojiGroups.Length - 1)],
                    Normalize($"{p[2]} {p[3]} {p[4]}")));
            }
            return list;
        }
        catch (Exception ex)
        {
            Log.Warn("Catalogue d'emoji indisponible", ex);
            return [];
        }
    }

    private static bool IsSupported(string emoji, IDictionary<int, ushort> map)
    {
        foreach (var rune in emoji.EnumerateRunes())
        {
            if (rune.Value is 0x200D or 0xFE0F or 0x20E3) continue; // liaison, sélecteur de variante, cadre de touche
            if (!map.ContainsKey(rune.Value)) return false;
        }
        return true;
    }

    private static Dictionary<string, BrandInfo> LoadBrands()
    {
        var d = new Dictionary<string, BrandInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in ReadLines("brands.tsv"))
            {
                var p = line.Split('\t');
                if (p.Length < 4) continue;
                try
                {
                    var geo = Geometry.Parse(p[3]);
                    geo.Freeze();
                    d[p[0]] = new BrandInfo(p[0], p[1], "#" + p[2], geo);
                }
                catch (FormatException)
                {
                    Log.Warn($"Logo illisible : {p[0]}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Catalogue de logos indisponible", ex);
        }
        return d;
    }
}
