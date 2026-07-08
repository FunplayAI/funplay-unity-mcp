// Copyright (C) Funplay. Licensed under MIT.
using System.Collections.Generic;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using Funplay.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Funplay.Editor.Tools.Builtins
{
    [ToolProvider("Mesh")]
    internal static class MeshFunctions
    {
        [Description("Inspect a Mesh. Resolves the mesh from EITHER an asset path (e.g. 'Assets/Models/foo.fbx' — " +
                     "for a model/fbx every embedded Mesh sub-asset is reported; a bare .mesh/.asset reports that one) " +
                     "OR a scene GameObject identifier (instance id, name, or path — reads MeshFilter.sharedMesh, " +
                     "falling back to SkinnedMeshRenderer.sharedMesh). Reports per mesh: name, vertexCount, triangleCount, " +
                     "subMeshCount, bounds, populated UV channels, hasNormals/Tangents/Colors, isReadable, blendShapeCount.")]
        [ReadOnlyTool]
        public static object GetMeshInfo(
            [ToolParam("Asset path to a mesh/model, or a scene GameObject identifier (instance id, name, or path)")] string target)
        {
            if (string.IsNullOrEmpty(target))
                return Response.Error("MESH_NOT_FOUND",
                    new { target, hint = "Provide an asset path (e.g. 'Assets/Models/foo.fbx') or a scene GameObject identifier." });

            var meshes = new List<Mesh>();
            string resolvedVia = null;

            // 1) Try to resolve as an asset first. LoadAllAssetsAtPath surfaces every embedded Mesh
            //    sub-asset of a model/fbx; for a bare .mesh/.asset it returns that single mesh.
            //    On a non-asset string it simply returns an empty array, so this is safe to always try.
            var atPath = AssetDatabase.LoadAllAssetsAtPath(target);
            if (atPath != null)
            {
                foreach (var obj in atPath)
                {
                    if (obj is Mesh m && m != null)
                        meshes.Add(m);
                }
            }
            if (meshes.Count == 0)
            {
                var single = AssetDatabase.LoadAssetAtPath<Mesh>(target);
                if (single != null)
                    meshes.Add(single);
            }
            if (meshes.Count > 0)
                resolvedVia = "asset";

            // 2) Otherwise resolve as a scene GameObject: MeshFilter.sharedMesh, else SkinnedMeshRenderer.sharedMesh.
            if (meshes.Count == 0)
            {
                var go = ObjectsHelper.FindTarget(target);
                if (go != null)
                {
                    var mf = go.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null)
                    {
                        meshes.Add(mf.sharedMesh);
                        resolvedVia = "gameObject:MeshFilter";
                    }
                    else
                    {
                        var smr = go.GetComponent<SkinnedMeshRenderer>();
                        if (smr != null && smr.sharedMesh != null)
                        {
                            meshes.Add(smr.sharedMesh);
                            resolvedVia = "gameObject:SkinnedMeshRenderer";
                        }
                    }
                }
            }

            if (meshes.Count == 0)
                return Response.Error("MESH_NOT_FOUND", new
                {
                    target,
                    hint = "No mesh at that asset path (or its sub-assets), and no scene GameObject with a " +
                           "MeshFilter.sharedMesh / SkinnedMeshRenderer.sharedMesh matched. Check the path/name."
                });

            var described = new List<object>(meshes.Count);
            foreach (var mesh in meshes)
                described.Add(Describe(mesh));

            return Response.Success(
                $"Resolved {meshes.Count} mesh(es) from '{target}' (via {resolvedVia}).",
                new { target, resolvedVia, meshCount = meshes.Count, meshes = described });
        }

        // -------- Helpers --------

        private static object Describe(Mesh mesh)
        {
            // triangleCount reads mesh.triangles, which throws when the mesh is not readable
            // (Read/Write disabled on the importer). Guard it and report null in that case.
            int? triangleCount = null;
            string triangleNote = null;
            if (mesh.isReadable)
                triangleCount = mesh.triangles.Length / 3;
            else
                triangleNote = "triangleCount unavailable: mesh is not readable (Read/Write disabled on the importer).";

            // HasVertexAttribute reads the vertex layout and works even when the mesh is not readable,
            // so it is preferred over inspecting uv/uv2/uv3/uv4 array lengths (which require readable).
            var uvChannels = new Dictionary<string, bool>
            {
                { "uv0", mesh.HasVertexAttribute(VertexAttribute.TexCoord0) },
                { "uv1", mesh.HasVertexAttribute(VertexAttribute.TexCoord1) },
                { "uv2", mesh.HasVertexAttribute(VertexAttribute.TexCoord2) },
                { "uv3", mesh.HasVertexAttribute(VertexAttribute.TexCoord3) },
            };
            var populatedUVChannels = new List<string>();
            foreach (var kv in uvChannels)
            {
                if (kv.Value)
                    populatedUVChannels.Add(kv.Key);
            }

            var b = mesh.bounds;

            return new
            {
                name = mesh.name,
                instanceId = mesh.GetInstanceID(),
                vertexCount = mesh.vertexCount,
                triangleCount,
                triangleNote,
                subMeshCount = mesh.subMeshCount,
                bounds = new
                {
                    center = new { x = b.center.x, y = b.center.y, z = b.center.z },
                    size = new { x = b.size.x, y = b.size.y, z = b.size.z }
                },
                uvChannels,
                populatedUVChannels,
                hasNormals = mesh.HasVertexAttribute(VertexAttribute.Normal),
                hasTangents = mesh.HasVertexAttribute(VertexAttribute.Tangent),
                hasColors = mesh.HasVertexAttribute(VertexAttribute.Color),
                isReadable = mesh.isReadable,
                blendShapeCount = mesh.blendShapeCount
            };
        }
    }
}
