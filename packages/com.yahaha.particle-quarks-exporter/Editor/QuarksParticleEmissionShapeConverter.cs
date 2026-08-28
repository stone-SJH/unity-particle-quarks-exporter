using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using static UnityParticleQuarksExporter.Editor.QuarksCoordinateUtility;
using static UnityParticleQuarksExporter.Editor.QuarksParticleSemanticsUtility;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class ShapeBakeContext
    {
        public bool bakeCircleToMeshSurface;
        public Vector3 faceNormal;
    }

    internal sealed class QuarksParticleEmissionShapeConverter
    {
        private readonly QuarksGeometryRendererConverter geometryRendererConverter;

        internal QuarksParticleEmissionShapeConverter(
            QuarksGeometryRendererConverter geometryRenderer)
        {
            geometryRendererConverter = geometryRenderer ??
                throw new ArgumentNullException(nameof(geometryRenderer));
        }

        internal JsonValue BuildEmissionOverTime(
            ParticleSystem.EmissionModule emission,
            ConversionDiagnostics diagnostics)
        {
            return Curve(emission.enabled ? emission.rateOverTime : ZeroCurve(), diagnostics, "emission.rateOverTime");
        }

        internal JsonValue BuildEmissionOverDistance(
            ParticleSystem.EmissionModule emission,
            ConversionDiagnostics diagnostics)
        {
            return Curve(emission.enabled ? emission.rateOverDistance : ZeroCurve(), diagnostics, "emission.rateOverDistance");
        }

        internal ShapeBakeContext BuildShapeBakeContext(
            ParticleSystem system,
            int renderMode,
            ConversionDiagnostics diagnostics)
        {
            return new ShapeBakeContext();
        }

        internal JsonValue BuildShape(
            ParticleSystem system,
            string path,
            ShapeBakeContext bake,
            Vector3 shapeScale,
            ConversionDiagnostics diagnostics)
        {
            var shape = system.shape;
            if (!shape.enabled)
            {
                diagnostics.mapped.Add("shape.point");
                return Json.Object().Add("type", Json.String("point"));
            }

            DiagnoseShapeDirectionOptions(system, diagnostics);
            var common = Json.Object();
            switch (shape.shapeType)
            {
                case ParticleSystemShapeType.Cone:
                case ParticleSystemShapeType.ConeVolume:
                case ParticleSystemShapeType.ConeVolumeShell:
                    var coneScale = UniformShapeScale(shapeScale, "shape.cone.scalingModeShape", diagnostics);
                    diagnostics.mapped.Add("shape.cone");
                    return common.Add("type", Json.String("cone"))
                        .Add("radius", Json.Number(shape.radius * coneScale))
                        .Add("arc", Json.Number(shape.arc * Mathf.Deg2Rad))
                        .Add("thickness", Json.Number(shape.radiusThickness))
                        .Add("angle", Json.Number(shape.angle * Mathf.Deg2Rad))
                        .Add("mode", Json.Number(EmitterMode(shape.arcMode, "shape.cone.arcMode", diagnostics)))
                        .Add("spread", Json.Number(shape.arcSpread))
                        .Add("speed", Constant(1));
                case ParticleSystemShapeType.Sphere:
                case ParticleSystemShapeType.SphereShell:
                    var sphereScale = UniformShapeScale(shapeScale, "shape.sphere.scalingModeShape", diagnostics);
                    DiagnoseRadialVolumeDistribution(
                        "sphere",
                        shape.shapeType == ParticleSystemShapeType.SphereShell ? 0 : shape.radiusThickness,
                        diagnostics);
                    diagnostics.mapped.Add("shape.sphere");
                    return common.Add("type", Json.String("sphere"))
                        .Add("radius", Json.Number(shape.radius * sphereScale))
                        .Add("arc", Json.Number(Mathf.PI * 2))
                        .Add("thickness", Json.Number(shape.shapeType == ParticleSystemShapeType.SphereShell ? 0 : shape.radiusThickness))
                        .Add("mode", Json.Number(0)).Add("spread", Json.Number(0)).Add("speed", Constant(1));
                case ParticleSystemShapeType.Hemisphere:
                case ParticleSystemShapeType.HemisphereShell:
                    var hemisphereScale = UniformShapeScale(shapeScale, "shape.hemisphere.scalingModeShape", diagnostics);
                    DiagnoseRadialVolumeDistribution(
                        "hemisphere",
                        shape.shapeType == ParticleSystemShapeType.HemisphereShell ? 0 : shape.radiusThickness,
                        diagnostics);
                    diagnostics.mapped.Add("shape.hemisphere");
                    return common.Add("type", Json.String("hemisphere"))
                        .Add("radius", Json.Number(shape.radius * hemisphereScale))
                        .Add("arc", Json.Number(Mathf.PI * 2))
                        .Add("thickness", Json.Number(shape.shapeType == ParticleSystemShapeType.HemisphereShell ? 0 : shape.radiusThickness))
                        .Add("mode", Json.Number(0)).Add("spread", Json.Number(0)).Add("speed", Constant(1));
                case ParticleSystemShapeType.Circle:
                case ParticleSystemShapeType.CircleEdge:
                    if (bake.bakeCircleToMeshSurface)
                    {
                        var geometry = geometryRendererConverter.RegisterCircleSamplingGeometry(
                            shape,
                            bake.faceNormal,
                            shapeScale,
                            path + ":shape-circle-mesh-surface",
                            diagnostics);
                        return common.Add("type", Json.String("mesh_surface"))
                            .Add("geometry", Json.String(geometry));
                    }
                    var circleScale = UniformShapeScale(shapeScale, "shape.circle.scalingModeShape", diagnostics);
                    diagnostics.mapped.Add("shape.circle");
                    return common.Add("type", Json.String("circle"))
                        .Add("radius", Json.Number(shape.radius * circleScale))
                        .Add("arc", Json.Number(shape.arc * Mathf.Deg2Rad))
                        .Add("thickness", Json.Number(shape.shapeType == ParticleSystemShapeType.CircleEdge ? 0 : shape.radiusThickness))
                        .Add("mode", Json.Number(EmitterMode(shape.arcMode, "shape.circle.arcMode", diagnostics)))
                        .Add("spread", Json.Number(shape.arcSpread)).Add("speed", Constant(1));
                case ParticleSystemShapeType.SingleSidedEdge:
                    // A zero-height Quarks rectangle is the closest stock shape:
                    // it remains loadable and distributes particles along a line.
                    // The paired runtime metadata below restores Unity's exact
                    // [-radius, radius] phase and local +Y emission direction.
                    var edgeScale = UniformShapeScale(shapeScale, "shape.singleSidedEdge.scalingModeShape", diagnostics);
                    diagnostics.mapped.Add("shape.singleSidedEdge.runtime");
                    diagnostics.approximated.Add("shape.singleSidedEdge.stockRectangleFallback");
                    diagnostics.warnings.Add("Unity Single Sided Edge birth positions and local +Y direction are preserved by paired SDK Shape metadata. Stock Quarks uses a zero-height rectangle fallback whose Random distribution is equivalent but whose Loop/PingPong phase and radial direction are not.");
                    return common.Add("type", Json.String("rectangle"))
                        .Add("width", Json.Number(shape.radius * edgeScale * 2))
                        .Add("height", Json.Number(0))
                        .Add("thickness", Json.Number(0))
                        .Add("mode", Json.Number(EmitterMode(shape.radiusMode, "shape.singleSidedEdge.radiusMode", diagnostics)))
                        .Add("spread", Json.Number(shape.radiusSpread))
                        .Add("speed", Curve(shape.radiusSpeed, diagnostics, "shape.singleSidedEdge.radiusSpeed"));
                case ParticleSystemShapeType.Donut:
                    var donutScale = UniformShapeScale(shapeScale, "shape.donut.scalingModeShape", diagnostics);
                    diagnostics.mapped.Add("shape.donut");
                    return common.Add("type", Json.String("donut"))
                        .Add("radius", Json.Number(shape.radius * donutScale))
                        .Add("arc", Json.Number(shape.arc * Mathf.Deg2Rad))
                        .Add("thickness", Json.Number(shape.radiusThickness))
                        .Add("donutRadius", Json.Number(shape.donutRadius * donutScale))
                        .Add("mode", Json.Number(EmitterMode(shape.arcMode, "shape.donut.arcMode", diagnostics)))
                        .Add("spread", Json.Number(shape.arcSpread)).Add("speed", Constant(1));
                case ParticleSystemShapeType.Rectangle:
                    diagnostics.mapped.Add("shape.rectangle");
                    return common.Add("type", Json.String("rectangle"))
                        .Add("width", Json.Number(shape.scale.x * shapeScale.x))
                        .Add("height", Json.Number(shape.scale.y * shapeScale.y))
                        .Add("thickness", Json.Number(0))
                        .Add("mode", Json.Number(0)).Add("spread", Json.Number(0)).Add("speed", Constant(1));
                case ParticleSystemShapeType.Mesh:
                    if (shape.mesh != null)
                    {
                        DiagnoseMeshShapeOptions(shape, diagnostics);
                        string geometry;
                        switch (shape.meshShapeType)
                        {
                            case ParticleSystemMeshShapeType.Vertex:
                                geometry = geometryRendererConverter.RegisterMeshVertexSamplingGeometry(
                                    shape.mesh,
                                    shapeScale,
                                    path + ":shape-mesh-vertex",
                                    diagnostics);
                                diagnostics.approximated.Add("shape.meshVertex");
                                diagnostics.warnings.Add("Unity Mesh Shape Vertex sampling is approximated with equal-area microscopic triangles centered on every source vertex for stock Quarks mesh_surface loading.");
                                break;
                            case ParticleSystemMeshShapeType.Triangle:
                                geometry = geometryRendererConverter.RegisterGeometry(
                                    shape.mesh,
                                    shapeScale,
                                    path + ":shape-mesh-surface",
                                    diagnostics);
                                diagnostics.mapped.Add("shape.meshSurface");
                                break;
                            case ParticleSystemMeshShapeType.Edge:
                                geometry = geometryRendererConverter.RegisterGeometry(
                                    shape.mesh,
                                    shapeScale,
                                    path + ":shape-mesh-edge-fallback",
                                    diagnostics);
                                diagnostics.unsupported.Add("shape.meshEdge");
                                diagnostics.approximated.Add("shape.meshEdge.meshSurfaceFallback");
                                diagnostics.warnings.Add("Unity Mesh Shape Edge sampling has no stock Quarks equivalent; best-effort output uses mesh-surface sampling.");
                                break;
                            default:
                                geometry = geometryRendererConverter.RegisterGeometry(
                                    shape.mesh,
                                    shapeScale,
                                    path + ":shape-mesh-unsupported",
                                    diagnostics);
                                diagnostics.unsupported.Add("shape.meshPlacement." + shape.meshShapeType);
                                diagnostics.approximated.Add("shape.meshPlacement.meshSurfaceFallback");
                                diagnostics.warnings.Add("The Unity Mesh Shape placement mode has no stock Quarks equivalent; best-effort output uses mesh-surface sampling.");
                                break;
                        }
                        return common.Add("type", Json.String("mesh_surface")).Add("geometry", Json.String(geometry));
                    }
                    diagnostics.unsupported.Add("shape.meshSurfaceMissingMesh");
                    diagnostics.approximated.Add("shape.meshSurfaceMissingMesh.pointFallback");
                    diagnostics.warnings.Add("Mesh Shape has no assigned mesh. Best-effort explicitly emits from a point; strict export fails.");
                    return Json.Object().Add("type", Json.String("point"));
                case ParticleSystemShapeType.Box:
                    diagnostics.mapped.Add("shape.boxVolume.runtime");
                    diagnostics.mapped.Add("shape.boxDimensions");
                    diagnostics.approximated.Add("shape.boxDiscreteGrid.stockFallback");
                    diagnostics.warnings.Add("Unity Box volume placement is preserved in exporter metadata for the paired SDK runtime. Stock Quarks playback retains a deterministic 10x10x10 mesh-surface fallback and is not distribution-equivalent.");
                    return common.Add("type", Json.String("mesh_surface"))
                        .Add("geometry", Json.String(geometryRendererConverter.RegisterBoxSamplingGeometry(
                            shape,
                            shapeScale,
                            path + ":shape-box-discrete-grid",
                            diagnostics)));
                case ParticleSystemShapeType.BoxShell:
                case ParticleSystemShapeType.BoxEdge:
                    diagnostics.unsupported.Add("shape." + shape.shapeType);
                    diagnostics.approximated.Add("shape.boxDiscreteGrid.meshSurfaceFallback");
                    diagnostics.mapped.Add("shape.boxDimensions");
                    diagnostics.warnings.Add("Unity Box placement has no continuous stock Quarks volume/shell/edge emitter; best-effort uses equal-area microscopic triangles on a deterministic 10x10x10 position grid with local +Z emission direction.");
                    return common.Add("type", Json.String("mesh_surface"))
                        .Add("geometry", Json.String(geometryRendererConverter.RegisterBoxSamplingGeometry(
                            shape,
                            shapeScale,
                            path + ":shape-box-discrete-grid",
                            diagnostics)));
                default:
                    diagnostics.unsupported.Add("shape." + shape.shapeType);
                    diagnostics.approximated.Add("shape.unsupported.pointFallback");
                    diagnostics.warnings.Add("The active Unity Shape type has no mapped emitter. Best-effort explicitly emits from a point; strict export fails.");
                    return Json.Object().Add("type", Json.String("point"));
            }
        }

        private static void DiagnoseShapeDirectionOptions(
            ParticleSystem system,
            ConversionDiagnostics diagnostics)
        {
            var shape = system.shape;
            if (shape.alignToDirection)
            {
                if (shape.shapeType == ParticleSystemShapeType.Mesh &&
                    shape.meshShapeType == ParticleSystemMeshShapeType.Triangle &&
                    system.main.simulationSpace == ParticleSystemSimulationSpace.Local)
                {
                    diagnostics.mapped.Add("shape.alignToDirection.runtime");
                    diagnostics.approximated.Add("shape.alignToDirection.stockUnalignedFallback");
                    diagnostics.warnings.Add("The paired SDK aligns Mesh Triangle birth orientation with the sampled direction in Local simulation space. Billboard and non-Local combinations remain unsupported.");
                }
                else
                {
                    diagnostics.unsupported.Add("shape.alignToDirection");
                    diagnostics.approximated.Add("shape.alignToDirection.omittedFallback");
                    diagnostics.warnings.Add("Unity Shape Align To Direction is only mapped for Mesh Triangle Local simulation. Best-effort explicitly omits it for other renderer/sampling combinations; strict export fails.");
                }
            }
            if (Mathf.Abs(shape.randomDirectionAmount) > 0.000001f)
            {
                if (!CurveHasEffect(system.main.startSpeed))
                {
                    diagnostics.inactive.Add("shape.randomDirectionAmount.zeroStartSpeed");
                }
                else if (SupportsRuntimeRandomDirection(shape.shapeType))
                {
                    // BuildShapeSemanticsMetadata emits the paired-runtime
                    // metadata after the stock shape has been selected.
                }
                else
                {
                    diagnostics.unsupported.Add("shape.randomDirectionAmount");
                    diagnostics.approximated.Add("shape.randomDirectionAmount.stockShapeDirectionFallback");
                    diagnostics.warnings.Add("Unity randomDirectionAmount is active on a Shape type whose base emission direction is not mapped by the paired SDK runtime. Best-effort explicitly retains the stock Quarks Shape direction; strict export fails.");
                }
            }
            var primitiveRandomPosition = SupportsRuntimePrimitiveShape(shape.shapeType);
            if (Mathf.Abs(shape.sphericalDirectionAmount) > 0.000001f)
            {
                if (primitiveRandomPosition)
                {
                    diagnostics.mapped.Add("shape.sphericalDirectionAmount.runtime");
                    diagnostics.approximated.Add("shape.sphericalDirectionAmount.stockShapeFallback");
                }
                else
                {
                    diagnostics.unsupported.Add("shape.sphericalDirectionAmount");
                    diagnostics.approximated.Add("shape.sphericalDirectionAmount.omittedFallback");
                    diagnostics.warnings.Add("Unity Shape sphericalDirectionAmount has no equivalent for this Shape type. Best-effort explicitly omits it; strict export fails.");
                }
            }
            if (Mathf.Abs(shape.randomPositionAmount) > 0.000001f)
            {
                if (primitiveRandomPosition)
                {
                    diagnostics.mapped.Add("shape.randomPositionAmount.runtime");
                    diagnostics.approximated.Add("shape.randomPositionAmount.stockShapeFallback");
                }
                else
                {
                    diagnostics.unsupported.Add("shape.randomPositionAmount");
                    diagnostics.approximated.Add("shape.randomPositionAmount.omittedFallback");
                    diagnostics.warnings.Add("Unity Shape randomPositionAmount has no equivalent for this Shape type. Best-effort explicitly omits it; strict export fails.");
                }
            }
        }

        private static bool SupportsRuntimePrimitiveShape(ParticleSystemShapeType shapeType)
        {
            return shapeType == ParticleSystemShapeType.Cone ||
                   shapeType == ParticleSystemShapeType.ConeVolume ||
                   shapeType == ParticleSystemShapeType.ConeVolumeShell ||
                   shapeType == ParticleSystemShapeType.Sphere ||
                   shapeType == ParticleSystemShapeType.SphereShell ||
                   shapeType == ParticleSystemShapeType.Box ||
                   shapeType == ParticleSystemShapeType.BoxShell ||
                   shapeType == ParticleSystemShapeType.BoxEdge;
        }

        private static void DiagnoseMeshShapeOptions(
            ParticleSystem.ShapeModule shape,
            ConversionDiagnostics diagnostics)
        {
            if (shape.useMeshColors)
            {
                if (shape.mesh.HasVertexAttribute(VertexAttribute.Color))
                {
                    diagnostics.unsupported.Add("shape.meshColors");
                    diagnostics.approximated.Add("shape.meshColors.omittedFallback");
                    diagnostics.warnings.Add("Unity Mesh Shape vertex-color modulation is active but stock Quarks mesh_surface does not expose sampled vertex color. Best-effort explicitly omits modulation; strict export fails.");
                }
                else
                {
                    diagnostics.inactive.Add("shape.meshColors");
                }
            }

            if (shape.useMeshMaterialIndex)
            {
                diagnostics.unsupported.Add("shape.meshMaterialIndex");
                diagnostics.approximated.Add("shape.meshMaterialIndex.wholeMeshFallback");
                diagnostics.warnings.Add("Unity Mesh Shape material-index filtering is active. Best-effort samples the exported mesh as a whole; strict export fails.");
            }
            if (Mathf.Abs(shape.normalOffset) > 0.000001f)
            {
                if (shape.meshShapeType == ParticleSystemMeshShapeType.Triangle)
                {
                    diagnostics.mapped.Add("shape.meshNormalOffset.runtime");
                    diagnostics.approximated.Add("shape.meshNormalOffset.stockOmittedFallback");
                    diagnostics.warnings.Add("The paired SDK offsets Mesh Triangle birth positions along the sampled surface normal. Stock Quarks playback explicitly retains the unoffset surface position.");
                }
                else
                {
                    diagnostics.unsupported.Add("shape.meshNormalOffset");
                    diagnostics.approximated.Add("shape.meshNormalOffset.omittedFallback");
                    diagnostics.warnings.Add("Unity Mesh Shape normal offset has no reliable normal for the selected non-Triangle sampling mode. Best-effort explicitly omits the offset; strict export fails.");
                }
            }
        }

        internal JsonArray BuildBursts(ParticleSystem.EmissionModule emission, ConversionDiagnostics diagnostics)
        {
            var result = Json.Array();
            if (!emission.enabled || emission.burstCount == 0) return result;
            var bursts = new ParticleSystem.Burst[emission.burstCount];
            emission.GetBursts(bursts);
            foreach (var burst in bursts.OrderBy(item => item.time))
            {
                result.Add(Json.Object()
                    .Add("time", Json.Number(burst.time))
                    .Add("count", Curve(burst.count, diagnostics, "emission.burst.count"))
                    .Add("cycle", Json.Number(burst.cycleCount))
                    .Add("interval", Json.Number(burst.repeatInterval))
                    .Add("probability", Json.Number(burst.probability)));
            }
            return result;
        }

        internal JsonObject BuildShapeSemanticsMetadata(
            ParticleSystem system,
            ScalingContext scaling,
            ConversionDiagnostics diagnostics)
        {
            var shape = system.shape;
            JsonObject distribution = null;
            string directionMode = null;
            if (shape.enabled)
            {
                switch (shape.shapeType)
                {
                    case ParticleSystemShapeType.Sphere:
                    case ParticleSystemShapeType.Hemisphere:
                        if (shape.radiusThickness > 0.000001f)
                        {
                            distribution = Json.Object()
                                .Add("type", Json.String(shape.shapeType == ParticleSystemShapeType.Sphere
                                    ? "sphereVolume"
                                    : "hemisphereVolume"))
                                .Add("radius", Json.Number(shape.radius))
                                .Add("thickness", Json.Number(Mathf.Clamp01(shape.radiusThickness)));
                        }
                        break;
                    case ParticleSystemShapeType.Box:
                        distribution = Json.Object()
                            .Add("type", Json.String("boxVolume"))
                            .Add("size", VectorArray(shape.scale));
                        break;
                    case ParticleSystemShapeType.Rectangle:
                        if (CurveHasEffect(system.main.startSpeed)) directionMode = "localZ";
                        break;
                    case ParticleSystemShapeType.SingleSidedEdge:
                        distribution = Json.Object()
                            .Add("type", Json.String("singleSidedEdge"))
                            .Add("radius", Json.Number(Mathf.Max(0, shape.radius)))
                            .Add("mode", Json.Number(EmitterMode(
                                shape.radiusMode,
                                "shape.singleSidedEdge.radiusMode",
                                diagnostics)))
                            .Add("spread", Json.Number(Mathf.Clamp01(shape.radiusSpread)));
                        directionMode = "localY";
                        break;
                }
            }

            var birthPositionTransform = Matrix4x4.identity;
            var birthDirectionTransform = Matrix4x4.identity;
            if (shape.enabled)
            {
                var scaleIsShapeSize = ShapeScaleIsShapeSize(shape.shapeType);
                var shapeTransformScale = Vector3.one;
                var shapeDirectionScale = Vector3.one;
                var hasShapeTransformScale = !scaleIsShapeSize && !Approximately(shape.scale, Vector3.one);
                if (hasShapeTransformScale)
                {
                    shapeTransformScale = NonSingularShapeTransformScale(shape.scale, diagnostics);
                    shapeDirectionScale = InverseScale(shapeTransformScale);
                }
                var sourceShapeTransform = Matrix4x4.TRS(
                    shape.position,
                    Quaternion.Euler(shape.rotation),
                    shapeTransformScale);
                var sourceShapeDirection = Matrix4x4.Rotate(Quaternion.Euler(shape.rotation)) *
                                           Matrix4x4.Scale(shapeDirectionScale);
                birthPositionTransform = UnityLocalToQuarksLocal *
                                         Matrix4x4.Scale(SignedScale(scaling.shapeScale, scaling.shapeAxisSigns)) *
                                         sourceShapeTransform *
                                         UnityLocalToQuarksLocal;
                birthDirectionTransform = UnityLocalToQuarksLocal *
                                          Matrix4x4.Scale(scaling.shapeAxisSigns) *
                                          sourceShapeDirection *
                                          UnityLocalToQuarksLocal;
                if (!Approximately(shape.position, Vector3.zero)) diagnostics.mapped.Add("shape.transform.position.runtime");
                if (!Approximately(shape.rotation, Vector3.zero)) diagnostics.mapped.Add("shape.transform.rotation.runtime");

                if (!Approximately(scaling.shapeScale, Vector3.one))
                {
                    diagnostics.mapped.Add("main.scalingMode.shape.positionRuntime");
                    diagnostics.approximated.Add("main.scalingMode.shape.stockUnitShapeFallback");
                    diagnostics.warnings.Add("ParticleSystem Shape scaling applies the full hierarchy magnitude only to birth positions and the axis signs to birth directions in the paired SDK. Stock Quarks playback explicitly retains the unscaled source shape.");
                }

                if (hasShapeTransformScale)
                {
                    diagnostics.mapped.Add("shape.transform.scale.runtime");
                    diagnostics.approximated.Add("shape.transform.scale.stockUnitShapeFallback");
                    diagnostics.warnings.Add("ParticleSystem Shape module scale is applied to birth positions and inverse-transpose birth directions by the paired SDK runtime. Stock Quarks playback explicitly retains the unscaled primitive shape.");
                }
            }
            else if (!Approximately(scaling.particleAxisSigns, Vector3.one))
            {
                birthPositionTransform = Matrix4x4.Scale(scaling.particleAxisSigns);
                birthDirectionTransform = birthPositionTransform;
            }

            var correctWorldVelocity = system.main.simulationSpace == ParticleSystemSimulationSpace.World;
            var hasBirthPositionTransform = !Approximately(birthPositionTransform, Matrix4x4.identity);
            var hasBirthDirectionTransform = !Approximately(birthDirectionTransform, Matrix4x4.identity);
            var hasRuntimeRandomDirection = shape.enabled &&
                                            Mathf.Abs(shape.randomDirectionAmount) > 0.000001f &&
                                            CurveHasEffect(system.main.startSpeed) &&
                                            SupportsRuntimeRandomDirection(shape.shapeType);
            var hasRuntimePrimitiveRandomPosition = shape.enabled &&
                                                    SupportsRuntimePrimitiveShape(shape.shapeType) &&
                                                    (Mathf.Abs(shape.randomPositionAmount) > 0.000001f ||
                                                     Mathf.Abs(shape.sphericalDirectionAmount) > 0.000001f);
            var hasRuntimeAlignToDirection = shape.enabled &&
                                             shape.shapeType == ParticleSystemShapeType.Mesh &&
                                             shape.meshShapeType == ParticleSystemMeshShapeType.Triangle &&
                                             shape.alignToDirection &&
                                             system.main.simulationSpace == ParticleSystemSimulationSpace.Local;
            var hasRuntimeMeshNormalOffset = shape.enabled &&
                                             shape.shapeType == ParticleSystemShapeType.Mesh &&
                                             shape.meshShapeType == ParticleSystemMeshShapeType.Triangle &&
                                             Mathf.Abs(shape.normalOffset) > 0.000001f;
            if (distribution == null && directionMode == null && !hasBirthPositionTransform &&
                !hasBirthDirectionTransform && !correctWorldVelocity && !hasRuntimeRandomDirection &&
                !hasRuntimeMeshNormalOffset && !hasRuntimePrimitiveRandomPosition && !hasRuntimeAlignToDirection)
                return null;

            var metadata = Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.shape_semantics.v1"));
            if (distribution != null) metadata.Add("distribution", distribution);
            if (directionMode != null)
            {
                metadata.Add("directionMode", Json.String(directionMode));
                if (directionMode == "localZ")
                {
                    diagnostics.mapped.Add("shape.rectangle.normalDirectionRuntime");
                    diagnostics.approximated.Add("shape.rectangle.stockRadialDirectionFallback");
                    diagnostics.warnings.Add("Unity Rectangle emits along its local +Z normal, while stock Quarks RectangleEmitter emits radially in the rectangle plane. The paired SDK corrects the birth direction; stock playback remains an explicit radial fallback.");
                }
                else
                {
                    diagnostics.mapped.Add("shape.singleSidedEdge.localYDirectionRuntime");
                    diagnostics.approximated.Add("shape.singleSidedEdge.stockRadialDirectionFallback");
                }
            }
            if (hasBirthPositionTransform || hasBirthDirectionTransform)
            {
                metadata.Add("birthPositionTransform", MatrixArray(birthPositionTransform));
                metadata.Add("birthDirectionTransform", MatrixArray(birthDirectionTransform));
                diagnostics.mapped.Add("shape.birthTransform.runtime");
                diagnostics.approximated.Add("shape.birthTransform.stockOmittedFallback");
                diagnostics.warnings.Add("Shape position, rotation, and signed scaling are applied at particle birth by the paired SDK so they do not rotate unrelated simulation modules or Mesh renderer orientation. Stock Quarks playback explicitly omits this birth transform.");
            }
            if (correctWorldVelocity)
            {
                metadata.Add("correctWorldSpaceBirthVelocity", Json.Boolean(true));
                diagnostics.mapped.Add("main.worldSpaceBirthVelocity.runtimeMatrix");
                diagnostics.approximated.Add("main.worldSpaceBirthVelocity.stockNormalMatrixFallback");
                diagnostics.warnings.Add("The paired SDK applies the emitter linear matrix to world-space birth velocity. Stock Quarks uses a normal matrix that cancels emitter scale and is an explicit non-equivalent fallback.");
            }
            if (hasRuntimeRandomDirection)
            {
                var amount = Mathf.Clamp01(shape.randomDirectionAmount);
                if (shape.shapeType == ParticleSystemShapeType.Cone)
                {
                    metadata.Add("randomDirection", Json.Object()
                        .Add("mode", Json.String("coneSurface"))
                        .Add("amount", Json.Number(amount))
                        .Add("angle", Json.Number(shape.angle * Mathf.Deg2Rad))
                        .Add("radius", Json.Number(Mathf.Max(0, shape.radius))));
                    diagnostics.mapped.Add("shape.randomDirectionAmount.coneSurfaceRuntime");
                    diagnostics.warnings.Add("Unity Cone Shape randomDirectionAmount is reproduced by the paired SDK from the Unity ShapeModule source formula: the cone-local XY direction is lerped toward a random point inside the unit disk before applying the cone angle. Stock Quarks playback retains its authored Cone direction fallback.");
                }
                else
                {
                    metadata.Add("randomDirection", Json.Object()
                        .Add("mode", Json.String("lerpRandomUnit"))
                        .Add("amount", Json.Number(amount)));
                    diagnostics.mapped.Add("shape.randomDirectionAmount.randomUnitLerpRuntime");
                    diagnostics.warnings.Add("Unity Shape randomDirectionAmount is reproduced by the paired SDK from the Unity ShapeModule source formula: the stock shape direction is lerped toward a random unit vector and renormalized before Shape birth-direction transforms. Stock Quarks playback retains the unrandomized Shape direction fallback.");
                }
                diagnostics.mapped.Add("shape.randomDirectionAmount.runtime");
                diagnostics.approximated.Add("shape.randomDirectionAmount.stockShapeDirectionFallback");
            }
            if (hasRuntimeMeshNormalOffset)
            {
                metadata.Add("meshNormalOffset", Json.Number(shape.normalOffset));
                diagnostics.mapped.Add("shape.meshNormalOffset.runtime");
                diagnostics.approximated.Add("shape.meshNormalOffset.stockOmittedFallback");
            }
            if (hasRuntimePrimitiveRandomPosition)
            {
                metadata.Add("randomPosition", Json.Object()
                    .Add("amount", Json.Number(Mathf.Clamp01(shape.randomPositionAmount)))
                    .Add("sphericalAmount", Json.Number(Mathf.Clamp01(shape.sphericalDirectionAmount)))
                    .Add("mode", Json.String(shape.shapeType == ParticleSystemShapeType.Box ||
                                             shape.shapeType == ParticleSystemShapeType.BoxShell ||
                                             shape.shapeType == ParticleSystemShapeType.BoxEdge
                        ? "box" : "radial")));
                diagnostics.mapped.Add("shape.randomPositionAmount.runtime");
                diagnostics.mapped.Add("shape.sphericalDirectionAmount.runtime");
                diagnostics.approximated.Add("shape.randomPositionAmount.stockShapeFallback");
            }
            if (hasRuntimeAlignToDirection)
            {
                metadata.Add("alignToDirection", Json.Boolean(true));
                diagnostics.mapped.Add("shape.alignToDirection.runtime");
                diagnostics.approximated.Add("shape.alignToDirection.stockUnalignedFallback");
            }
            return metadata;
        }

        private static bool SupportsRuntimeRandomDirection(ParticleSystemShapeType shapeType)
        {
            switch (shapeType)
            {
                case ParticleSystemShapeType.Cone:
                case ParticleSystemShapeType.ConeVolume:
                case ParticleSystemShapeType.ConeVolumeShell:
                case ParticleSystemShapeType.Sphere:
                case ParticleSystemShapeType.SphereShell:
                case ParticleSystemShapeType.Hemisphere:
                case ParticleSystemShapeType.HemisphereShell:
                case ParticleSystemShapeType.Circle:
                case ParticleSystemShapeType.CircleEdge:
                case ParticleSystemShapeType.Donut:
                case ParticleSystemShapeType.Rectangle:
                case ParticleSystemShapeType.Box:
                    return true;
                default:
                    return false;
            }
        }

        private static bool ShapeScaleIsShapeSize(ParticleSystemShapeType shapeType)
        {
            return shapeType == ParticleSystemShapeType.Rectangle ||
                   shapeType == ParticleSystemShapeType.Box ||
                   shapeType == ParticleSystemShapeType.BoxShell ||
                   shapeType == ParticleSystemShapeType.BoxEdge;
        }

        private static Vector3 NonSingularShapeTransformScale(
            Vector3 scale,
            ConversionDiagnostics diagnostics)
        {
            var result = scale;
            if (Mathf.Abs(result.x) <= 0.000001f ||
                Mathf.Abs(result.y) <= 0.000001f ||
                Mathf.Abs(result.z) <= 0.000001f)
            {
                diagnostics.unsupported.Add("shape.transform.scale.zeroAxis");
                diagnostics.approximated.Add("shape.transform.scale.zeroAxis.epsilonFallback");
                diagnostics.warnings.Add("ParticleSystem Shape module scale contains a zero axis, which makes the birth-direction normal basis singular. Best-effort clamps that axis to 0.000001 with its sign preserved; strict export fails.");
                result.x = ClampSignedAxis(result.x);
                result.y = ClampSignedAxis(result.y);
                result.z = ClampSignedAxis(result.z);
            }
            return result;
        }

        private static float ClampSignedAxis(float value)
        {
            if (Mathf.Abs(value) > 0.000001f) return value;
            return value < 0 ? -0.000001f : 0.000001f;
        }

        private static Vector3 InverseScale(Vector3 scale)
        {
            return new Vector3(1f / scale.x, 1f / scale.y, 1f / scale.z);
        }

        private static float UniformShapeScale(
            Vector3 scale,
            string field,
            ConversionDiagnostics diagnostics)
        {
            if (Mathf.Abs(scale.x - scale.y) <= 0.000001f &&
                Mathf.Abs(scale.x - scale.z) <= 0.000001f)
            {
                return scale.x;
            }
            diagnostics.unsupported.Add(field + ".nonUniform");
            diagnostics.approximated.Add(field + ".nonUniformMean");
            diagnostics.warnings.Add(field + " is non-uniform and would turn the stock axial emitter into an ellipsoid. Best-effort uses the arithmetic mean radius.");
            return (scale.x + scale.y + scale.z) / 3f;
        }

        private static void DiagnoseRadialVolumeDistribution(
            string shape,
            float thickness,
            ConversionDiagnostics diagnostics)
        {
            if (thickness <= 0.000001f) return;
            diagnostics.mapped.Add("shape." + shape + ".uniformVolumeRuntime");
            diagnostics.approximated.Add("shape." + shape + ".linearRadiusStockFallback");
            diagnostics.warnings.Add("Unity " + shape + " volume emission is corrected to a uniform-volume radius by the paired SDK runtime. Stock Quarks 0.17.1 samples radius linearly, so playback without the exporter compatibility behavior remains an explicit approximation.");
        }


        internal JsonObject BuildSimulationSpeedMetadata(
            ParticleSystem system,
            ConversionDiagnostics diagnostics)
        {
            var speed = Mathf.Max(0, system.main.simulationSpeed);
            if (Mathf.Abs(speed - 1) <= 0.000001f)
            {
                diagnostics.mapped.Add("main.simulationSpeed.identity");
                return null;
            }

            diagnostics.mapped.Add("main.simulationSpeed.runtime");
            diagnostics.approximated.Add("main.simulationSpeed.stockUnitSpeedFallback");
            diagnostics.warnings.Add("The paired SDK scales each ParticleSystem simulation step by main.simulationSpeed. Stock Quarks playback keeps unit simulation speed.");
            return Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.simulation_speed.v1"))
                .Add("value", Json.Number(speed));
        }

        internal void DiagnoseEmitterConfiguration(
            ParticleSystem system,
            bool materialConsumesParticleColor,
            JsonObject startDelaySemantics,
            ConversionDiagnostics diagnostics)
        {
            var main = system.main;
            if (startDelaySemantics == null &&
                (main.startDelay.constant > 0 ||
                 main.startDelay.constantMin > 0 ||
                 main.startDelay.constantMax > 0))
            {
                diagnostics.approximated.Add("main.startDelay");
                diagnostics.warnings.Add("Quarks v0 starts immediately; Unity start delay is not equivalent.");
            }
            else if (startDelaySemantics == null)
            {
                diagnostics.inactive.Add("main.startDelay");
            }

            diagnostics.mapped.UnionWith(new[]
            {
                "main.duration", "main.loop", "main.prewarm", "main.startLifetime",
                "main.startSize", "main.startRotation", "emission.rateOverTime",
                "emission.rateOverDistance", "emission.bursts"
            });
            diagnostics.mapped.Add("main.maxParticles.runtimeCapacity");
            diagnostics.approximated.Add("main.maxParticles.stockUnboundedFallback");
            diagnostics.warnings.Add("Unity maxParticles is enforced by the paired SDK runtime for regular emission, bursts, prewarm, and subemission. Stock Quarks playback remains explicitly unbounded.");

            if (materialConsumesParticleColor)
            {
                diagnostics.mapped.Add("main.startColor");
            }
            else
            {
                diagnostics.inactive.Add("main.startColor.notConsumedBySourceShader");
                diagnostics.warnings.Add("The source shader does not consume ParticleSystem vertex color. Start Color is explicitly neutralized instead of changing the converted material output.");
            }
            diagnostics.mapped.Add("main.startSpeed");

            if (main.simulationSpace == ParticleSystemSimulationSpace.Custom)
            {
                diagnostics.unsupported.Add("main.customSimulationSpace");
                diagnostics.approximated.Add("main.customSimulationSpace.localFallback");
                diagnostics.warnings.Add("Custom simulation space cannot be represented by the wrapper contract. Best-effort explicitly uses local simulation under the flattened emitter transform; strict export fails.");
            }
        }

        internal JsonObject BuildStartDelayMetadata(
            ParticleSystem system,
            ConversionDiagnostics diagnostics)
        {
            var delay = system.main.startDelay;
            var hasEffect = delay.mode switch
            {
                ParticleSystemCurveMode.Constant => delay.constant > 0.000001f,
                ParticleSystemCurveMode.TwoConstants => delay.constantMin > 0.000001f || delay.constantMax > 0.000001f,
                ParticleSystemCurveMode.Curve => CurveHasEffect(delay),
                ParticleSystemCurveMode.TwoCurves => CurveHasEffect(delay),
                _ => false
            };
            if (!hasEffect)
            {
                diagnostics.inactive.Add("main.startDelay");
                return null;
            }
            diagnostics.mapped.Add("main.startDelay.runtime");
            diagnostics.approximated.Add("main.startDelay.stockImmediateFallback");
            diagnostics.warnings.Add("The paired SDK gates emitter, burst, and subemitter emission until the authored Unity startDelay has elapsed. Stock Quarks starts immediately.");
            return Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.start_delay.v1"))
                .Add("randomSeed", Json.Number(system.randomSeed))
                .Add("delay", VelocityCurveMetadata(delay, diagnostics, "main.startDelay"));
        }

        internal JsonObject BuildLifetimeByEmitterSpeedMetadata(
            ParticleSystem system,
            ConversionDiagnostics diagnostics)
        {
            if (!TryGetLifetimeByEmitterSpeedModule(system, out var module)) return null;
            var curveProperty = module.GetType().GetProperty("curve");
            var rangeProperty = module.GetType().GetProperty("range");
            if (curveProperty == null || rangeProperty == null ||
                !(curveProperty.GetValue(module, null) is ParticleSystem.MinMaxCurve curve) ||
                !(rangeProperty.GetValue(module, null) is Vector2 range))
            {
                diagnostics.unsupported.Add("main.lifetimeByEmitterSpeed.malformedModule");
                diagnostics.approximated.Add("main.lifetimeByEmitterSpeed.omittedFallback");
                diagnostics.warnings.Add("Unity lifetimeByEmitterSpeed is enabled but its curve/range properties could not be read. Best-effort omits the module; strict export fails.");
                return null;
            }
            diagnostics.mapped.Add("main.lifetimeByEmitterSpeed.runtime");
            diagnostics.approximated.Add("main.lifetimeByEmitterSpeed.stockUnscaledFallback");
            diagnostics.warnings.Add("The paired SDK samples emitter translation speed at birth and scales the authored lifetime once. Existing particles are not rewritten when the emitter moves later.");
            return Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.lifetime_by_emitter_speed.v1"))
                .Add("randomSeed", Json.Number(system.randomSeed))
                .Add("range", Json.Array().Add(Json.Number(range.x)).Add(Json.Number(range.y)))
                .Add("curve", VelocityCurveMetadata(curve, diagnostics, "main.lifetimeByEmitterSpeed.curve"));
        }

        private static bool TryGetLifetimeByEmitterSpeedModule(ParticleSystem system, out object module)
        {
            module = null;
            // Reflection keeps this contract tolerant of editors whose managed
            // ParticleSystem surface differs from the package baseline.
            var moduleType = typeof(ParticleSystem);
            var moduleProperty = moduleType.GetProperty("lifetimeByEmitterSpeed");
            if (moduleProperty == null) return false;
            module = moduleProperty.GetValue(system, null);
            var enabledProperty = module?.GetType().GetProperty("enabled");
            if (!(enabledProperty?.GetValue(module, null) is bool enabled) || !enabled)
            {
                module = null;
                return false;
            }
            return true;
        }

        internal static int EmitterMode(
            ParticleSystemShapeMultiModeValue mode,
            string field,
            ConversionDiagnostics diagnostics)
        {
            switch (mode)
            {
                case ParticleSystemShapeMultiModeValue.Random: return 0;
                case ParticleSystemShapeMultiModeValue.Loop: return 1;
                case ParticleSystemShapeMultiModeValue.PingPong: return 2;
                case ParticleSystemShapeMultiModeValue.BurstSpread: return 3;
                default:
                    diagnostics.unsupported.Add(field + ".unknown");
                    diagnostics.approximated.Add(field + ".unknown.randomFallback");
                    diagnostics.warnings.Add(field + " uses an unknown Unity Shape emission mode. Best-effort explicitly uses Random emission; strict export fails.");
                    return 0;
            }
        }
    }
}
