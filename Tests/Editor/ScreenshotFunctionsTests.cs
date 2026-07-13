// Copyright (C) Funplay. Licensed under MIT.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Funplay.Editor.MCP.Server;
using Funplay.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEngine;

namespace Funplay.Editor.Tests
{
    public sealed class ScreenshotFunctionsTests
    {
        [Test]
        public void DrawSafeAreaOverlay_DrawsScaledOutline()
        {
            var texture = new Texture2D(50, 100, TextureFormat.RGB24, false);
            try
            {
                Fill(texture, Color.black);

                ScreenshotFunctions.DrawSafeAreaOverlay(
                    texture,
                    new Rect(10, 20, 80, 160),
                    sourceWidth: 100,
                    sourceHeight: 200);

                AssertGreen(texture.GetPixel(5, 10));
                AssertGreen(texture.GetPixel(45, 90));
                AssertGreen(texture.GetPixel(25, 10));
                AssertGreen(texture.GetPixel(5, 50));
                AssertBlack(texture.GetPixel(25, 50));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void FlipTextureVertically_SwapsRows()
        {
            var texture = new Texture2D(2, 3, TextureFormat.RGB24, false);
            try
            {
                texture.SetPixel(0, 0, Color.red);
                texture.SetPixel(1, 0, Color.red);
                texture.SetPixel(0, 1, Color.green);
                texture.SetPixel(1, 1, Color.green);
                texture.SetPixel(0, 2, Color.blue);
                texture.SetPixel(1, 2, Color.blue);
                texture.Apply();

                ScreenshotFunctions.FlipTextureVertically(texture);

                AssertBlue(texture.GetPixel(0, 0));
                AssertGreen(texture.GetPixel(1, 1));
                AssertRed(texture.GetPixel(1, 2));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void ReadTextureToTexture2D_WhenFlipRequested_MirrorsUnflippedRows()
        {
            var sourcePixels = new Texture2D(2, 3, TextureFormat.RGBA32, false);
            var source = new RenderTexture(2, 3, 0, RenderTextureFormat.ARGB32);
            Texture2D unflipped = null;
            Texture2D flipped = null;
            try
            {
                sourcePixels.SetPixel(0, 0, Color.red);
                sourcePixels.SetPixel(1, 0, Color.red);
                sourcePixels.SetPixel(0, 1, Color.green);
                sourcePixels.SetPixel(1, 1, Color.green);
                sourcePixels.SetPixel(0, 2, Color.blue);
                sourcePixels.SetPixel(1, 2, Color.blue);
                sourcePixels.Apply();
                source.Create();
                Graphics.CopyTexture(sourcePixels, source);

                unflipped = ScreenshotFunctions.ReadTextureToTexture2D(source, 2, 3, flipVertically: false);
                flipped = ScreenshotFunctions.ReadTextureToTexture2D(source, 2, 3, flipVertically: true);

                AssertColorClose(unflipped.GetPixel(0, 2), flipped.GetPixel(0, 0));
                AssertColorClose(unflipped.GetPixel(1, 1), flipped.GetPixel(1, 1));
                AssertColorClose(unflipped.GetPixel(1, 0), flipped.GetPixel(1, 2));
            }
            finally
            {
                if (unflipped != null)
                    UnityEngine.Object.DestroyImmediate(unflipped);
                if (flipped != null)
                    UnityEngine.Object.DestroyImmediate(flipped);
                if (source != null)
                    source.Release();
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(sourcePixels);
            }
        }

        [Test]
        public void ShouldFlipPlayModeViewRenderTexture_DefaultsToTrue()
        {
            Assert.IsTrue(ScreenshotFunctions.ShouldFlipPlayModeViewRenderTexture());
        }

        [Test]
        public void ShouldFlipCameraRenderTexture_DefaultsToFalse()
        {
            Assert.IsFalse(ScreenshotFunctions.ShouldFlipCameraRenderTexture());
        }

        [Test]
        public void ReadActiveRenderTextureToTexture2D_WhenFlipRequested_MirrorsUnflippedRows()
        {
            var sourcePixels = new Texture2D(2, 3, TextureFormat.RGBA32, false);
            var source = new RenderTexture(2, 3, 0, RenderTextureFormat.ARGB32);
            var previousActive = RenderTexture.active;
            Texture2D unflipped = null;
            Texture2D flipped = null;

            try
            {
                sourcePixels.SetPixel(0, 0, Color.red);
                sourcePixels.SetPixel(1, 0, Color.red);
                sourcePixels.SetPixel(0, 1, Color.green);
                sourcePixels.SetPixel(1, 1, Color.green);
                sourcePixels.SetPixel(0, 2, Color.blue);
                sourcePixels.SetPixel(1, 2, Color.blue);
                sourcePixels.Apply();
                source.Create();
                Graphics.CopyTexture(sourcePixels, source);

                RenderTexture.active = source;
                unflipped = ScreenshotFunctions.ReadActiveRenderTextureToTexture2D(2, 3, flipVertically: false);
                flipped = ScreenshotFunctions.ReadActiveRenderTextureToTexture2D(2, 3, flipVertically: true);

                AssertColorClose(unflipped.GetPixel(0, 2), flipped.GetPixel(0, 0));
                AssertColorClose(unflipped.GetPixel(1, 1), flipped.GetPixel(1, 1));
                AssertColorClose(unflipped.GetPixel(1, 0), flipped.GetPixel(1, 2));
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (unflipped != null)
                    UnityEngine.Object.DestroyImmediate(unflipped);
                if (flipped != null)
                    UnityEngine.Object.DestroyImmediate(flipped);
                if (source != null)
                    source.Release();
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(sourcePixels);
            }
        }

        [Test]
        public void CoreToolProfile_IncludesSimulatorScreenshot()
        {
            Assert.IsTrue(MCPToolExportPolicy.DefaultCoreTools.Contains("capture_simulator_view"));
        }

        [Test]
        public void CoreToolProfile_IncludesEditorWindowAndRaycastDiagnostics()
        {
            Assert.IsTrue(MCPToolExportPolicy.DefaultCoreTools.Contains("capture_editor_window"));
            Assert.IsTrue(MCPToolExportPolicy.DefaultCoreTools.Contains("raycast_at_point"));
        }

        [Test]
        public void CaptureSimulatorView_ExposesDeviceNameParameter()
        {
            var method = typeof(ScreenshotFunctions).GetMethod(
                "CaptureSimulatorView",
                BindingFlags.Public | BindingFlags.Static);

            Assert.IsNotNull(method);
            Assert.IsNotNull(method.GetParameters().FirstOrDefault(p => p.Name == "device_name"));
        }

        [Test]
        public void NormalizeDeviceName_RemovesSpacingAndPunctuation()
        {
            Assert.AreEqual(
                "appleipadpro1292018",
                ScreenshotFunctions.NormalizeDeviceName("Apple iPad Pro 12.9 (2018)"));
            Assert.AreEqual("iphone12", ScreenshotFunctions.NormalizeDeviceName("iPhone 12"));
        }

        [Test]
        public void ResolveCaptureSize_PreservesAspectWhenOnlyOneDimensionProvided()
        {
            var widthOnly = 390;
            var heightOnly = 0;
            ScreenshotFunctions.ResolveCaptureSize(ref widthOnly, ref heightOnly, 1170, 2532);
            Assert.AreEqual(390, widthOnly);
            Assert.AreEqual(844, heightOnly);

            var widthFromHeight = 0;
            var requestedHeight = 683;
            ScreenshotFunctions.ResolveCaptureSize(ref widthFromHeight, ref requestedHeight, 2048, 2732);
            Assert.AreEqual(512, widthFromHeight);
            Assert.AreEqual(683, requestedHeight);
        }

        [Test]
        public void ShouldSpillScreenshotToFile_UsesCombinedRawByteThreshold()
        {
            Assert.IsFalse(ScreenshotFunctions.ShouldSpillScreenshotToFile(
                ScreenshotFunctions.MaxInlineScreenshotBytes,
                saveToFile: false));
            Assert.IsTrue(ScreenshotFunctions.ShouldSpillScreenshotToFile(
                ScreenshotFunctions.MaxInlineScreenshotBytes + 1L,
                saveToFile: false));
            Assert.IsTrue(ScreenshotFunctions.ShouldSpillScreenshotToFile(1, saveToFile: true));
        }

        [Test]
        public void TryResolveScreenshotOutputPath_DefaultsInsideProjectRoot()
        {
            var projectRoot = MakeTempProjectRoot();

            Assert.IsTrue(ScreenshotFunctions.TryResolveScreenshotOutputPath(
                null,
                "game-view",
                projectRoot,
                out var path,
                out var error));

            Assert.IsNull(error);
            AssertPathInside(path, projectRoot);
            StringAssert.StartsWith("game-view-", Path.GetFileName(path));
            StringAssert.EndsWith(".png", path);
        }

        [Test]
        public void TryResolveScreenshotOutputPath_AcceptsRelativeProjectPath()
        {
            var projectRoot = MakeTempProjectRoot();

            Assert.IsTrue(ScreenshotFunctions.TryResolveScreenshotOutputPath(
                "Library/FunplayMcp/Screenshots/custom.png",
                "game-view",
                projectRoot,
                out var path,
                out var error));

            Assert.IsNull(error);
            Assert.AreEqual(
                Path.GetFullPath(Path.Combine(projectRoot, "Library/FunplayMcp/Screenshots/custom.png")),
                path);
        }

        [Test]
        public void TryResolveScreenshotOutputPath_AcceptsAbsoluteProjectPath()
        {
            var projectRoot = MakeTempProjectRoot();
            var outputPath = Path.Combine(projectRoot, "Library", "FunplayMcp", "Screenshots", "absolute.png");

            Assert.IsTrue(ScreenshotFunctions.TryResolveScreenshotOutputPath(
                outputPath,
                "game-view",
                projectRoot,
                out var path,
                out var error));

            Assert.IsNull(error);
            Assert.AreEqual(Path.GetFullPath(outputPath), path);
        }

        [Test]
        public void TryResolveScreenshotOutputPath_RejectsTraversalOutsideProjectRoot()
        {
            var projectRoot = MakeTempProjectRoot();

            Assert.IsFalse(ScreenshotFunctions.TryResolveScreenshotOutputPath(
                "../outside.png",
                "game-view",
                projectRoot,
                out var path,
                out var error));

            Assert.IsNull(path);
            Assert.IsNotNull(error);
        }

        [Test]
        public void TryResolveScreenshotOutputPath_RejectsAbsoluteOutsideProjectRoot()
        {
            var projectRoot = MakeTempProjectRoot();
            var outputPath = Path.GetFullPath(Path.Combine(projectRoot, "..", "outside.png"));

            Assert.IsFalse(ScreenshotFunctions.TryResolveScreenshotOutputPath(
                outputPath,
                "game-view",
                projectRoot,
                out var path,
                out var error));

            Assert.IsNull(path);
            Assert.IsNotNull(error);
        }

        [Test]
        public void TryResolveScreenshotOutputPath_RejectsNonPngExtension()
        {
            var projectRoot = MakeTempProjectRoot();

            Assert.IsFalse(ScreenshotFunctions.TryResolveScreenshotOutputPath(
                "Library/FunplayMcp/Screenshots/custom.jpg",
                "game-view",
                projectRoot,
                out var path,
                out var error));

            Assert.IsNull(path);
            Assert.IsNotNull(error);
        }

        [Test]
        public void DeviceSimulatorReflection_ResolvesPreviewTexturePathWhenAvailable()
        {
            var simulatorWindowType = ResolveType(
                "UnityEditor.DeviceSimulation.SimulatorWindow",
                "UnityEditor.DeviceSimulatorModule");
            if (simulatorWindowType == null)
                Assert.Ignore("Unity Device Simulator module is not available in this editor.");

            var directDeviceViewMember = GetMember(simulatorWindowType, "DeviceView")
                                         ?? GetMember(simulatorWindowType, "m_DeviceView");
            if (directDeviceViewMember != null)
            {
                AssertPreviewTextureMemberExists(GetMemberType(directDeviceViewMember));
                return;
            }

            var mainMember = GetMember(simulatorWindowType, "main");
            Assert.IsNotNull(mainMember, "SimulatorWindow.main could not be resolved.");

            var mainType = GetMemberType(mainMember);
            var userInterfaceMember = GetMember(mainType, "userInterface")
                                      ?? GetMember(mainType, "ui")
                                      ?? GetMember(mainType, "m_UserInterfaceController")
                                      ?? GetMember(mainType, "userInterfaceController");
            Assert.IsNotNull(userInterfaceMember, "SimulatorWindow.main user interface controller could not be resolved.");

            var userInterfaceType = GetMemberType(userInterfaceMember);
            var deviceViewMember = GetMember(userInterfaceType, "DeviceView")
                                   ?? GetMember(userInterfaceType, "m_DeviceView");
            Assert.IsNotNull(deviceViewMember, "Device Simulator DeviceView could not be resolved.");
            AssertPreviewTextureMemberExists(GetMemberType(deviceViewMember));
        }

        private static void Fill(Texture2D texture, Color color)
        {
            for (var y = 0; y < texture.height; y++)
            {
                for (var x = 0; x < texture.width; x++)
                    texture.SetPixel(x, y, color);
            }
            texture.Apply();
        }

        private static void AssertGreen(Color color)
        {
            Assert.Greater(color.g, 0.8f);
            Assert.Less(color.r, 0.4f);
            Assert.Less(color.b, 0.5f);
        }

        private static void AssertRed(Color color)
        {
            Assert.Greater(color.r, 0.8f);
            Assert.Less(color.g, 0.4f);
            Assert.Less(color.b, 0.4f);
        }

        private static void AssertBlue(Color color)
        {
            Assert.Greater(color.b, 0.8f);
            Assert.Less(color.r, 0.4f);
            Assert.Less(color.g, 0.4f);
        }

        private static void AssertBlack(Color color)
        {
            Assert.Less(color.r, 0.1f);
            Assert.Less(color.g, 0.1f);
            Assert.Less(color.b, 0.1f);
        }

        private static void AssertColorClose(Color expected, Color actual)
        {
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(0.01f));
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(0.01f));
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(0.01f));
        }

        private static string MakeTempProjectRoot()
        {
            return Path.Combine(Path.GetTempPath(), "FunplayMcpScreenshotPathTests", Guid.NewGuid().ToString("N"));
        }

        private static void AssertPathInside(string path, string root)
        {
            var normalizedRoot = Path.GetFullPath(root);
            if (!normalizedRoot.EndsWith(Path.DirectorySeparatorChar.ToString()))
                normalizedRoot += Path.DirectorySeparatorChar;

            StringAssert.StartsWith(normalizedRoot, Path.GetFullPath(path));
        }

        private static Type ResolveType(string fullName, string assemblyName)
        {
            return Type.GetType($"{fullName},{assemblyName}") ?? Type.GetType(fullName);
        }

        private static MemberInfo GetMember(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            return (MemberInfo)type.GetProperty(name, flags) ?? type.GetField(name, flags);
        }

        private static Type GetMemberType(MemberInfo member)
        {
            var property = member as PropertyInfo;
            if (property != null)
                return property.PropertyType;

            return ((FieldInfo)member).FieldType;
        }

        private static void AssertPreviewTextureMemberExists(Type deviceViewType)
        {
            Assert.IsNotNull(
                GetMember(deviceViewType, "PreviewTexture"),
                "DeviceView.PreviewTexture could not be resolved.");
        }
    }
}
