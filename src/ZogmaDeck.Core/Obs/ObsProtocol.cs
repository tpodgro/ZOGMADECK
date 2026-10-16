using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZogmaDeck.Core.Actions;

namespace ZogmaDeck.Core.Obs;

public sealed class ObsException(string message, Exception? inner = null) : ActionException(message, inner);

public enum ObsOpCode
{
    Hello = 0,
    Identify = 1,
    Identified = 2,
    Reidentify = 3,
    Event = 5,
    Request = 6,
    RequestResponse = 7,
    RequestBatch = 8,
    RequestBatchResponse = 9,
}

public readonly record struct ObsMessage(ObsOpCode Op, JsonElement Data);

/// <summary>Messages du protocole obs-websocket 5.x (rpcVersion 1). Fonctions pures, testables sans OBS.</summary>
public static class ObsProtocol
{
    public const int RpcVersion = 1;
    public const string SubProtocol = "obsws.json";

    /// <summary>authentication = base64(sha256(base64(sha256(password + salt)) + challenge))</summary>
    public static string ComputeAuthentication(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    public static string BuildIdentify(string? authentication, int eventSubscriptions = 0)
    {
        var d = new JsonObject { ["rpcVersion"] = RpcVersion, ["eventSubscriptions"] = eventSubscriptions };
        if (authentication is not null) d["authentication"] = authentication;
        return new JsonObject { ["op"] = (int)ObsOpCode.Identify, ["d"] = d }.ToJsonString();
    }

    public static string BuildRequest(string requestType, string requestId, JsonObject? requestData)
    {
        var d = new JsonObject { ["requestType"] = requestType, ["requestId"] = requestId };
        if (requestData is not null) d["requestData"] = requestData.DeepClone();
        return new JsonObject { ["op"] = (int)ObsOpCode.Request, ["d"] = d }.ToJsonString();
    }

    public static ObsMessage Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.Number)
                throw new ObsException("Message OBS invalide (champ « op » manquant).");
            var data = root.TryGetProperty("d", out var d) ? d.Clone() : default;
            return new ObsMessage((ObsOpCode)op.GetInt32(), data);
        }
        catch (JsonException ex)
        {
            throw new ObsException("Message OBS illisible.", ex);
        }
    }

    public static (string Challenge, string Salt)? GetAuthChallenge(JsonElement helloData)
    {
        if (helloData.ValueKind != JsonValueKind.Object || !helloData.TryGetProperty("authentication", out var auth) ||
            auth.ValueKind != JsonValueKind.Object) return null;
        return (auth.GetProperty("challenge").GetString() ?? "", auth.GetProperty("salt").GetString() ?? "");
    }

    /// <summary>Message français pour un code de fermeture WebSocket envoyé par OBS.</summary>
    public static string DescribeCloseCode(int? code, string? reason) => code switch
    {
        4009 => "Mot de passe OBS incorrect. Vérifiez-le dans Réglages › OBS (OBS : Outils › Paramètres du serveur WebSocket).",
        4010 => "Version du protocole incompatible : ZogmaDeck nécessite obs-websocket 5.x (inclus dans OBS 28 ou plus récent).",
        4011 => "La session a été fermée par OBS.",
        4007 => "OBS a refusé la requête : session non identifiée.",
        4002 or 4003 or 4004 or 4005 or 4006 => $"OBS a rejeté un message ({code}) : {reason}".TrimEnd(' ', ':'),
        null or 1000 or 1001 => "OBS a fermé la connexion (OBS a peut-être été quitté).",
        1006 => "Connexion à OBS interrompue.",
        _ => $"OBS a fermé la connexion (code {code}{(string.IsNullOrEmpty(reason) ? "" : " : " + reason)}).",
    };

    /// <summary>Message français pour un code requestStatus en échec.</summary>
    public static string DescribeRequestStatus(string requestType, int code, string? comment)
    {
        var what = code switch
        {
            204 => "type de requête inconnu",
            207 => "OBS n'est pas encore prêt",
            300 or 301 => "un champ obligatoire est manquant",
            400 or 401 or 402 or 403 or 404 => "un champ de la requête est invalide",
            500 => "la sortie est déjà active",
            501 => "la sortie n'est pas active",
            502 => "la sortie est déjà en pause",
            503 => "la sortie n'est pas en pause",
            504 => "la sortie est désactivée (ex. tampon de relecture non activé dans OBS)",
            505 => "le mode studio est actif",
            506 => "le mode studio n'est pas actif",
            600 => "élément introuvable (vérifiez le nom exact de la scène ou de la source)",
            601 => "l'élément existe déjà",
            602 or 605 => "type de source incompatible",
            604 => "état de la ressource incompatible",
            700 or 701 or 702 or 703 => "OBS n'a pas pu exécuter l'opération",
            _ => $"erreur {code}",
        };
        var detail = string.IsNullOrWhiteSpace(comment) ? "" : $" — {comment}";
        return $"OBS « {requestType} » : {what}{detail}.";
    }
}
