using System.Reflection;
using System.Text.Json.Serialization;

namespace ZogmaDeck.Core.Actions;

public enum ErrorPolicy
{
    [Label("Arrêter la séquence")] Stop,
    [Label("Continuer la séquence")] Continue,
}

public enum ResourceKind
{
    /// <summary>Image d'icône — copiée dans les exports.</summary>
    Image,
    /// <summary>Son ou média — copié dans les exports.</summary>
    Media,
    /// <summary>Programme — vérifié à l'import, jamais copié.</summary>
    Program,
    /// <summary>Fichier ou dossier ouvert — vérifié à l'import, jamais copié.</summary>
    Document,
}

/// <summary>Référence à un fichier utilisé par la configuration (pour l'export et la détection des ressources manquantes).</summary>
public sealed class ResourceSlot(ResourceKind kind, string description, Func<string?> get, Action<string> set)
{
    public ResourceKind Kind { get; } = kind;
    public string Description { get; } = description;
    public string? Path { get => get(); set => set(value ?? ""); }
    public bool IsBundled => Kind is ResourceKind.Image or ResourceKind.Media;
}

/// <summary>
/// Étape d'une séquence. Pour ajouter un type d'action : dériver de cette classe, ajouter <see cref="ActionTypeAttribute"/>,
/// décorer les propriétés éditables avec <see cref="FieldAttribute"/>, implémenter <see cref="ExecuteAsync"/>.
/// La sérialisation, l'éditeur et l'exécution s'adaptent automatiquement.
/// </summary>
public abstract class DeckAction
{
    [JsonIgnore]
    public string TypeId => ActionRegistry.GetInfo(GetType())?.TypeId ?? GetType().Name;

    public bool Enabled { get; set; } = true;
    public ErrorPolicy OnError { get; set; } = ErrorPolicy.Stop;

    /// <summary>Résumé lisible en français, affiché dans l'éditeur et les messages.</summary>
    public abstract string Describe();

    /// <summary>Problèmes de configuration (champs vides, valeurs invalides). Une liste vide signifie « valide ».</summary>
    public virtual IEnumerable<string> Validate() => [];

    /// <summary>Fichiers référencés par l'action.</summary>
    public virtual IEnumerable<ResourceSlot> GetResources() => [];

    /// <summary>L'action envoie-t-elle des frappes clavier à la fenêtre cible ?</summary>
    [JsonIgnore]
    public virtual bool NeedsTargetFocus => false;

    public abstract Task ExecuteAsync(ActionContext ctx, CancellationToken ct);

    public DeckAction Clone() => ActionJson.Clone(this);

    /// <summary>Champs éditables, triés.</summary>
    public static IReadOnlyList<(PropertyInfo Property, FieldAttribute Field)> GetFields(Type actionType) =>
        actionType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (Property: p, Field: p.GetCustomAttribute<FieldAttribute>()))
            .Where(x => x.Field is not null && x.Property.CanRead && x.Property.CanWrite)
            .OrderBy(x => x.Field!.Order)
            .Select(x => (x.Property, x.Field!))
            .ToList();

    protected static string Quote(string? s, int max = 40)
    {
        if (string.IsNullOrWhiteSpace(s)) return "(non défini)";
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length > max ? $"« {s[..(max - 1)]}… »" : $"« {s} »";
    }
}

/// <summary>Action d'un type inconnu (version plus récente, extension absente). Conservée telle quelle pour ne rien perdre.</summary>
public sealed class UnknownAction : DeckAction
{
    public string OriginalTypeId { get; set; } = "";
    public System.Text.Json.JsonElement Raw { get; set; }

    public override string Describe() => $"Action inconnue « {OriginalTypeId} » (ignorée)";
    public override IEnumerable<string> Validate() => [$"Type d'action inconnu : « {OriginalTypeId} ». Cette étape ne peut pas être exécutée."];
    public override Task ExecuteAsync(ActionContext ctx, CancellationToken ct) =>
        throw new ActionException($"Type d'action inconnu « {OriginalTypeId} » : mettez ZogmaDeck à jour.");
}
