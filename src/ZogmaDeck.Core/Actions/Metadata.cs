using System.Reflection;

namespace ZogmaDeck.Core.Actions;

/// <summary>Libellé français d'une valeur d'énumération affichée dans l'interface.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class LabelAttribute(string text) : Attribute
{
    public string Text { get; } = text;
}

public static class EnumLabels
{
    public static string Get<T>(T value) where T : struct, Enum => Get(typeof(T), value);

    public static string Get(Type enumType, object value)
    {
        var name = Enum.GetName(enumType, value);
        if (name is null) return value.ToString() ?? "";
        return enumType.GetField(name)?.GetCustomAttribute<LabelAttribute>()?.Text ?? name;
    }
}

/// <summary>Déclare un type d'action. Toute classe dérivée de <see cref="DeckAction"/> portant cet attribut est découverte par <see cref="ActionRegistry"/>.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ActionTypeAttribute(string typeId, string displayName) : Attribute
{
    /// <summary>Identifiant stable écrit dans le JSON. Ne jamais le modifier une fois publié.</summary>
    public string ActionId { get; } = typeId;
    public string DisplayName { get; } = displayName;
    public string Category { get; set; } = "Divers";
    /// <summary>Glyphe Segoe Fluent Icons.</summary>
    public string Glyph { get; set; } = "\uE768";
    public string Description { get; set; } = "";
    public int Order { get; set; }
}

public enum FieldKind
{
    Auto,
    Text,
    MultilineText,
    FilePath,
    FolderPath,
    FileOrUrl,
    Integer,
    Slider,
    Toggle,
    Choice,
    KeyChord,
    Profile,
    ObsScene,
    ObsInput,
    Json,
}

/// <summary>
/// Décrit une propriété éditable d'une action. L'éditeur génère automatiquement le contrôle adapté :
/// ajouter un type d'action ne nécessite aucune modification de l'interface.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class FieldAttribute(string label) : Attribute
{
    public string Label { get; } = label;
    public FieldKind Kind { get; set; } = FieldKind.Auto;
    public int Order { get; set; }
    public string? Hint { get; set; }
    public string? Placeholder { get; set; }
    /// <summary>Filtre de la boîte de dialogue de fichier (format Win32 : "Images|*.png;*.jpg").</summary>
    public string? FileFilter { get; set; }
    public double Min { get; set; } = double.NaN;
    public double Max { get; set; } = double.NaN;
    public string? Unit { get; set; }
    /// <summary>Nom d'une autre propriété dont la valeur conditionne l'affichage de ce champ.</summary>
    public string? VisibleWhen { get; set; }
    /// <summary>Valeurs (séparées par des virgules) de <see cref="VisibleWhen"/> pour lesquelles le champ est visible.</summary>
    public string? VisibleValues { get; set; }

    public bool IsVisibleFor(object? controllingValue)
    {
        if (VisibleWhen is null || VisibleValues is null) return true;
        var current = controllingValue?.ToString() ?? "";
        return VisibleValues.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains(current, StringComparer.OrdinalIgnoreCase);
    }
}
