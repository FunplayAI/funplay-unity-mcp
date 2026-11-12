// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Globalization;
using System.IO;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Funplay.Editor.State
{
    // JsonConvert and JObject.FromObject(value) consult project-wide DefaultSettings, even
    // when some explicit settings are passed. Our persistence/protocol DTOs must not do so.
    internal static class ExecutionJson
    {
        internal static JsonSerializer Serializer() => JsonSerializer.Create(new JsonSerializerSettings {
            TypeNameHandling = TypeNameHandling.None, MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            Culture = CultureInfo.InvariantCulture, DateParseHandling = DateParseHandling.None,
            ReferenceLoopHandling = ReferenceLoopHandling.Error, MaxDepth = ToolResultSerializer.MaxDepth });

        internal static string Serialize(object value)
        {
            using (var text = new StringWriter(CultureInfo.InvariantCulture))
            using (var writer = new JsonTextWriter(text))
            { Serializer().Serialize(writer, value); writer.Flush(); return text.ToString(); }
        }
        internal static JObject Object(object value) => JObject.FromObject(value, Serializer());
        internal static T Copy<T>(T value) => value == null ? default : Object(value).ToObject<T>(Serializer());
        internal static JObject Parse(string text)
        {
            using (var input = new StringReader(text))
            using (var reader = new JsonTextReader(input) { MaxDepth = ToolResultSerializer.MaxDepth, DateParseHandling = DateParseHandling.None })
            {
                var result = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new JsonSerializationException("Unexpected trailing journal content.");
                return result;
            }
        }
    }
}
