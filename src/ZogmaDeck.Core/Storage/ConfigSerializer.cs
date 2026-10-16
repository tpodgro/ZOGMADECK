using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ZogmaDeck.Core.Actions;
using ZogmaDeck.Core.Model;

namespace ZogmaDeck.Core.Storage;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = Create();

    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 64,
    };

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        o.Converters.Add(new ActionJsonConverter());
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }
}

public sealed class ConfigFormatException(string message, Exception? inner = null) : Exception(message, inner);

public static class ConfigSerializer
{
    public static string Serialize(DeckConfig config) => JsonSerializer.Serialize(config, JsonDefaults.Options);

    /// <summary>Lit, migre puis valide une configuration. Les corrections appliquées sont ajoutées à <paramref name="warnings"/>.</summary>
    public static DeckConfig Deserialize(string json, List<string> warnings)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: JsonDefaults.DocumentOptions) as JsonObject
                ?? throw new ConfigFormatException("Le fichier ne contient pas un objet JSON.");
        }
        catch (JsonException ex)
        {
            throw new ConfigFormatException($"JSON invalide (ligne {ex.LineNumber + 1}) : {ex.Message}", ex);
        }

        ConfigMigrator.Migrate(root, warnings);

        DeckConfig? config;
        try
        {
            config = root.Deserialize<DeckConfig>(JsonDefaults.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            throw new ConfigFormatException("Structure de configuration invalide : " + ex.Message, ex);
        }
        if (config is null) throw new ConfigFormatException("Configuration vide.");

        warnings.AddRange(ConfigValidator.Normalize(config));
        return config;
    }

    public static DeckConfig DeepClone(DeckConfig config) => Deserialize(Serialize(config), []);
}

/// <summary>Migrations successives du schéma. Ajouter une entrée N → N+1 à chaque changement de format.</summary>
public static class ConfigMigrator
{
    private static readonly SortedDictionary<int, Action<JsonObject>> Steps = new()
    {
        // 0 → 1 : fichiers antérieurs au versionnage (aucune différence de structure, simple marquage).
        [0] = _ => { },
        // 1 → 2 : apparence du panneau (settings.panel) et nouveaux types d'icônes ; valeurs par défaut, rien à convertir.
        [1] = _ => { },
    };

    /// <returns>La version d'origine du document.</returns>
    public static int Migrate(JsonObject root, List<string> warnings)
    {
        var version = 0;
        if (root["schemaVersion"] is JsonValue v && v.TryGetValue<int>(out var parsed)) version = parsed;

        if (version > DeckConfig.CurrentSchemaVersion)
        {
            warnings.Add($"Cette configuration provient d'une version plus récente de ZogmaDeck (schéma {version}). " +
                         "Les éléments inconnus sont conservés autant que possible.");
            return version;
        }

        var original = version;
        while (version < DeckConfig.CurrentSchemaVersion)
        {
            if (!Steps.TryGetValue(version, out var step))
                throw new ConfigFormatException($"Aucune migration disponible depuis le schéma {version}.");
            step(root);
            version++;
        }
        root["schemaVersion"] = DeckConfig.CurrentSchemaVersion;
        if (original != version) warnings.Add($"Configuration migrée du schéma {original} vers le schéma {version}.");
        return original;
    }
}
