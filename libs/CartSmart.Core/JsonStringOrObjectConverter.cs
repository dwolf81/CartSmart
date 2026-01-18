using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CartSmart.API.Models
{
    /// <summary>
    /// Allows PostgREST json/jsonb columns to deserialize into a string property.
    /// Supports values returned as either a JSON string or a JSON object/array.
    /// </summary>
    public sealed class JsonStringOrObjectConverter : JsonConverter<string?>
    {
        public override bool CanWrite => true;

        public override string? ReadJson(
            JsonReader reader,
            Type objectType,
            string? existingValue,
            bool hasExistingValue,
            JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return null;

            if (reader.TokenType == JsonToken.String)
                return reader.Value?.ToString();

            if (reader.TokenType == JsonToken.StartObject || reader.TokenType == JsonToken.StartArray)
            {
                var token = JToken.Load(reader);
                return token.ToString(Formatting.None);
            }

            return reader.Value?.ToString();
        }

        public override void WriteJson(JsonWriter writer, string? value, JsonSerializer serializer)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                writer.WriteNull();
                return;
            }

            // If the string looks like JSON, write it as raw JSON; else write as normal string.
            var trimmed = value.Trim();
            if ((trimmed.StartsWith("{") && trimmed.EndsWith("}")) || (trimmed.StartsWith("[") && trimmed.EndsWith("]")))
            {
                try
                {
                    var token = JToken.Parse(trimmed);
                    token.WriteTo(writer);
                    return;
                }
                catch
                {
                    // fall through
                }
            }

            writer.WriteValue(value);
        }
    }
}
