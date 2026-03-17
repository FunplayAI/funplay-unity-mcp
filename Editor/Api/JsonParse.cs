// Copyright (C) GameBooom. Licensed under GPLv3.
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GameBooom.Editor.Api
{
    /// <summary>
    /// Minimal JSON utility for MCP protocol communication.
    /// </summary>
    internal static class JsonParse
    {
        private static readonly JsonSerializerSettings SerializeSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        public static string Serialize(object obj)
        {
            if (obj == null) return "null";
            return JsonConvert.SerializeObject(obj, SerializeSettings);
        }

        internal static Dictionary<string, object> ParseJsonObject(string json, int start, out int end)
        {
            end = json?.Length ?? 0;
            if (string.IsNullOrEmpty(json)) return null;

            try
            {
                var substring = start > 0 ? json.Substring(start) : json;
                var token = JToken.Parse(substring);
                end = start + substring.Length;
                return ConvertToDictionary(token);
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, object> ConvertToDictionary(JToken token)
        {
            if (token == null || token.Type != JTokenType.Object) return null;

            var dict = new Dictionary<string, object>();
            foreach (var prop in ((JObject)token).Properties())
                dict[prop.Name] = ConvertToken(prop.Value);
            return dict;
        }

        private static object ConvertToken(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Object:
                    var dict = new Dictionary<string, object>();
                    foreach (var prop in ((JObject)token).Properties())
                        dict[prop.Name] = ConvertToken(prop.Value);
                    return dict;
                case JTokenType.Array:
                    var list = new List<object>();
                    foreach (var item in (JArray)token)
                        list.Add(ConvertToken(item));
                    return list;
                case JTokenType.Integer:
                    return (long)token;
                case JTokenType.Float:
                    return (double)token;
                case JTokenType.String:
                    return (string)token;
                case JTokenType.Boolean:
                    return (bool)token;
                case JTokenType.Null:
                    return null;
                default:
                    return token.ToString();
            }
        }
    }
}
