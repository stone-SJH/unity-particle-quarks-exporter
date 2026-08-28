using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UnityParticleQuarksExporter.Editor
{
    internal abstract class JsonValue
    {
        internal abstract void WriteTo(StringBuilder builder);

        public override string ToString()
        {
            var builder = new StringBuilder(4096);
            WriteTo(builder);
            return builder.ToString();
        }
    }

    internal sealed class JsonObject : JsonValue
    {
        private readonly List<KeyValuePair<string, JsonValue>> values = new List<KeyValuePair<string, JsonValue>>();

        public JsonObject Add(string key, JsonValue value)
        {
            values.Add(new KeyValuePair<string, JsonValue>(key, value ?? Json.Null));
            return this;
        }

        public JsonObject Set(string key, JsonValue value)
        {
            for (var index = 0; index < values.Count; index++)
            {
                if (!string.Equals(values[index].Key, key, StringComparison.Ordinal)) continue;
                values[index] = new KeyValuePair<string, JsonValue>(key, value ?? Json.Null);
                return this;
            }
            return Add(key, value);
        }

        internal override void WriteTo(StringBuilder builder)
        {
            builder.Append('{');
            for (var index = 0; index < values.Count; index++)
            {
                if (index > 0) builder.Append(',');
                Json.WriteString(builder, values[index].Key);
                builder.Append(':');
                values[index].Value.WriteTo(builder);
            }
            builder.Append('}');
        }
    }

    internal sealed class JsonArray : JsonValue
    {
        private readonly List<JsonValue> values = new List<JsonValue>();

        public JsonArray Add(JsonValue value)
        {
            values.Add(value ?? Json.Null);
            return this;
        }

        internal override void WriteTo(StringBuilder builder)
        {
            builder.Append('[');
            for (var index = 0; index < values.Count; index++)
            {
                if (index > 0) builder.Append(',');
                values[index].WriteTo(builder);
            }
            builder.Append(']');
        }
    }

    internal sealed class JsonPrimitive : JsonValue
    {
        private readonly object value;

        public JsonPrimitive(object valueToWrite)
        {
            value = valueToWrite;
        }

        internal override void WriteTo(StringBuilder builder)
        {
            if (value == null)
            {
                builder.Append("null");
            }
            else if (value is string stringValue)
            {
                Json.WriteString(builder, stringValue);
            }
            else if (value is bool boolValue)
            {
                builder.Append(boolValue ? "true" : "false");
            }
            else if (value is float floatValue)
            {
                builder.Append(floatValue.ToString("R", CultureInfo.InvariantCulture));
            }
            else if (value is double doubleValue)
            {
                builder.Append(doubleValue.ToString("R", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
            }
        }
    }

    internal static class Json
    {
        public static readonly JsonValue Null = new JsonPrimitive(null);

        public static JsonObject Object() => new JsonObject();
        public static JsonArray Array() => new JsonArray();
        public static JsonValue String(string value) => new JsonPrimitive(value ?? string.Empty);
        public static JsonValue Number(float value) => new JsonPrimitive(value);
        public static JsonValue Number(double value) => new JsonPrimitive(value);
        public static JsonValue Number(int value) => new JsonPrimitive(value);
        public static JsonValue Number(uint value) => new JsonPrimitive(value);
        public static JsonValue Boolean(bool value) => new JsonPrimitive(value);

        internal static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (var character in value ?? string.Empty)
            {
                switch (character)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < 32)
                        {
                            builder.Append("\\u");
                            builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(character);
                        }
                        break;
                }
            }
            builder.Append('"');
        }
    }

    public static class UnityParticleQuarksStableId
    {
        public static string Create(string sourcePath, string componentPath, string semanticSlot)
        {
            var key = "unity_particle_quarks_pipeline.v1\n" + Normalize(sourcePath) + "\n" + Normalize(componentPath) + "\n" + semanticSlot;
            byte[] hash;
            using (var sha256 = SHA256.Create())
            {
                hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(key));
            }

            var guidBytes = new byte[16];
            Array.Copy(hash, guidBytes, guidBytes.Length);
            guidBytes[7] = (byte)((guidBytes[7] & 0x0f) | 0x50);
            guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
            return new Guid(guidBytes).ToString("D");
        }

        public static string Hash(string value)
        {
            byte[] hash;
            using (var sha256 = SHA256.Create())
            {
                hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
            }
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var item in hash) builder.Append(item.ToString("x2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Replace('\\', '/').Trim();
        }
    }
}
