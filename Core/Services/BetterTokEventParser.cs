using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamLightControl2.Core.Models;

namespace StreamLightControl2.Core.Services;

public enum FrameKind { Event, Connected, Subscribed, Pong, Error, Unknown }

public sealed class ParsedFrame
{
    public FrameKind Kind { get; init; }
    public StreamEvent? Event { get; init; }
    public string ErrorCode { get; init; } = "";
    public string Message { get; init; } = "";
    public string Username { get; init; } = "";
}

/// <summary>
/// Zet BetterTok-berichten om naar <see cref="StreamEvent"/>. Gebaseerd op de officiële documentatie
/// (bettertok.app/developer-api):
///  • Local Mode: { "event": "...", "data": { camelCase-velden zoals uniqueId, nickname, giftName, repeatCount, diamondCount } }
///  • Cloud Mode: { "v":1, "id":"evt_…", "type":"gift", "data": { viewer_username, display_name, gift_name, gift_count, gift_total_value … } }
///  • Control frames: connected / subscribed / pong / error (code + message)
/// Onbekende velden worden genegeerd; het ruwe bericht blijft altijd zichtbaar in het live-log.
/// </summary>
public static class BetterTokEventParser
{
    private static readonly HashSet<string> ControlTypes = new(StringComparer.OrdinalIgnoreCase)
        { "connected", "subscribed", "pong", "error" };

    public static ParsedFrame Parse(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return new ParsedFrame { Kind = FrameKind.Unknown };

        // Cloud-envelope of control frame
        if (root.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String && !root.TryGetProperty("event", out _))
        {
            var type = typeEl.GetString() ?? "";
            if (ControlTypes.Contains(type))
            {
                return type.ToLowerInvariant() switch
                {
                    "connected" => new ParsedFrame { Kind = FrameKind.Connected, Username = Str(root, "username") },
                    "subscribed" => new ParsedFrame { Kind = FrameKind.Subscribed },
                    "pong" => new ParsedFrame { Kind = FrameKind.Pong },
                    _ => new ParsedFrame
                    {
                        Kind = FrameKind.Error,
                        ErrorCode = FirstNonEmpty(Str(root, "code"), root.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.Object ? Str(er, "code") : ""),
                        Message = FirstNonEmpty(Str(root, "message"), root.TryGetProperty("error", out var er2) && er2.ValueKind == JsonValueKind.Object ? Str(er2, "message") : "")
                    }
                };
            }
            var data = root.TryGetProperty("data", out var dEl) && dEl.ValueKind == JsonValueKind.Object ? dEl : default;
            return new ParsedFrame { Kind = FrameKind.Event, Event = FromCloud(type, Str(root, "id"), data, raw) };
        }

        // Local Mode (TikFinity-stijl): { event, data }
        if (root.TryGetProperty("event", out var evEl))
        {
            var name = evEl.ValueKind == JsonValueKind.String ? evEl.GetString() ?? "" : evEl.ToString();
            var data = root.TryGetProperty("data", out var dEl) && dEl.ValueKind == JsonValueKind.Object ? dEl : default;
            return new ParsedFrame { Kind = FrameKind.Event, Event = FromLocal(name, data, raw) };
        }

        // Fallback voor oudere varianten
        if (root.TryGetProperty("eventType", out var et))
        {
            var data = root.TryGetProperty("data", out var dEl) && dEl.ValueKind == JsonValueKind.Object ? dEl : root;
            return new ParsedFrame { Kind = FrameKind.Event, Event = FromLocal(et.ToString(), data, raw) };
        }
        return new ParsedFrame { Kind = FrameKind.Unknown };
    }

    public static string NormalizeType(string raw, JsonElement data = default)
    {
        var t = (raw ?? "").Trim().ToLowerInvariant();
        switch (t)
        {
            case "follow": return EventTypes.Follow;
            case "gift": return EventTypes.Gift;
            case "like": return EventTypes.Like;
            case "share": return EventTypes.Share;
            case "chat": case "comment": return EventTypes.Comment;
            case "member": case "join": return EventTypes.Member;
            case "subscribe": case "superfan": return EventTypes.Subscribe;
            case "social":
                // TikFinity-stijl 'social'-event: displayType bevat 'follow' of 'share'
                var display = data.ValueKind == JsonValueKind.Object ? Str(data, "displayType").ToLowerInvariant() : "";
                if (display.Contains("follow")) return EventTypes.Follow;
                if (display.Contains("share")) return EventTypes.Share;
                return EventTypes.Other;
            default: return EventTypes.Other;
        }
    }

    private static StreamEvent FromCloud(string type, string id, JsonElement d, string raw)
    {
        var norm = NormalizeType(type, d);
        var count = (int)Math.Max(1, Num(d, "gift_count") ?? 1);
        var per = Num(d, "gift_value") ?? 0;
        var total = Num(d, "gift_total_value") ?? per * count;
        if (norm != EventTypes.Gift) { count = 1; per = 0; total = Num(d, "coins") ?? 0; }
        return new StreamEvent
        {
            DedupeKey = string.IsNullOrEmpty(id) ? Hash(raw) : id,
            Type = norm,
            RawType = type,
            User = Str(d, "viewer_username"),
            DisplayName = Str(d, "display_name"),
            GiftName = Str(d, "gift_name"),
            GiftId = Str(d, "gift_id"),
            GiftCount = count,
            CoinsPerItem = per,
            TotalCoins = total,
            StreakInProgress = false, // Cloud stuurt een combo pas als die klaar is
            LikeCount = (int)(Num(d, "like_count") ?? (norm == EventTypes.Like ? 1 : 0)),
            Comment = Str(d, "comment"),
            Raw = raw,
            FromCloud = true
        };
    }

    private static StreamEvent FromLocal(string name, JsonElement d, string raw)
    {
        var norm = NormalizeType(name, d);
        var repeat = (int)Math.Max(1, Num(d, "repeatCount") ?? Num(d, "giftCount") ?? 1);
        var diamonds = Num(d, "diamondCount") ?? Num(d, "diamonds") ?? Num(d, "coins") ?? 0;
        var giftType = Num(d, "giftType");
        var repeatEnd = Bool(d, "repeatEnd");
        // Volgens TikTok-LIVE-conventie: alleen giftType 1 is 'streakable'. Zonder repeatEnd loopt de combo nog.
        var streak = norm == EventTypes.Gift && giftType == 1 && repeatEnd == false;
        var msgId = Str(d, "msgId");
        var user = FirstNonEmpty(Str(d, "uniqueId"), Str(d, "username"), Str(d, "userId"));
        return new StreamEvent
        {
            DedupeKey = string.IsNullOrEmpty(msgId) || msgId == "null"
                ? Hash(raw)
                : $"{name}:{msgId}:{(norm == EventTypes.Gift ? repeat + ":" + (repeatEnd == true ? "end" : "") : "")}",
            Type = norm,
            RawType = name,
            User = user,
            DisplayName = Str(d, "nickname"),
            GiftName = Str(d, "giftName"),
            GiftId = Str(d, "giftId"),
            GiftCount = norm == EventTypes.Gift ? repeat : 1,
            CoinsPerItem = norm == EventTypes.Gift ? diamonds : 0,
            TotalCoins = norm == EventTypes.Gift ? diamonds * repeat : 0,
            StreakInProgress = streak,
            LikeCount = (int)(Num(d, "likeCount") ?? (norm == EventTypes.Like ? 1 : 0)),
            Comment = FirstNonEmpty(Str(d, "comment"), norm == EventTypes.Comment ? Str(d, "text") : ""),
            Raw = raw,
            FromCloud = false
        };
    }

    // ---- helpers ----

    private static string Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }

    private static long? Num(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var dbl)) return (long)dbl;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    private static bool? Bool(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : null,
            JsonValueKind.Number => v.TryGetInt32(out var i) ? i != 0 : null,
            _ => null
        };
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private static string Hash(string raw) =>
        "raw:" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
}
