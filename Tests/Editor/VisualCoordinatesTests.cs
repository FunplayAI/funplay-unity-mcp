// Copyright (C) Funplay. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.Tools.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Funplay.Editor.Tests
{
    public sealed class VisualCoordinatesTests
    {
        private VisualGeometry Geometry() => new VisualGeometry {
            capture_id = "test", surface = "game_view", interactive = true, render_width = 1000, render_height = 500,
            output_width = 500, output_height = 250, viewport = new VisualRect { width = 500, height = 250 },
            editor_epoch = "epoch", view_id = "view", scene_signature = "scene", camera_signature = "camera", is_playing = true
        };
        [TestCase("image_pixels", "top_left", 125f, 50f, 250f, 400f)]
        [TestCase("image_pixels", "bottom_left", 125f, 50f, 250f, 100f)]
        [TestCase("normalized", "top_left", .25f, .2f, 250f, 400f)]
        [TestCase("normalized", "bottom_left", .25f, .2f, 250f, 100f)]
        [TestCase("render_pixels", "bottom_left", 125f, 50f, 125f, 50f)]
        public void CoordinateSpacesShareOneMapping(string space, string origin, float x, float y, float expectedX, float expectedY)
        {
            Assert.IsTrue(VisualCoordinates.TryConvert(x, y, space, origin, Geometry(), out var point, out var error), error);
            Assert.AreEqual(expectedX, point.x, .001f); Assert.AreEqual(expectedY, point.y, .001f);
        }
        [Test] public void LetterboxedImageViewportIsExplicitAndBarsAreRejected()
        {
            var g = Geometry(); g.output_height = 300; g.viewport.y = 25;
            Assert.IsTrue(VisualCoordinates.TryConvert(250, 150, "image_pixels", "top_left", g, out var p, out _));
            Assert.AreEqual(new Vector2(500, 250), p);
            Assert.IsFalse(VisualCoordinates.TryConvert(250, 10, "image_pixels", "top_left", g, out _, out var error));
            Assert.AreEqual("POINT_OUTSIDE_RENDER_VIEWPORT", error);
        }
        [TestCase("render_pixels", "bad", 1f, 1f)]
        [TestCase("bad", "bottom_left", 1f, 1f)]
        [TestCase("normalized", "bottom_left", 2f, 1f)]
        [TestCase("image_pixels", "top_left", -1f, 1f)]
        public void InvalidCoordinatesAreNotClampedToAnotherTarget(string space, string origin, float x, float y)
            => Assert.IsFalse(VisualCoordinates.TryConvert(x, y, space, origin, Geometry(), out _, out _));
        [Test] public void NonFiniteCoordinatesAreRejected()
        {
            Assert.IsFalse(VisualCoordinates.TryConvert(float.NaN, 0, "render_pixels", "bottom_left", Geometry(), out _, out _));
            Assert.IsFalse(VisualCoordinates.TryConvert(float.PositiveInfinity, 0, "render_pixels", "bottom_left", Geometry(), out _, out _));
        }
        [TestCase("editor_epoch")][TestCase("view_id")][TestCase("scene_signature")][TestCase("camera_signature")]
        public void CaptureContextChangeInvalidatesInteraction(string field)
        {
            var capture = Geometry(); var current = Geometry(); typeof(VisualGeometry).GetField(field).SetValue(current, "changed");
            Assert.AreEqual("CAPTURE_GEOMETRY_CHANGED", VisualCoordinates.Validate(capture, current, 1));
        }
        [Test] public void ResizeAndPlayModeChangeInvalidateCapture()
        {
            var capture = Geometry(); var current = Geometry(); current.render_width++;
            Assert.AreEqual("CAPTURE_GEOMETRY_CHANGED", VisualCoordinates.Validate(capture, current, 1));
            current = Geometry(); current.is_playing = false;
            Assert.AreEqual("CAPTURE_GEOMETRY_CHANGED", VisualCoordinates.Validate(capture, current, 1));
        }
        [Test] public void StaleAndNonInteractiveReceiptsAreRejected()
        {
            var g = Geometry(); Assert.IsNull(VisualCoordinates.Validate(g, Geometry(), 1));
            Assert.AreEqual("STALE_CAPTURE", VisualCoordinates.Validate(g, Geometry(), 31));
            Assert.AreEqual("STALE_CAPTURE", VisualCoordinates.Validate(g, Geometry(), -1));
            g.interactive = false; Assert.AreEqual("CAPTURE_NOT_INTERACTIVE", VisualCoordinates.Validate(g, Geometry(), 1));
        }
        [Test] public void ImageProjectionRoundTrips()
        {
            var g = Geometry(); var render = new Vector2(321, 123); var image = VisualCoordinates.ToImage(render, g, "top_left");
            Assert.IsTrue(VisualCoordinates.TryConvert(image.x, image.y, "image_pixels", "top_left", g, out var actual, out _));
            Assert.AreEqual(render.x, actual.x, .001); Assert.AreEqual(render.y, actual.y, .001);
        }
        [Test] public void CameraProjectionIncludesViewportOffset()
        {
            var go = new GameObject("CoordinateCamera", typeof(Camera));
            try
            {
                var camera = go.GetComponent<Camera>(); camera.transform.position = new Vector3(0, 0, -10);
                camera.rect = new Rect(.25f, .25f, .5f, .5f);
                var point = VisualCoordinates.WorldToRender(Vector3.zero, camera, Geometry());
                Assert.AreEqual(500, point.x, .1); Assert.AreEqual(250, point.y, .1);
            }
            finally { Object.DestroyImmediate(go); }
        }
        [Test] public void CaptureReceiptKeepsNativeMcpImageAndStructuredMetadataWithoutBase64InLog()
        {
            var response = JsonConvert.SerializeObject(Response.Success("captured", new { geometry = Geometry(), inline_image = "data:image/png;base64,iVBORw0KGgo=" }, new { funplay_capture = 1 }));
            var handler = FormatterServices.GetUninitializedObject(typeof(MCPRequestHandler));
            var method = typeof(MCPRequestHandler).GetMethod("BuildContentFromResult", BindingFlags.NonPublic | BindingFlags.Instance);
            var content = (List<Dictionary<string, object>>)method.Invoke(handler, new object[] { response });
            Assert.AreEqual("image", content[0]["type"]); Assert.AreEqual("iVBORw0KGgo=", content[0]["data"]);
            var receipt = JObject.Parse((string)content[1]["text"]);
            Assert.AreEqual("test", (string)receipt["data"]["geometry"]["capture_id"]); Assert.IsNull(receipt["data"]["inline_image"]);
            Assert.IsFalse(VisualCoordinates.WithoutInlineImage(response).Contains("iVBORw0KGgo="));
        }
        [Test] public void OrdinaryToolJsonIsNotReinterpretedAsImage()
        {
            var ordinary = "{\"data\":{\"inline_image\":\"abc\"}}";
            Assert.AreEqual(ordinary, VisualCoordinates.WithoutInlineImage(ordinary));
        }
    }
}
