using System.Globalization;
using System.Text.Json;
using MemSrv.Core;

namespace CaptureAdapters;

internal static class JsonAdapterHelpers
{
    public static JsonElement Clone(JsonElement value) => value.Clone();

    public static JsonElement Json(object? value) =>
        JsonSerializer.SerializeToElement(value, JsonDefaults.Options);

    public static CaptureSourceTimestamp? SourceTimestamp(JsonElement record)
    {
        if (!TryGetString(record, "timestamp", out var raw))
        {
            return null;
        }

        DateTimeOffset? parsed = DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var value)
            ? value
            : null;
        return new CaptureSourceTimestamp(raw, parsed);
    }

    public static string? NullableString(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object when value.TryGetProperty("name", out var nested)
                && nested.ValueKind == JsonValueKind.String => nested.GetString(),
            _ => null
        };
    }

    public static bool TryGetString(JsonElement owner, string name, out string value)
    {
        value = "";
        if (!owner.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || property.GetString() is not { } text)
        {
            return false;
        }

        value = text;
        return true;
    }

    public static string Text(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Array => string.Join(
                "\n",
                value.EnumerateArray().Select(ContentPartText).Where(text => text is not null)),
            JsonValueKind.Object => ContentPartText(value) ?? value.GetRawText(),
            JsonValueKind.Null => "",
            _ => value.GetRawText()
        };

    public static JsonElement ObjectOrParsedString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return value.Clone();
        }

        string text = value.GetString() ?? "";
        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            return Json(text);
        }
    }

    private static string? ContentPartText(JsonElement part)
    {
        if (part.ValueKind == JsonValueKind.String)
        {
            return part.GetString();
        }
        if (part.ValueKind != JsonValueKind.Object)
        {
            return part.GetRawText();
        }
        if (TryGetString(part, "text", out var text))
        {
            return text;
        }
        if (part.TryGetProperty("content", out var content))
        {
            return Text(content);
        }
        return part.GetRawText();
    }
}

internal static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } =
        new(JsonSerializerDefaults.Web);
}
