// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace Funplay.Editor.Tools.Helpers
{
    // Captures execution separately from successful JSON delivery. A failed serialization must
    // never imply that the user command was not run or make retrying an unknown write seem safe.
    internal sealed class ScriptExecutionTrace
    {
        internal string Id = Guid.NewGuid().ToString("N");
        internal bool Executed;
        internal string Phase = "validation";
        internal Action<string> OnPhase;
        internal object Recovery;
        internal Newtonsoft.Json.Linq.JObject Diagnostic;
        internal void SetPhase(string phase) { Phase = phase; OnPhase?.Invoke(phase); }
    }

    internal sealed class ExecutionResultEnvelope
    {
        [JsonIgnore] internal ScriptExecutionTrace Trace;
        public bool success { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string message { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string code { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string error { get; private set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public object data { get; private set; }
        public bool executed => Trace.Executed;
        public string execution_id => Trace.Id;

        internal static ExecutionResultEnvelope Wrap(object value, ScriptExecutionTrace trace)
        {
            object Read(string name) => value?.GetType().GetProperty(name)?.GetValue(value);
            return new ExecutionResultEnvelope { Trace = trace, success = Read("success") is bool ok && ok,
                message = Read("message") as string, code = Read("code") as string,
                error = Read("error") as string, data = Read("data") };
        }
    }

    internal static class ToolResultSerializer
    {
        internal const int MaxBytes = 8 * 1024 * 1024;
        internal const int MaxDepth = 64;
        internal static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Error,
            TypeNameHandling = TypeNameHandling.None,
            Culture = CultureInfo.InvariantCulture,
            Converters = { new UnityValueConverter(), new UnityObjectReferenceConverter() }
        };

        internal static string Serialize(object value, string tool = null)
        {
            try { return SerializeChecked(value); }
            catch (Exception exception)
            {
                if (exception is OutOfMemoryException || exception is StackOverflowException) throw;
                var execution = value as ExecutionResultEnvelope;
                if (execution != null) execution.Trace.Diagnostic = Funplay.Editor.State.OperationDiagnostic.Capture("serializing", exception);
                string code = exception is ResultSizeException ? "RESULT_TOO_LARGE" : "RESULT_SERIALIZATION_FAILED";
                // Only fixed primitive metadata is used in this fallback; never call the failed
                // object's ToString(), traverse it again, or silently omit a problematic property.
                return Funplay.Editor.State.ExecutionJson.Serialize(Response.Error(code, new
                {
                    tool, executed = execution == null ? (bool?)null : execution.executed,
                    execution_id = execution?.execution_id, invocation_returned = true,
                    execution_status = execution == null ? "unknown" : execution.executed ? "returned" : "not_executed",
                    serialization_status = "failed", retry_safe = false,
                    exception_type = Short(exception.GetType().FullName, 256),
                    path = Short((exception as JsonSerializationException)?.Path, 256),
                    recovery = execution?.Trace.Recovery,
                    message = "Result delivery failed, not execution rollback. Inspect this execution; do not replay the command automatically.",
                    max_result_bytes = MaxBytes, max_depth = MaxDepth
                }));
            }
        }

        internal static string SerializeChecked(object value)
        {
            using (var text = new LimitedStringWriter(MaxBytes))
            using (var writer = new LimitedJsonWriter(text))
            {
                JsonSerializer.Create(Settings).Serialize(writer, value);
                writer.Flush();
                var json = text.ToString();
                if (Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new ResultSizeException();
                return json;
            }
        }

        internal static string Short(string text, int length) => text == null || text.Length <= length ? text : text.Substring(0, length) + "…";
        internal sealed class ResultSizeException : JsonSerializationException
        { internal ResultSizeException() : base("Result exceeds the bounded JSON output limit.") { } }

        private sealed class LimitedStringWriter : StringWriter
        {
            private readonly int limit;
            internal LimitedStringWriter(int limit) : base(CultureInfo.InvariantCulture) { this.limit = limit; }
            private void Check(int count) { if (count > limit - GetStringBuilder().Length) throw new ResultSizeException(); }
            public override void Write(char value) { Check(1); base.Write(value); }
            public override void Write(string value) { Check(value?.Length ?? 0); base.Write(value); }
            public override void Write(char[] buffer, int index, int count) { Check(count); base.Write(buffer, index, count); }
        }

        private sealed class LimitedJsonWriter : JsonTextWriter
        {
            internal LimitedJsonWriter(TextWriter text) : base(text) { CloseOutput = false; }
            private void CheckDepth() { if (Top >= MaxDepth) throw new JsonSerializationException("Result nesting exceeds the JSON depth limit."); }
            public override void WriteStartObject() { CheckDepth(); base.WriteStartObject(); }
            public override void WriteStartArray() { CheckDepth(); base.WriteStartArray(); }
            // Json.NET's JavaScript extensions/raw tokens bypass ordinary finite/depth checks
            // and are not interoperable JSON. Require callers to return structured values.
            public override void WriteRawValue(string json) => throw new JsonSerializationException("Raw JSON tokens are unsupported; return structured values.");
            public override void WriteUndefined() => throw new JsonSerializationException("Undefined is not a JSON value.");
            public override void WriteStartConstructor(string name) => throw new JsonSerializationException("JavaScript constructors are not JSON values.");
            public override void WriteComment(string text) => throw new JsonSerializationException("Comments are not JSON values.");
            public override void WriteValue(float value) { CheckFinite(value); base.WriteValue(value); }
            public override void WriteValue(double value) { CheckFinite(value); base.WriteValue(value); }
            public override void WriteValue(float? value) { if (value.HasValue) CheckFinite(value.Value); base.WriteValue(value); }
            public override void WriteValue(double? value) { if (value.HasValue) CheckFinite(value.Value); base.WriteValue(value); }
            public override void WriteValue(object value)
            {
                if (value is double d) CheckFinite(d);
                if (value is float f) CheckFinite(f);
                base.WriteValue(value);
            }
            private static void CheckFinite(double value)
            { if (double.IsNaN(value) || double.IsInfinity(value)) throw new JsonSerializationException("Non-finite JSON number."); }
        }

        private sealed class UnityObjectReferenceConverter : JsonConverter
        {
            public override bool CanConvert(Type type) => typeof(UnityEngine.Object).IsAssignableFrom(type);
            public override bool CanRead => false;
            public override object ReadJson(JsonReader reader, Type type, object existing, JsonSerializer serializer) => throw new NotSupportedException();
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var obj = value as UnityEngine.Object;
                if (obj == null) { writer.WriteNull(); return; }
                writer.WriteStartObject();
                writer.WritePropertyName("object_id"); writer.WriteValue(ObjectIdHelper.GetSerializableId(obj));
                writer.WritePropertyName("type"); writer.WriteValue(obj.GetType().FullName);
                writer.WritePropertyName("name"); writer.WriteValue(obj.name);
                writer.WriteEndObject();
            }
        }

        private sealed class UnityValueConverter : JsonConverter
        {
            private static readonly HashSet<Type> Types = new HashSet<Type> {
                typeof(Vector2), typeof(Vector3), typeof(Vector4), typeof(Vector2Int), typeof(Vector3Int),
                typeof(Color), typeof(Color32), typeof(Quaternion), typeof(Rect), typeof(RectInt),
                typeof(Bounds), typeof(BoundsInt), typeof(Ray), typeof(Ray2D), typeof(Plane), typeof(Pose), typeof(Matrix4x4) };
            public override bool CanConvert(Type type) => Types.Contains(type);
            public override bool CanRead => false;
            public override object ReadJson(JsonReader reader, Type type, object existing, JsonSerializer serializer) => throw new NotSupportedException();
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                writer.WriteStartObject();
                void Number(string name, double number) { writer.WritePropertyName(name); writer.WriteValue(number); }
                void Nested(string name, object child) { writer.WritePropertyName(name); serializer.Serialize(writer, child); }
                // Explicit field contracts avoid computed properties such as Vector3.normalized,
                // Quaternion.eulerAngles and Color.linear recursively serializing more value types.
                if (value is Vector2 a) { Number("x", a.x); Number("y", a.y); }
                else if (value is Vector3 b) { Number("x", b.x); Number("y", b.y); Number("z", b.z); }
                else if (value is Vector4 c) { Number("x", c.x); Number("y", c.y); Number("z", c.z); Number("w", c.w); }
                else if (value is Vector2Int d) { Number("x", d.x); Number("y", d.y); }
                else if (value is Vector3Int e) { Number("x", e.x); Number("y", e.y); Number("z", e.z); }
                else if (value is Color f) { Number("r", f.r); Number("g", f.g); Number("b", f.b); Number("a", f.a); }
                else if (value is Color32 g) { Number("r", g.r); Number("g", g.g); Number("b", g.b); Number("a", g.a); }
                else if (value is Quaternion h) { Number("x", h.x); Number("y", h.y); Number("z", h.z); Number("w", h.w); }
                else if (value is Rect i) { Number("x", i.x); Number("y", i.y); Number("width", i.width); Number("height", i.height); }
                else if (value is RectInt j) { Number("x", j.x); Number("y", j.y); Number("width", j.width); Number("height", j.height); }
                else if (value is Bounds k) { Nested("center", k.center); Nested("size", k.size); }
                else if (value is BoundsInt l) { Nested("position", l.position); Nested("size", l.size); }
                else if (value is Ray m) { Nested("origin", m.origin); Nested("direction", m.direction); }
                else if (value is Ray2D n) { Nested("origin", n.origin); Nested("direction", n.direction); }
                else if (value is Plane o) { Nested("normal", o.normal); Number("distance", o.distance); }
                else if (value is Pose p) { Nested("position", p.position); Nested("rotation", p.rotation); }
                else if (value is Matrix4x4 matrix)
                    for (int row = 0; row < 4; row++) for (int col = 0; col < 4; col++) Number("m" + row + col, matrix[row, col]);
                writer.WriteEndObject();
            }
        }
    }
}
