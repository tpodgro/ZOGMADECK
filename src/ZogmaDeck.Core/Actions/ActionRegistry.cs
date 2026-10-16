using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ZogmaDeck.Core.Actions;

public sealed record ActionTypeInfo(string TypeId, Type ClrType, string DisplayName, string Category, string Glyph, string Description, int Order);

/// <summary>Catalogue des types d'actions disponibles, découverts par réflexion via <see cref="ActionTypeAttribute"/>.</summary>
public static class ActionRegistry
{
    private static readonly Lock Sync = new();
    private static readonly Dictionary<string, ActionTypeInfo> ById = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, ActionTypeInfo> ByType = [];

    static ActionRegistry() => RegisterAssembly(typeof(ActionRegistry).Assembly);

    /// <summary>Enregistre tous les types d'actions d'un assembly (point d'extension).</summary>
    public static void RegisterAssembly(Assembly assembly)
    {
        foreach (var t in assembly.GetTypes())
        {
            if (t.IsAbstract || !typeof(DeckAction).IsAssignableFrom(t)) continue;
            var attr = t.GetCustomAttribute<ActionTypeAttribute>();
            if (attr is null) continue;
            if (t.GetConstructor(Type.EmptyTypes) is null)
                throw new InvalidOperationException($"Le type d'action {t.Name} doit avoir un constructeur sans paramètre.");
            Register(new ActionTypeInfo(attr.ActionId, t, attr.DisplayName, attr.Category, attr.Glyph, attr.Description, attr.Order));
        }
    }

    public static void Register(ActionTypeInfo info)
    {
        lock (Sync)
        {
            if (ById.TryGetValue(info.TypeId, out var existing) && existing.ClrType != info.ClrType)
                throw new InvalidOperationException($"Identifiant d'action en double : {info.TypeId}");
            ById[info.TypeId] = info;
            ByType[info.ClrType] = info;
        }
    }

    public static IReadOnlyList<ActionTypeInfo> All
    {
        get { lock (Sync) return ById.Values.OrderBy(i => i.Order).ThenBy(i => i.DisplayName).ToList(); }
    }

    public static ActionTypeInfo? Find(string? typeId)
    {
        if (typeId is null) return null;
        lock (Sync) return ById.GetValueOrDefault(typeId);
    }

    public static ActionTypeInfo? GetInfo(Type t)
    {
        lock (Sync) return ByType.GetValueOrDefault(t);
    }

    public static DeckAction Create(string typeId) =>
        (DeckAction)Activator.CreateInstance(Find(typeId)?.ClrType ?? throw new ArgumentException($"Type d'action inconnu : {typeId}"))!;
}

/// <summary>Sérialise les actions sous la forme { "type": "…", …propriétés }. Les types inconnus sont conservés à l'identique.</summary>
public sealed class ActionJsonConverter : JsonConverter<DeckAction>
{
    public override DeckAction? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Une action doit être un objet JSON.");
        var typeId = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var info = ActionRegistry.Find(typeId);
        if (info is null)
            return new UnknownAction { OriginalTypeId = typeId ?? "(absent)", Raw = root.Clone() };
        return (DeckAction?)root.Deserialize(info.ClrType, options)
            ?? throw new JsonException($"Action « {typeId} » illisible.");
    }

    public override void Write(Utf8JsonWriter writer, DeckAction value, JsonSerializerOptions options)
    {
        if (value is UnknownAction unknown)
        {
            if (unknown.Raw.ValueKind == JsonValueKind.Object) unknown.Raw.WriteTo(writer);
            else writer.WriteNullValue();
            return;
        }
        var body = JsonSerializer.SerializeToNode(value, value.GetType(), options) as JsonObject ?? [];
        var result = new JsonObject { ["type"] = value.TypeId };
        foreach (var (k, v) in body.ToList())
        {
            body.Remove(k);
            result[k] = v;
        }
        result.WriteTo(writer, options);
    }
}

public static class ActionJson
{
    public static DeckAction Clone(DeckAction action)
    {
        var json = JsonSerializer.Serialize(action, Storage.JsonDefaults.Options);
        return JsonSerializer.Deserialize<DeckAction>(json, Storage.JsonDefaults.Options)!;
    }
}
