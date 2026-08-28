using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using static UnityParticleQuarksExporter.Editor.QuarksCoordinateUtility;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class ScalingContext
    {
        public Vector3 emitterScale = Vector3.one;
        public Vector3 shapeScale = Vector3.one;
        public Vector3 particleAxisSigns = Vector3.one;
        public Vector3 shapeAxisSigns = Vector3.one;
        public Vector3 rendererAxisSigns = Vector3.one;
        public Vector3 worldModuleScale = Vector3.one;
        public Matrix4x4 localModuleToUnityWorld = Matrix4x4.identity;
    }

    internal static class QuarksCoordinateUtility
    {
        internal static readonly Matrix4x4 UnityWorldToThreeWorld =
            Matrix4x4.Scale(new Vector3(1, 1, -1));

        internal static readonly Matrix4x4 UnityLocalToQuarksLocal =
            Matrix4x4.Scale(new Vector3(-1, 1, 1));

        internal static float Sign(float value)
        {
            return value < 0 ? -1 : 1;
        }

        internal static Vector3 SignedScale(Vector3 magnitudes, Vector3 signs)
        {
            return Vector3.Scale(magnitudes, signs);
        }

        internal static Matrix4x4 BuildVectorModuleBasis(
            ParticleSystem system,
            ScalingContext scaling,
            Matrix4x4 particleToThreeWorld,
            ParticleSystemSimulationSpace moduleSpace)
        {
            var moduleToUnityWorld = moduleSpace == ParticleSystemSimulationSpace.Local
                ? scaling.localModuleToUnityWorld
                : Matrix4x4.Scale(scaling.worldModuleScale);
            var moduleToThreeWorld = UnityWorldToThreeWorld * moduleToUnityWorld;
            return system.main.simulationSpace == ParticleSystemSimulationSpace.World
                ? moduleToThreeWorld
                : particleToThreeWorld.inverse * moduleToThreeWorld;
        }
    }

    internal sealed class QuarksGeometryRendererConverter
    {
        private readonly GameObject root;
        private readonly string sourcePath;
        private readonly Dictionary<string, JsonObject> geometries;
        private readonly Matrix4x4 unityRootPoseInverse;

        internal QuarksGeometryRendererConverter(
            GameObject prefabRoot,
            string prefabPath,
            Dictionary<string, JsonObject> geometryArtifacts)
        {
            root = prefabRoot != null ? prefabRoot : throw new ArgumentNullException(nameof(prefabRoot));
            sourcePath = prefabPath;
            geometries = geometryArtifacts ?? throw new ArgumentNullException(nameof(geometryArtifacts));
            unityRootPoseInverse = Matrix4x4.TRS(
                root.transform.position,
                root.transform.rotation,
                Vector3.one).inverse;
        }

        internal int ResolveRenderMode(ParticleSystem system, ParticleSystemRenderer renderer, ConversionDiagnostics diagnostics)
        {
            if (system.trails.enabled)
            {
                diagnostics.approximated.Add("trails");
                if (renderer != null && renderer.renderMode != ParticleSystemRenderMode.None)
                {
                    diagnostics.mapped.Add("trails.particleHeadRenderer.companionRuntime");
                    diagnostics.approximated.Add("trails.particleHeadRenderer.stockTrailOnlyFallback");
                    diagnostics.warnings.Add("The paired SDK renders the Unity particle head through a companion batch that reads the authoritative Trail ParticleSystem; stock Quarks playback remains trail-only.");
                }
                return 3;
            }
            if (renderer == null)
            {
                diagnostics.unsupported.Add("renderer.missing");
                diagnostics.approximated.Add("renderer.missing.transparentBillboardFallback");
                diagnostics.warnings.Add("The ParticleSystem has no renderer component. Best-effort retains only a transparent billboard control emitter when trigger semantics require it; otherwise the system is omitted.");
                return 0;
            }
            if (!renderer.enabled || renderer.renderMode == ParticleSystemRenderMode.None)
            {
                diagnostics.inactive.Add("renderer.invisible");
                return 0;
            }
            switch (renderer.renderMode)
            {
                case ParticleSystemRenderMode.Billboard: diagnostics.mapped.Add("renderer.billboard"); return 0;
                case ParticleSystemRenderMode.Stretch: return 1;
                case ParticleSystemRenderMode.Mesh: return 2;
                case ParticleSystemRenderMode.HorizontalBillboard: diagnostics.mapped.Add("renderer.horizontalBillboard"); return 4;
                case ParticleSystemRenderMode.VerticalBillboard: diagnostics.mapped.Add("renderer.verticalBillboard"); return 5;
                default:
                    diagnostics.unsupported.Add("renderer." + renderer.renderMode);
                    diagnostics.approximated.Add("renderer.unsupported.billboardFallback");
                    diagnostics.warnings.Add("The active ParticleSystem renderer mode has no mapped Quarks render mode. Best-effort explicitly uses a billboard; strict export fails.");
                    return 0;
            }
        }

        internal string ResolveRendererAlignment(ParticleSystemRenderer renderer)
        {
            if (renderer == null) return "billboard";
            switch (renderer.alignment)
            {
                case ParticleSystemRenderSpace.World: return "world";
                case ParticleSystemRenderSpace.Local: return "local";
                case ParticleSystemRenderSpace.Facing: return "facing";
                case ParticleSystemRenderSpace.Velocity: return "velocity";
                case ParticleSystemRenderSpace.View:
                default: return "view";
            }
        }

        internal JsonObject BuildRendererAlignmentMetadata(
            ParticleSystem system,
            ParticleSystemRenderer renderer,
            int renderMode,
            ConversionDiagnostics diagnostics)
        {
            if (system == null || renderer == null || renderMode == 3 ||
                !renderer.enabled || renderer.renderMode == ParticleSystemRenderMode.None)
                return null;
            var alignment = ResolveRendererAlignment(renderer);
            if (alignment == "view" || alignment == "facing")
                diagnostics.mapped.Add("renderer.alignment." + alignment);
            else
                diagnostics.mapped.Add("renderer.alignment." + alignment + ".runtime");
            if (alignment == "local" || alignment == "world")
            {
                diagnostics.requiresPairedRuntime = true;
                diagnostics.warnings.Add("Unity Billboard " + alignment + " alignment is preserved through paired renderer metadata; stock Quarks billboard remains camera-facing.");
            }
            else if (alignment == "velocity")
            {
                diagnostics.approximated.Add("renderer.alignment.velocity.stockBillboardFallback");
                diagnostics.warnings.Add("Unity Billboard Velocity alignment is recorded for the paired renderer contract; current stock Quarks playback keeps its camera-facing billboard fallback.");
            }
            return Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.renderer_alignment.v1"))
                .Add("mode", Json.String(alignment))
                .Add("preserveAuthored", Json.Boolean(true))
                .Add("simulationSpace", Json.String(system.main.simulationSpace == ParticleSystemSimulationSpace.World ? "world" : "local"));
        }

        internal JsonObject BuildRendererPivotMetadata(
            ParticleSystemRenderer renderer,
            ConversionDiagnostics diagnostics)
        {
            if (renderer == null || renderer.pivot.sqrMagnitude <= 0.000000000001f)
            {
                diagnostics.inactive.Add("renderer.pivot");
                return null;
            }

            var pivot = renderer.pivot;
            var geometryOffset = new Vector3(pivot.x, pivot.y, -pivot.z);
            if (renderer.renderMode == ParticleSystemRenderMode.Mesh && renderer.mesh != null)
            {
                geometryOffset = Vector3.Scale(geometryOffset, renderer.mesh.bounds.size);
            }
            diagnostics.mapped.Add("renderer.pivot.runtime");
            diagnostics.approximated.Add("renderer.pivot.stockCenteredFallback");
            diagnostics.warnings.Add("ParticleSystemRenderer pivot is preserved with a handedness-corrected geometry offset and applied before authored particle rotation by the paired SDK batch shader. Stock Quarks keeps centered geometry.");
            return Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.renderer_pivot.v1"))
                .Add("sourceRenderMode", Json.String(renderer.renderMode.ToString()))
                .Add("value", Json.Array()
                    .Add(Json.Number(pivot.x))
                    .Add(Json.Number(pivot.y))
                    .Add(Json.Number(pivot.z)))
                .Add("geometryOffset", Json.Array()
                    .Add(Json.Number(geometryOffset.x))
                    .Add(Json.Number(geometryOffset.y))
                    .Add(Json.Number(geometryOffset.z)));
        }


        internal ScalingContext BuildScalingContext(
            ParticleSystem system,
            ConversionDiagnostics diagnostics)
        {
            var context = new ScalingContext();
            var main = system.main;
            switch (main.scalingMode)
            {
                case ParticleSystemScalingMode.Hierarchy:
                    context.emitterScale = ScaleMagnitudes(
                        system.transform.lossyScale,
                        "main.scalingMode.hierarchy",
                        diagnostics,
                        out context.particleAxisSigns);
                    context.shapeAxisSigns = context.particleAxisSigns;
                    context.rendererAxisSigns = context.particleAxisSigns;
                    context.worldModuleScale = SignedScale(context.emitterScale, context.particleAxisSigns);
                    context.localModuleToUnityWorld = unityRootPoseInverse * Matrix4x4.TRS(
                        Vector3.zero,
                        system.transform.rotation,
                        context.worldModuleScale);
                    diagnostics.mapped.Add("main.scalingMode.hierarchy");
                    if (HasHierarchyShear(system.transform))
                    {
                        diagnostics.unsupported.Add("main.scalingMode.hierarchy.shear");
                        diagnostics.approximated.Add("main.scalingMode.hierarchy.orthogonalizedTrsFallback");
                        diagnostics.warnings.Add("The parent hierarchy produces shear. Unity ParticleSystem transform decomposition is not representable by a Three Object3D TRS; best-effort uses Unity world rotation plus signed lossyScale and reports the orthogonalized TRS fallback.");
                    }
                    break;
                case ParticleSystemScalingMode.Local:
                    context.emitterScale = ScaleMagnitudes(
                        system.transform.localScale,
                        "main.scalingMode.local",
                        diagnostics,
                        out context.particleAxisSigns);
                    context.shapeAxisSigns = context.particleAxisSigns;
                    context.rendererAxisSigns = context.particleAxisSigns;
                    context.worldModuleScale = SignedScale(context.emitterScale, context.particleAxisSigns);
                    context.localModuleToUnityWorld = unityRootPoseInverse * Matrix4x4.TRS(
                        Vector3.zero,
                        system.transform.rotation,
                        context.worldModuleScale);
                    diagnostics.mapped.Add("main.scalingMode.local");
                    break;
                case ParticleSystemScalingMode.Shape:
                    context.shapeScale = ScaleMagnitudes(
                        system.transform.lossyScale,
                        "main.scalingMode.shape",
                        diagnostics,
                        out context.shapeAxisSigns);
                    context.localModuleToUnityWorld = unityRootPoseInverse * Matrix4x4.TRS(
                        Vector3.zero,
                        system.transform.rotation,
                        Vector3.one);
                    diagnostics.mapped.Add("main.scalingMode.shape");
                    if (HasHierarchyShear(system.transform))
                    {
                        diagnostics.unsupported.Add("main.scalingMode.shape.shear");
                        diagnostics.approximated.Add("main.scalingMode.shape.shear.orthogonalizedTrsFallback");
                        diagnostics.warnings.Add("The parent hierarchy produces shear in Shape scaling mode. Unity's sheared birth-position basis has not been black-box matched; best-effort uses world rotation plus signed lossyScale and strict export fails.");
                    }
                    break;
                default:
                    diagnostics.unsupported.Add("main.scalingMode." + main.scalingMode);
                    diagnostics.approximated.Add("main.scalingMode.unknown.unitFallback");
                    diagnostics.warnings.Add("Unknown ParticleSystem scaling mode; best-effort explicitly uses a unit emitter and unit shape scale.");
                    break;
            }
            return context;
        }

        internal Matrix4x4 BuildEmitterMatrix(ParticleSystem system, Vector3 emitterScale)
        {
            var unityWorld = Matrix4x4.TRS(
                system.transform.position,
                system.transform.rotation,
                emitterScale);
            return UnityWorldToThreeWorld * unityRootPoseInverse * unityWorld * UnityLocalToQuarksLocal;
        }


        internal int ResolveParticleHeadRenderMode(
            ParticleSystem system,
            ParticleSystemRenderer renderer,
            int renderMode,
            ConversionDiagnostics diagnostics)
        {
            if (renderMode != 3 || !system.trails.enabled || renderer == null ||
                !renderer.enabled || renderer.renderMode == ParticleSystemRenderMode.None)
            {
                return -1;
            }
            switch (renderer.renderMode)
            {
                case ParticleSystemRenderMode.Billboard:
                    diagnostics.mapped.Add("trails.particleHeadRenderer.billboard.companionRuntime");
                    return 0;
                case ParticleSystemRenderMode.Mesh:
                    diagnostics.mapped.Add("trails.particleHeadRenderer.mesh.companionRuntime");
                    return 2;
                case ParticleSystemRenderMode.HorizontalBillboard:
                    diagnostics.mapped.Add("trails.particleHeadRenderer.horizontalBillboard.companionRuntime");
                    return 4;
                case ParticleSystemRenderMode.VerticalBillboard:
                    diagnostics.mapped.Add("trails.particleHeadRenderer.verticalBillboard.companionRuntime");
                    return 5;
                case ParticleSystemRenderMode.Stretch:
                    diagnostics.mapped.Add("trails.particleHeadRenderer.stretchedBillboard.companionRuntime");
                    return 1;
                default:
                    diagnostics.unsupported.Add("trails.particleHeadRenderer." + renderer.renderMode);
                    diagnostics.approximated.Add("trails.particleHeadRenderer.billboardFallback");
                    diagnostics.warnings.Add("The Unity particle head renderer mode has no companion mapping; best-effort uses a billboard head.");
                    return 0;
            }
        }


        internal bool ActiveRendererHasMissingMaterial(
            ParticleSystem system,
            ParticleSystemRenderer renderer)
        {
            if (renderer == null || !renderer.enabled || renderer.renderMode == ParticleSystemRenderMode.None)
            {
                return false;
            }
            return system.trails.enabled ? renderer.trailMaterial == null : renderer.sharedMaterial == null;
        }

        private static Vector3 ScaleMagnitudes(
            Vector3 scale,
            string field,
            ConversionDiagnostics diagnostics,
            out Vector3 signs)
        {
            signs = new Vector3(
                QuarksCoordinateUtility.Sign(scale.x),
                QuarksCoordinateUtility.Sign(scale.y),
                QuarksCoordinateUtility.Sign(scale.z));
            if (scale.x < 0 || scale.y < 0 || scale.z < 0)
            {
                diagnostics.mapped.Add(field + ".negativeAxisRuntime");
                diagnostics.approximated.Add(field + ".negativeAxis.stockMagnitudeFallback");
                diagnostics.warnings.Add(field + " contains a negative axis. The paired SDK applies the signed birth basis and reflected Mesh geometry while the stock JSON keeps an explicit magnitude-only fallback; the Quarks emitter matrix remains positive-determinant.");
            }
            var magnitudes = new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            if (magnitudes.x <= 0.000001f || magnitudes.y <= 0.000001f || magnitudes.z <= 0.000001f)
            {
                diagnostics.unsupported.Add(field + ".zeroAxis");
                diagnostics.approximated.Add(field + ".zeroAxis.epsilonFallback");
                diagnostics.warnings.Add(field + " contains a zero axis, which would make the Quarks emitter matrix singular. Best-effort explicitly clamps that magnitude to 0.000001.");
                magnitudes.x = Mathf.Max(magnitudes.x, 0.000001f);
                magnitudes.y = Mathf.Max(magnitudes.y, 0.000001f);
                magnitudes.z = Mathf.Max(magnitudes.z, 0.000001f);
            }
            return magnitudes;
        }

        private static bool HasHierarchyShear(Transform transform)
        {
            var matrix = transform.localToWorldMatrix;
            var x = matrix.GetColumn(0);
            var y = matrix.GetColumn(1);
            var z = matrix.GetColumn(2);
            var xVector = new Vector3(x.x, x.y, x.z);
            var yVector = new Vector3(y.x, y.y, y.z);
            var zVector = new Vector3(z.x, z.y, z.z);
            if (xVector.sqrMagnitude <= 0.000000000001f ||
                yVector.sqrMagnitude <= 0.000000000001f ||
                zVector.sqrMagnitude <= 0.000000000001f) return false;
            xVector.Normalize();
            yVector.Normalize();
            zVector.Normalize();
            return Mathf.Abs(Vector3.Dot(xVector, yVector)) > 0.0001f ||
                   Mathf.Abs(Vector3.Dot(xVector, zVector)) > 0.0001f ||
                   Mathf.Abs(Vector3.Dot(yVector, zVector)) > 0.0001f;
        }

        internal string RegisterBillboardGeometry()
        {
            var id = UnityParticleQuarksStableId.Create(sourcePath, root.name, "geometry:billboard-plane");
            if (geometries.ContainsKey(id)) return id;
            geometries[id] = BufferGeometry(id,
                new[] { -0.5f, -0.5f, 0, 0.5f, -0.5f, 0, 0.5f, 0.5f, 0, -0.5f, 0.5f, 0 },
                new[] { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f },
                new[] { 0, 1, 2, 0, 2, 3 },
                new[] { 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f });
            return id;
        }

        internal string RegisterGeometry(Mesh mesh, string slot, ConversionDiagnostics diagnostics)
        {
            return RegisterGeometry(mesh, Vector3.one, slot, diagnostics, false);
        }

        internal string RegisterGeometry(
            Mesh mesh,
            Vector3 shapeScale,
            string slot,
            ConversionDiagnostics diagnostics)
        {
            return RegisterGeometry(mesh, shapeScale, slot, diagnostics, true);
        }

        internal string RegisterGeometry(
            Mesh mesh,
            Vector3 shapeScale,
            string slot,
            ConversionDiagnostics diagnostics,
            bool billboardFallback)
        {
            var assetPath = AssetDatabase.GetAssetPath(mesh);
            var id = UnityParticleQuarksStableId.Create(sourcePath, slot + ":" + assetPath, "geometry");
            if (geometries.ContainsKey(id)) return id;
            try
            {
                var vertices = mesh.vertices;
                var uv = mesh.uv;
                var triangles = mesh.triangles;
                var sourceNormals = mesh.normals;
                if (sourceNormals.Length != vertices.Length)
                {
                    sourceNormals = DeriveVertexNormals(vertices, triangles);
                    diagnostics.approximated.Add("mesh.normals.derived");
                    diagnostics.warnings.Add("Mesh geometry derived vertex normals because the source mesh has no complete normal stream.");
                }
                else
                {
                    diagnostics.mapped.Add("mesh.normals");
                }
                var positions = new float[vertices.Length * 3];
                var normals = new float[vertices.Length * 3];
                for (var index = 0; index < vertices.Length; index++)
                {
                    WritePosition(
                        positions,
                        index * 3,
                        ConvertPosition(Vector3.Scale(vertices[index], shapeScale)));
                    var transformedNormal = ConvertDirection(new Vector3(
                        Mathf.Abs(shapeScale.x) <= 0.000001f ? 0 : sourceNormals[index].x / shapeScale.x,
                        Mathf.Abs(shapeScale.y) <= 0.000001f ? 0 : sourceNormals[index].y / shapeScale.y,
                        Mathf.Abs(shapeScale.z) <= 0.000001f ? 0 : sourceNormals[index].z / shapeScale.z));
                    if (transformedNormal.sqrMagnitude <= 0.000000000001f) transformedNormal = Vector3.up;
                    else transformedNormal.Normalize();
                    WritePosition(normals, index * 3, transformedNormal);
                }
                var uvs = new float[vertices.Length * 2];
                for (var index = 0; index < Math.Min(uv.Length, vertices.Length); index++)
                {
                    uvs[index * 2] = uv[index].x;
                    uvs[index * 2 + 1] = uv[index].y;
                }
                // Unity treats clockwise triangles as front-facing, while Three uses
                // counter-clockwise triangles. The local X handedness reflection
                // already converts that convention unless an odd signed scale
                // cancels the reflection.
                var reversesWinding = shapeScale.x * shapeScale.y * shapeScale.z < 0;
                for (var index = 0; reversesWinding && index + 2 < triangles.Length; index += 3)
                {
                    var swap = triangles[index + 1];
                    triangles[index + 1] = triangles[index + 2];
                    triangles[index + 2] = swap;
                }
                geometries[id] = BufferGeometry(id, positions, uvs, triangles, normals);
                return id;
            }
            catch (Exception exception)
            {
                diagnostics.unsupported.Add("mesh.readableGeometry");
                diagnostics.approximated.Add(billboardFallback
                    ? "mesh.readableGeometry.billboardFallback"
                    : "mesh.readableGeometry.omittedFallback");
                diagnostics.warnings.Add("Mesh geometry could not be read. Best-effort " +
                                         (billboardFallback ? "uses billboard geometry" : "omits the visible Mesh emitter") +
                                         "; strict export fails: " + exception.Message);
                return billboardFallback ? RegisterBillboardGeometry() : null;
            }
        }

        internal string RegisterMeshVertexSamplingGeometry(
            Mesh mesh,
            Vector3 shapeScale,
            string slot,
            ConversionDiagnostics diagnostics)
        {
            var assetPath = AssetDatabase.GetAssetPath(mesh);
            var id = UnityParticleQuarksStableId.Create(sourcePath, slot + ":" + assetPath, "geometry");
            if (geometries.ContainsKey(id)) return id;
            try
            {
                var vertices = mesh.vertices;
                if (vertices.Length == 0) throw new InvalidOperationException("Mesh has no vertices.");
                var normals = mesh.normals;
                var hasSourceNormals = normals.Length == vertices.Length;
                if (!hasSourceNormals)
                {
                    normals = DeriveVertexNormals(vertices, mesh.triangles);
                    diagnostics.warnings.Add("Mesh Shape Vertex sampling geometry derived normals because the source mesh has no complete normal stream.");
                }

                var proxyRadius = Mathf.Max(mesh.bounds.size.magnitude * 0.00001f, 0.000001f);
                var positions = new float[vertices.Length * 9];
                var uvs = new float[vertices.Length * 6];
                var indices = new int[vertices.Length * 3];
                const float half = 0.5f;
                const float halfSqrtThree = 0.8660254037844386f;
                for (var index = 0; index < vertices.Length; index++)
                {
                    var center = ConvertPosition(Vector3.Scale(vertices[index], shapeScale));
                    var scaledNormal = new Vector3(
                        shapeScale.x <= 0.000001f ? 0 : normals[index].x / shapeScale.x,
                        shapeScale.y <= 0.000001f ? 0 : normals[index].y / shapeScale.y,
                        shapeScale.z <= 0.000001f ? 0 : normals[index].z / shapeScale.z);
                    var normal = ConvertDirection(scaledNormal);
                    if (normal.sqrMagnitude <= 0.000000000001f) normal = Vector3.up;
                    normal.Normalize();
                    var reference = Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right;
                    var tangent = Vector3.Cross(reference, normal).normalized;
                    var bitangent = Vector3.Cross(normal, tangent).normalized;
                    var first = center + tangent * proxyRadius;
                    var second = center + (-tangent * half + bitangent * halfSqrtThree) * proxyRadius;
                    var third = center + (-tangent * half - bitangent * halfSqrtThree) * proxyRadius;
                    WritePosition(positions, index * 9, first);
                    WritePosition(positions, index * 9 + 3, second);
                    WritePosition(positions, index * 9 + 6, third);
                    indices[index * 3] = index * 3;
                    indices[index * 3 + 1] = index * 3 + 1;
                    indices[index * 3 + 2] = index * 3 + 2;
                }

                geometries[id] = BufferGeometry(id, positions, uvs, indices);
                return id;
            }
            catch (Exception exception)
            {
                diagnostics.unsupported.Add("mesh.readableGeometry");
                diagnostics.approximated.Add("mesh.vertexSampling.billboardSurfaceFallback");
                diagnostics.warnings.Add("Mesh vertex-sampling geometry could not be read. Best-effort uses the billboard plane as a mesh-surface emitter; strict export fails: " + exception.Message);
                return RegisterBillboardGeometry();
            }
        }

        internal string RegisterCircleSamplingGeometry(
            ParticleSystem.ShapeModule shape,
            Vector3 faceNormal,
            Vector3 shapeScale,
            string slot,
            ConversionDiagnostics diagnostics)
        {
            var id = UnityParticleQuarksStableId.Create(sourcePath, slot, "geometry");
            if (geometries.ContainsKey(id)) return id;

            const int segments = 64;
            var outerRadius = Mathf.Max(shape.radius, 0.000001f);
            var thickness = shape.shapeType == ParticleSystemShapeType.CircleEdge
                ? 0
                : Mathf.Clamp01(shape.radiusThickness);
            var innerRadius = outerRadius * (1 - thickness);
            if (thickness <= 0.000001f)
            {
                innerRadius = Mathf.Max(0, outerRadius - Mathf.Max(outerRadius * 0.0001f, 0.000001f));
                diagnostics.approximated.Add("shape.circleEdgeThickness");
                diagnostics.warnings.Add("A zero-thickness Unity Circle edge is approximated by a microscopic annulus because stock Quarks mesh_surface requires nonzero triangle area.");
            }

            var rotation = Quaternion.Euler(shape.rotation);
            var positions = new List<Vector3>();
            var indices = new List<int>();
            var inverseScaledFaceNormal = new Vector3(
                shapeScale.x <= 0.000001f ? 0 : faceNormal.x / shapeScale.x,
                shapeScale.y <= 0.000001f ? 0 : faceNormal.y / shapeScale.y,
                shapeScale.z <= 0.000001f ? 0 : faceNormal.z / shapeScale.z);
            var convertedFaceNormal = ConvertDirection(inverseScaledFaceNormal).normalized;
            if (innerRadius <= 0.000001f)
            {
                positions.Add(ConvertPosition(Vector3.Scale(rotation * Vector3.zero, shapeScale)));
                for (var index = 0; index < segments; index++)
                {
                    var angle = Mathf.PI * 2 * index / segments;
                    positions.Add(ConvertPosition(Vector3.Scale(rotation * new Vector3(
                        Mathf.Cos(angle) * outerRadius,
                        Mathf.Sin(angle) * outerRadius,
                        0), shapeScale)));
                }
                for (var index = 0; index < segments; index++)
                {
                    AddOrientedTriangle(indices, positions, convertedFaceNormal, 0, index + 1, (index + 1) % segments + 1);
                }
            }
            else
            {
                for (var index = 0; index < segments; index++)
                {
                    var angle = Mathf.PI * 2 * index / segments;
                    var direction = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0);
                    positions.Add(ConvertPosition(Vector3.Scale(rotation * (direction * innerRadius), shapeScale)));
                    positions.Add(ConvertPosition(Vector3.Scale(rotation * (direction * outerRadius), shapeScale)));
                }
                for (var index = 0; index < segments; index++)
                {
                    var next = (index + 1) % segments;
                    var inner = index * 2;
                    var outer = inner + 1;
                    var nextInner = next * 2;
                    var nextOuter = nextInner + 1;
                    AddOrientedTriangle(indices, positions, convertedFaceNormal, inner, outer, nextOuter);
                    AddOrientedTriangle(indices, positions, convertedFaceNormal, inner, nextOuter, nextInner);
                }
            }

            var flattened = new float[positions.Count * 3];
            for (var index = 0; index < positions.Count; index++)
            {
                WritePosition(flattened, index * 3, positions[index]);
            }
            geometries[id] = BufferGeometry(id, flattened, new float[positions.Count * 2], indices.ToArray());
            return id;
        }

        internal string RegisterBoxSamplingGeometry(
            ParticleSystem.ShapeModule shape,
            Vector3 shapeScale,
            string slot,
            ConversionDiagnostics diagnostics)
        {
            var id = UnityParticleQuarksStableId.Create(sourcePath, slot, "geometry");
            if (geometries.ContainsKey(id)) return id;

            const int resolution = 10;
            var size = new Vector3(
                Mathf.Max(Mathf.Abs(shape.scale.x * shapeScale.x), 0.000001f),
                Mathf.Max(Mathf.Abs(shape.scale.y * shapeScale.y), 0.000001f),
                Mathf.Max(Mathf.Abs(shape.scale.z * shapeScale.z), 0.000001f));
            var half = size * 0.5f;
            var centers = new List<Vector3>();
            for (var z = 0; z < resolution; z++)
            {
                for (var y = 0; y < resolution; y++)
                {
                    for (var x = 0; x < resolution; x++)
                    {
                        var boundaryAxes = (x == 0 || x == resolution - 1 ? 1 : 0) +
                                           (y == 0 || y == resolution - 1 ? 1 : 0) +
                                           (z == 0 || z == resolution - 1 ? 1 : 0);
                        if (shape.shapeType == ParticleSystemShapeType.BoxShell && boundaryAxes == 0) continue;
                        if (shape.shapeType == ParticleSystemShapeType.BoxEdge && boundaryAxes < 2) continue;
                        centers.Add(new Vector3(
                            Mathf.Lerp(-half.x, half.x, (x + 0.5f) / resolution),
                            Mathf.Lerp(-half.y, half.y, (y + 0.5f) / resolution),
                            Mathf.Lerp(-half.z, half.z, (z + 0.5f) / resolution)));
                    }
                }
            }

            if (centers.Count == 0)
            {
                diagnostics.unsupported.Add("shape.boxDiscreteGrid.empty");
                diagnostics.approximated.Add("shape.boxDiscreteGrid.empty.billboardSurfaceFallback");
                diagnostics.warnings.Add("The deterministic Box sampling grid produced no points. Best-effort explicitly uses the billboard plane as a mesh-surface emitter; strict export fails.");
                return RegisterBillboardGeometry();
            }

            var proxyRadius = Mathf.Max(size.magnitude * 0.00001f, 0.000001f);
            var normal = ConvertDirection(Vector3.forward).normalized;
            var reference = Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right;
            var tangent = Vector3.Cross(reference, normal).normalized;
            var bitangent = Vector3.Cross(normal, tangent).normalized;
            const float halfTriangle = 0.5f;
            const float halfSqrtThree = 0.8660254037844386f;
            var positions = new float[centers.Count * 9];
            var uvs = new float[centers.Count * 6];
            var indices = new int[centers.Count * 3];
            for (var index = 0; index < centers.Count; index++)
            {
                var center = ConvertPosition(centers[index]);
                WritePosition(positions, index * 9, center + tangent * proxyRadius);
                WritePosition(positions, index * 9 + 3,
                    center + (-tangent * halfTriangle + bitangent * halfSqrtThree) * proxyRadius);
                WritePosition(positions, index * 9 + 6,
                    center + (-tangent * halfTriangle - bitangent * halfSqrtThree) * proxyRadius);
                indices[index * 3] = index * 3;
                indices[index * 3 + 1] = index * 3 + 1;
                indices[index * 3 + 2] = index * 3 + 2;
            }

            geometries[id] = BufferGeometry(id, positions, uvs, indices);
            return id;
        }

        private static void AddOrientedTriangle(
            List<int> indices,
            IReadOnlyList<Vector3> positions,
            Vector3 desiredNormal,
            int first,
            int second,
            int third)
        {
            var normal = Vector3.Cross(positions[second] - positions[first], positions[third] - positions[first]);
            if (Vector3.Dot(normal, desiredNormal) < 0)
            {
                var swap = second;
                second = third;
                third = swap;
            }
            indices.Add(first);
            indices.Add(second);
            indices.Add(third);
        }

        private static Vector3[] DeriveVertexNormals(Vector3[] vertices, int[] triangles)
        {
            var normals = new Vector3[vertices.Length];
            for (var index = 0; index + 2 < triangles.Length; index += 3)
            {
                var first = triangles[index];
                var second = triangles[index + 1];
                var third = triangles[index + 2];
                if (first < 0 || second < 0 || third < 0 ||
                    first >= vertices.Length || second >= vertices.Length || third >= vertices.Length) continue;
                var faceNormal = Vector3.Cross(vertices[second] - vertices[first], vertices[third] - vertices[first]);
                normals[first] += faceNormal;
                normals[second] += faceNormal;
                normals[third] += faceNormal;
            }
            for (var index = 0; index < normals.Length; index++)
            {
                if (normals[index].sqrMagnitude <= 0.000000000001f) normals[index] = Vector3.up;
                else normals[index].Normalize();
            }
            return normals;
        }

        private static Vector3 ConvertPosition(Vector3 value)
        {
            return new Vector3(-value.x, value.y, value.z);
        }

        private static Vector3 ConvertDirection(Vector3 value)
        {
            return new Vector3(-value.x, value.y, value.z);
        }

        private static void WritePosition(float[] positions, int offset, Vector3 value)
        {
            positions[offset] = value.x;
            positions[offset + 1] = value.y;
            positions[offset + 2] = value.z;
        }

        private static JsonObject BufferGeometry(
            string id,
            float[] positions,
            float[] uvs,
            int[] indices,
            float[] normals = null)
        {
            var attributes = Json.Object()
                .Add("position", BufferAttribute(3, "Float32Array", positions))
                .Add("uv", BufferAttribute(2, "Float32Array", uvs));
            if (normals != null && normals.Length == positions.Length)
                attributes.Add("normal", BufferAttribute(3, "Float32Array", normals));
            return Json.Object()
                .Add("uuid", Json.String(id))
                .Add("type", Json.String("BufferGeometry"))
                .Add("data", Json.Object()
                    .Add("attributes", attributes)
                    .Add("index", IndexAttribute(indices)));
        }

        internal JsonObject BuildMeshVelocityAlignmentMetadata(
            ParticleSystem system,
            ParticleSystemRenderer renderer,
            int renderMode,
            ConversionDiagnostics diagnostics)
        {
            if (renderMode != 2 || renderer == null ||
                renderer.alignment != ParticleSystemRenderSpace.Velocity)
            {
                return null;
            }

            if (system.main.simulationSpace != ParticleSystemSimulationSpace.Local)
            {
                diagnostics.unsupported.Add("renderer.mesh.alignment.velocitySimulationSpace");
                diagnostics.approximated.Add("renderer.mesh.alignment.velocitySimulationSpace.unalignedFallback");
                diagnostics.warnings.Add("Mesh Velocity alignment is paired-runtime mapped only for Local simulation space. Best-effort keeps the unaligned Mesh fallback in other spaces; strict export fails.");
                return null;
            }

            diagnostics.mapped.Add("renderer.mesh.alignment.velocity.runtime");
            diagnostics.approximated.Add("renderer.mesh.alignment.velocity.stockUnalignedFallback");
            diagnostics.warnings.Add("The paired SDK aligns the Mesh local forward axis with current particle velocity after simulation behaviors. Stock Quarks playback keeps the authored particle quaternion without renderer Velocity alignment.");
            return Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.mesh_velocity_alignment.v1"))
                .Add("forwardAxis", Json.Array()
                    .Add(Json.Number(0))
                    .Add(Json.Number(0))
                    .Add(Json.Number(1)));
        }

        internal JsonObject BuildMeshCameraAlignmentMetadata(
            ParticleSystem system,
            ParticleSystemRenderer renderer,
            int renderMode,
            ConversionDiagnostics diagnostics)
        {
            if (renderMode != 2 || renderer == null ||
                (renderer.alignment != ParticleSystemRenderSpace.View &&
                 renderer.alignment != ParticleSystemRenderSpace.Facing))
            {
                return null;
            }

            if (system.main.simulationSpace != ParticleSystemSimulationSpace.Local)
            {
                diagnostics.unsupported.Add("renderer.mesh.alignment.cameraFacingSimulationSpace");
                diagnostics.approximated.Add("renderer.mesh.alignment.cameraFacingSimulationSpace.unalignedFallback");
                diagnostics.warnings.Add("Mesh View/Facing alignment is paired-runtime mapped only for Local simulation space. Best-effort keeps the unaligned Mesh fallback in other spaces; strict export fails.");
                return null;
            }

            var mode = renderer.alignment == ParticleSystemRenderSpace.View ? "view" : "facing";
            diagnostics.mapped.Add("renderer.mesh.alignment." + mode + ".runtime");
            diagnostics.approximated.Add("renderer.mesh.alignment.cameraFacing.stockUnalignedFallback");
            diagnostics.warnings.Add("The paired SDK applies " + mode + " camera alignment after authored Mesh rotation. Stock Quarks keeps the authored Mesh quaternion without camera-facing alignment.");
            return Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.mesh_camera_alignment.v1"))
                .Add("mode", Json.String(mode))
                .Add("forwardAxis", Json.Array()
                    .Add(Json.Number(0))
                    .Add(Json.Number(0))
                    .Add(Json.Number(1)))
                .Add("upAxis", Json.Array()
                    .Add(Json.Number(0))
                    .Add(Json.Number(1))
                    .Add(Json.Number(0)))
                .Add("preserveAuthoredRotation", Json.Boolean(true))
                .Add("simulationSpace", Json.String("local"));
        }

        private static JsonObject BufferAttribute(int itemSize, string type, float[] values)
        {
            var array = Json.Array();
            foreach (var value in values) array.Add(Json.Number(value));
            return Json.Object()
                .Add("itemSize", Json.Number(itemSize))
                .Add("type", Json.String(type))
                .Add("array", array)
                .Add("normalized", Json.Boolean(false));
        }

        private static JsonObject IndexAttribute(int[] values)
        {
            var array = Json.Array();
            foreach (var value in values) array.Add(Json.Number(value));
            return Json.Object()
                .Add("type", Json.String(values.Any(value => value > ushort.MaxValue)
                    ? "Uint32Array"
                    : "Uint16Array"))
                .Add("array", array);
        }
    }
}
