using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NivalisAIChat.Npc;

/// <summary>
/// Reads JSON the AI wrote (or a file someone edited) into the mod's own types without a wrong shape breaking it:
/// a number or a yes/no where text goes is read as that text, an object or a list as its text values; text where a
/// list goes is split on commas; null, or anything unreadable where a number, a yes/no or a date goes, is the default.
/// Read only: records are written with the built-in converters (<see cref="NpcStore.Json"/>), so nothing on disk changes.
/// </summary>
public static class TolerantJson
{
    /// <summary>A copy of <paramref name="baseOptions"/> that reads tolerantly (never used to write).</summary>
    public static JsonSerializerOptions For(JsonSerializerOptions baseOptions)
    {
        var o = new JsonSerializerOptions(baseOptions);
        o.Converters.Add(new Text());
        o.Converters.Add(new TextList());
        o.Converters.Add(new Int32Reader());
        o.Converters.Add(new Int64Reader());
        o.Converters.Add(new DoubleReader());
        o.Converters.Add(new BoolReader());
        o.Converters.Add(new DateReader());
        return o;
    }

    private static NotSupportedException ReadOnly() => new("These converters only read; records are written with NpcStore.Json.");

    /// <summary>A number exactly as written ("34", "2.0").</summary>
    private static string NumberText(ref Utf8JsonReader reader) =>
        Encoding.UTF8.GetString(reader.HasValueSequence ? System.Buffers.BuffersExtensions.ToArray(reader.ValueSequence) : reader.ValueSpan.ToArray());

    /// <summary>
    /// Everything written as text in the value at the reader: a scalar as its text, an object's or a list's values in
    /// order (keys left out). Leaves the reader on the value's last token.
    /// </summary>
    private static string Flatten(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String: return reader.GetString() ?? "";
            case JsonTokenType.Number: return NumberText(ref reader);
            case JsonTokenType.True: return "true";
            case JsonTokenType.False: return "false";
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                string separator = reader.TokenType == JsonTokenType.StartArray ? ", " : " ";
                int depth = reader.CurrentDepth;
                var parts = new List<string>();
                while (reader.Read())
                {
                    if (reader.CurrentDepth == depth && reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) break;
                    if (reader.TokenType is JsonTokenType.String or JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False)
                    {
                        string s = Flatten(ref reader).Trim();
                        if (s.Length > 0) parts.Add(s);
                    }
                }
                return string.Join(separator, parts);
            default: return ""; // null
        }
    }

    /// <summary>Text: as written; a number or a yes/no as its text; an object or a list as its text values; null as "".</summary>
    private sealed class Text : JsonConverter<string>
    {
        public override bool HandleNull => true;
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Flatten(ref reader);
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => throw ReadOnly();
    }

    /// <summary>
    /// A list of text: each text item (empty ones too), numbers and yes/no as text; nulls, lists and objects in it left
    /// out. Text where the list goes is split on commas, semicolons and line breaks; a single number or yes/no is one
    /// item; null (or an object) is an empty list.
    /// </summary>
    private sealed class TextList : JsonConverter<List<string>>
    {
        public override bool HandleNull => true;

        public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var list = new List<string>();
            switch (reader.TokenType)
            {
                case JsonTokenType.StartArray:
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        switch (reader.TokenType)
                        {
                            case JsonTokenType.String: list.Add(reader.GetString() ?? ""); break;
                            case JsonTokenType.Number: list.Add(NumberText(ref reader)); break;
                            case JsonTokenType.True: list.Add("true"); break;
                            case JsonTokenType.False: list.Add("false"); break;
                            case JsonTokenType.StartObject:
                            case JsonTokenType.StartArray: reader.Skip(); break;
                        }
                    }
                    break;
                case JsonTokenType.String:
                    foreach (var part in (reader.GetString() ?? "").Split(new[] { ',', ';', '\n', '\r' }))
                        if (part.Trim() is { Length: > 0 } item) list.Add(item);
                    break;
                case JsonTokenType.Number: list.Add(NumberText(ref reader)); break;
                case JsonTokenType.True: list.Add("true"); break;
                case JsonTokenType.False: list.Add("false"); break;
                case JsonTokenType.StartObject: reader.Skip(); break;
            }
            return list;
        }

        public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options) => throw ReadOnly();
    }

    /// <summary>A number as a double, from a number or numeric text; anything else is null (the caller's default).</summary>
    private static double? Number(ref Utf8JsonReader reader)
    {
        double d;
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetDouble(out d) && double.IsFinite(d) ? d : null;
            case JsonTokenType.String:
                return double.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && double.IsFinite(d) ? d : null;
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                reader.Skip();
                return null;
            default: return null;
        }
    }

    private sealed class Int32Reader : JsonConverter<int>
    {
        public override bool HandleNull => true;
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int exact)) return exact;
            return Number(ref reader) is { } d ? (int)Math.Clamp(Math.Round(d), int.MinValue, int.MaxValue) : 0;
        }
        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => throw ReadOnly();
    }

    private sealed class Int64Reader : JsonConverter<long>
    {
        public override bool HandleNull => true;
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long exact)) return exact;
            return Number(ref reader) is { } d ? (long)Math.Clamp(Math.Round(d), long.MinValue, long.MaxValue) : 0;
        }
        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => throw ReadOnly();
    }

    private sealed class DoubleReader : JsonConverter<double>
    {
        public override bool HandleNull => true;
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Number(ref reader) ?? 0;
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) => throw ReadOnly();
    }

    /// <summary>A yes/no: true/false, "true"/"yes"/"1" and "false"/"no"/"0", a number other than 0; anything else is no.</summary>
    private sealed class BoolReader : JsonConverter<bool>
    {
        public override bool HandleNull => true;
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.True: return true;
                case JsonTokenType.False: return false;
                case JsonTokenType.String:
                    return (reader.GetString() ?? "").Trim().ToLowerInvariant() is "true" or "yes" or "y" or "1";
                case JsonTokenType.Number:
                    return reader.TryGetDouble(out double d) && d != 0;
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    reader.Skip();
                    return false;
                default: return false;
            }
        }
        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => throw ReadOnly();
    }

    /// <summary>A date the built-in way (its kind kept); anything else is the default date.</summary>
    private sealed class DateReader : JsonConverter<DateTime>
    {
        public override bool HandleNull => true;
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && reader.TryGetDateTime(out var date)) return date;
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();
            return default;
        }
        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) => throw ReadOnly();
    }
}
