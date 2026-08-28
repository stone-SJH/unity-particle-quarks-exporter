using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static UnityParticleQuarksExporter.Editor.QuarksParticleSemanticsUtility;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class QuarksJsonExporter
    {
        private readonly GameObject root;
        private readonly string sourcePath;
        private readonly Dictionary<string, JsonObject> geometries = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonObject> materials = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonObject> textures = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonObject> images = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        private readonly SortedSet<string> textureFiles = new SortedSet<string>(StringComparer.Ordinal);
        private readonly QuarksMaterialConverter materialConverter;
        private readonly QuarksGeometryRendererConverter geometryRendererConverter;
        private readonly QuarksParticleMotionConverter particleMotionConverter;
        private readonly QuarksParticleEmissionShapeConverter particleEmissionShapeConverter;
        private readonly QuarksParticleRenderTrailConverter particleRenderTrailConverter;
        private readonly QuarksParticleDiagnostics particleDiagnostics;
        private readonly Dictionary<ParticleSystem, string> emitterIds = new Dictionary<ParticleSystem, string>();
        private readonly HashSet<ParticleSystem> subEmitterSystems = new HashSet<ParticleSystem>();

        public QuarksJsonExporter(
            GameObject prefabRoot,
            string prefabPath,
            string effectOutputDirectory,
            int textureLimit,
            bool isPresentationTarget,
            bool isSourceBuiltInPipeline)
        {
            root = prefabRoot;
            sourcePath = prefabPath;
            geometryRendererConverter = new QuarksGeometryRendererConverter(
                prefabRoot,
                prefabPath,
                geometries);
            particleMotionConverter = new QuarksParticleMotionConverter();
            particleEmissionShapeConverter = new QuarksParticleEmissionShapeConverter(
                geometryRendererConverter);
            particleRenderTrailConverter = new QuarksParticleRenderTrailConverter();
            particleDiagnostics = new QuarksParticleDiagnostics(isPresentationTarget);
            materialConverter = new QuarksMaterialConverter(
                prefabPath,
                effectOutputDirectory,
                Mathf.Clamp(textureLimit <= 0 ? 1024 : textureLimit, 16, 4096),
                isSourceBuiltInPipeline,
                materials,
                textures,
                images,
                textureFiles);
        }

        public QuarksExportResult Export(IReadOnlyList<ParticleSystem> systems)
        {
            foreach (var system in systems)
            {
                var path = GetPath(root.transform, system.transform);
                emitterIds[system] = UnityParticleQuarksStableId.Create(sourcePath, path, "particle-emitter");
            }

            foreach (var system in systems)
            {
                var subEmitters = system.subEmitters;
                if (!subEmitters.enabled) continue;
                for (var index = 0; index < subEmitters.subEmittersCount; index++)
                {
                    var subSystem = subEmitters.GetSubEmitterSystem(index);
                    if (subSystem != null && emitterIds.ContainsKey(subSystem)) subEmitterSystems.Add(subSystem);
                }
            }

            var children = Json.Array();
            var reports = new List<UnityParticleQuarksParticleSystemReport>();
            var hasUnsupported = false;
            var hasProfileGaps = false;
            var fatalUnsupported = new SortedSet<string>(StringComparer.Ordinal);
            var emitterCount = 0;
            var orderedSystems = systems.OrderBy(item => GetPath(root.transform, item.transform), StringComparer.Ordinal).ToArray();
            var omittedRenderers = root.GetComponentsInChildren<Renderer>(true)
                .Where(item => item.enabled && !(item is ParticleSystemRenderer))
                .Select(item => GetPath(root.transform, item.transform))
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            for (var systemIndex = 0; systemIndex < orderedSystems.Length; systemIndex++)
            {
                var system = orderedSystems[systemIndex];
                var diagnostics = new ConversionDiagnostics();
                if (systemIndex == 0 && omittedRenderers.Length > 0)
                {
                    diagnostics.unsupported.Add("renderer.nonParticleSystem");
                    diagnostics.approximated.Add("renderer.nonParticleSystem.omittedFallback");
                    diagnostics.warnings.Add("Enabled non-ParticleSystem renderers are outside the v0 exporter contract and were not silently included: " + string.Join(", ", omittedRenderers));
                }
                var emitter = BuildEmitter(system, diagnostics);
                if (emitter != null)
                {
                    children.Add(emitter);
                    emitterCount++;
                }
                var report = diagnostics.ToReport(GetPath(root.transform, system.transform));
                reports.Add(report);
                hasUnsupported |= report.unsupported.Length > 0;
                hasProfileGaps |= report.shaderProfileGaps != null && report.shaderProfileGaps.Length > 0;
                foreach (var item in diagnostics.fatalUnsupported) fatalUnsupported.Add(item);
            }

            var rootId = UnityParticleQuarksStableId.Create(sourcePath, root.name, "quarks-root");
            var rootObject = Json.Object()
                .Add("uuid", Json.String(rootId))
                .Add("type", Json.String("Group"))
                .Add("name", Json.String(root.name))
                .Add("layers", Json.Number(1))
                .Add("matrix", MatrixArray(Matrix4x4.identity))
                .Add("children", children);

            var document = Json.Object()
                .Add("metadata", Json.Object()
                    .Add("version", Json.Number(4.7))
                    .Add("type", Json.String("Object"))
                    .Add("generator", Json.String("UnityParticleQuarksExporter")))
                .Add("geometries", ValuesByKey(geometries))
                .Add("materials", ValuesByKey(materials))
                .Add("textures", ValuesByKey(textures))
                .Add("images", ValuesByKey(images))
                .Add("object", rootObject);

            return new QuarksExportResult
            {
                json = document + Environment.NewLine,
                textures = textureFiles.ToArray(),
                reports = reports.ToArray(),
                hasUnsupported = hasUnsupported,
                hasProfileGaps = hasProfileGaps,
                hasFatalUnsupported = fatalUnsupported.Count > 0,
                fatalUnsupported = fatalUnsupported.ToArray(),
                runtimeTier = reports.Any(item => item.runtimeTier == "paired") ? "paired" : "stock",
                emitterCount = emitterCount
            };
        }

        private JsonObject BuildEmitter(ParticleSystem system, ConversionDiagnostics diagnostics)
        {
            var path = GetPath(root.transform, system.transform);
            if (!Approximately(root.transform.position, Vector3.zero) ||
                Quaternion.Angle(root.transform.rotation, Quaternion.identity) > 0.0001f)
            {
                diagnostics.mapped.Add("prefabRoot.poseNormalized");
                diagnostics.warnings.Add("Prefab-root scene position and rotation are normalized; the outer runtime wrapper owns placement while root scale remains in the ParticleSystem scaling contract.");
            }
            var main = system.main;
            var renderer = system.GetComponent<ParticleSystemRenderer>();
            var renderMode = geometryRendererConverter.ResolveRenderMode(system, renderer, diagnostics);
            var headRenderMode = geometryRendererConverter.ResolveParticleHeadRenderMode(
                system,
                renderer,
                renderMode,
                diagnostics);
            var scaling = geometryRendererConverter.BuildScalingContext(system, diagnostics);
            var missingMaterial = geometryRendererConverter.ActiveRendererHasMissingMaterial(system, renderer);
            var invalidRendererMesh = false;
            string geometryId;
            if (renderMode == 2)
            {
                if (renderer == null || renderer.mesh == null)
                {
                    invalidRendererMesh = true;
                    geometryId = geometryRendererConverter.RegisterBillboardGeometry();
                    diagnostics.unsupported.Add("renderer.meshGeometry");
                    diagnostics.approximated.Add("renderer.meshGeometry.omittedFallback");
                }
                else
                {
                    geometryId = geometryRendererConverter.RegisterGeometry(
                        renderer.mesh,
                        scaling.rendererAxisSigns,
                        path + ":renderer-mesh",
                        diagnostics,
                        false);
                    if (string.IsNullOrEmpty(geometryId))
                    {
                        invalidRendererMesh = true;
                        geometryId = geometryRendererConverter.RegisterBillboardGeometry();
                        diagnostics.unsupported.Add("renderer.meshGeometry");
                        diagnostics.approximated.Add("renderer.meshGeometry.omittedFallback");
                    }
                    else
                    {
                        diagnostics.mapped.Add("renderer.mesh");
                    }
                }
            }
            else
            {
                geometryId = geometryRendererConverter.RegisterBillboardGeometry();
            }
            string headGeometryId = null;
            string headMaterialId = null;
            MaterialExportSemantics headMaterialSemantics = null;
            if (headRenderMode >= 0 && renderer != null)
            {
                if (headRenderMode == 2 && renderer.mesh != null)
                {
                    headGeometryId = geometryRendererConverter.RegisterGeometry(
                        renderer.mesh,
                        scaling.rendererAxisSigns,
                        path + ":particle-head-mesh",
                        diagnostics,
                        false);
                }
                else if (headRenderMode == 2)
                {
                    diagnostics.unsupported.Add("renderer.particleHeadMesh");
                    diagnostics.approximated.Add("renderer.particleHeadMesh.billboardFallback");
                    diagnostics.warnings.Add("The Unity particle head Mesh is missing; the companion head uses the exporter billboard geometry fallback.");
                }
                headGeometryId = string.IsNullOrEmpty(headGeometryId)
                    ? geometryRendererConverter.RegisterBillboardGeometry()
                    : headGeometryId;
                if (renderer.sharedMaterial == null)
                {
                    diagnostics.unsupported.Add("renderer.particleHeadMaterial");
                    diagnostics.approximated.Add("renderer.particleHeadMaterial.omittedFallback");
                    diagnostics.warnings.Add("The Unity particle head material is missing; the trail remains loadable but the companion head is omitted.");
                    headRenderMode = -1;
                }
                else
                {
                    headMaterialId = materialConverter.RegisterMaterial(
                        renderer,
                        path + ":particle-head",
                        false,
                        diagnostics,
                        out headMaterialSemantics);
                }
            }
            var rendererCannotRender = renderer == null || !renderer.enabled ||
                                       (renderer.renderMode == ParticleSystemRenderMode.None && !system.trails.enabled);
            var invalidVisibleRenderer = missingMaterial || invalidRendererMesh || rendererCannotRender;
            var preserveInvisibleControl = invalidVisibleRenderer &&
                                           (IsRequiredForSubEmitterSemantics(system) ||
                                            particleRenderTrailConverter.HasEffectivePointLights(system));
            if (missingMaterial)
            {
                if (rendererCannotRender && preserveInvisibleControl)
                {
                    diagnostics.inactive.Add("renderer.material.notRequiredForControlEmitter");
                    diagnostics.warnings.Add("The disabled/None renderer has no material. A transparent control emitter is retained because the ParticleSystem participates in runtime subemitter or Point Light semantics.");
                }
                else
                {
                    diagnostics.unsupported.Add(system.trails.enabled ? "renderer.trailMaterial" : "renderer.material");
                    diagnostics.approximated.Add(system.trails.enabled
                        ? "renderer.trailMaterial.omittedVisibleEmitterFallback"
                        : "renderer.material.omittedVisibleEmitterFallback");
                    diagnostics.warnings.Add(preserveInvisibleControl
                        ? "The active renderer has no material. A transparent control emitter is retained because the ParticleSystem participates in runtime subemitter or Point Light semantics."
                        : "The active renderer has no material. Best-effort omits this visible ParticleSystem instead of fabricating an opaque white material.");
                }
            }
            if (invalidRendererMesh)
            {
                diagnostics.warnings.Add(preserveInvisibleControl
                    ? "The active Mesh renderer has no readable assigned mesh. A transparent billboard control emitter is retained because the ParticleSystem participates in runtime subemitter or Point Light semantics."
                    : "The active Mesh renderer has no readable assigned mesh. Best-effort omits this visible ParticleSystem instead of fabricating billboard geometry.");
            }
            if (rendererCannotRender && renderer != null)
            {
                diagnostics.inactive.Add("renderer.invisible");
                diagnostics.warnings.Add(preserveInvisibleControl
                    ? "The ParticleSystem renderer is disabled or set to None. A transparent billboard control emitter is retained for runtime subemitter or Point Light semantics."
                    : "The ParticleSystem renderer is disabled or set to None, so best-effort omits the intentionally invisible system.");
            }
            var materialId = materialConverter.RegisterMaterial(
                preserveInvisibleControl ? null : renderer,
                renderMode == 3 ? path + ":trail" : path,
                renderMode == 3,
                diagnostics,
                out var materialSemantics);
            if (preserveInvisibleControl && rendererCannotRender)
            {
                // No source shader participates in an intentionally invisible
                // emitter. Keep raw particle color in the transparent control
                // material so runtime-only modules can still consume it.
                materialSemantics.consumesParticleColor = true;
                // Quarks' particle shader writes vColor directly and does not
                // carry MeshBasicMaterial.opacity into its batch shader. An
                // alpha-zero particle color is therefore required to keep a
                // renderer=None control emitter from becoming a white quad.
                materialSemantics.particleColor = new Color(1, 1, 1, 0);
                // Keep the multiplier opaque so Lights.alphaAffectsIntensity
                // can still recover the authored particle alpha for runtime
                // Point Lights; visual transparency is carried by particleColor.
                materialSemantics.particleColorMultiplier = Color.white;
                diagnostics.mapped.Add("renderer.controlEmitter.particleColorRuntime");
            }
            var shapeBake = particleEmissionShapeConverter.BuildShapeBakeContext(
                system,
                renderMode,
                diagnostics);
            // scalingMode=Shape is applied by the paired runtime with separate
            // position and direction bases. Baking it into a stock primitive
            // would incorrectly scale start speed and collapse non-uniform scale.
            var shape = particleEmissionShapeConverter.BuildShape(
                system,
                path,
                shapeBake,
                Vector3.one,
                diagnostics);
            var localMatrix = geometryRendererConverter.BuildEmitterMatrix(system, scaling.emitterScale);
            var velocityOverLifetime = particleMotionConverter.BuildVelocityOverLifetimeMetadata(
                system,
                scaling,
                localMatrix,
                diagnostics);
            var forceOverLifetime = particleMotionConverter.BuildForceOverLifetimeMetadata(
                system,
                scaling,
                localMatrix,
                diagnostics);
            var gravity = particleMotionConverter.BuildGravityMetadata(system, localMatrix, diagnostics);
            var limitVelocityOverLifetime = particleMotionConverter.BuildLimitVelocityOverLifetimeMetadata(
                system,
                diagnostics);
            var inheritVelocity = particleMotionConverter.BuildInheritVelocityMetadata(system, diagnostics);
            var noiseSemantics = particleMotionConverter.BuildNoiseMetadata(system, diagnostics);
            var lightsSemantics = particleRenderTrailConverter.BuildLightsMetadata(
                system,
                renderMode,
                scaling,
                materialSemantics.consumesParticleColor,
                materialSemantics.particleColorMultiplier,
                diagnostics);
            var shapeSemantics = particleEmissionShapeConverter.BuildShapeSemanticsMetadata(
                system,
                scaling,
                diagnostics);
            var startDelaySemantics = particleEmissionShapeConverter.BuildStartDelayMetadata(
                system,
                diagnostics);
            var lifetimeByEmitterSpeed = particleEmissionShapeConverter.BuildLifetimeByEmitterSpeedMetadata(
                system,
                diagnostics);
            var particleRenderMode = headRenderMode >= 0 ? headRenderMode : renderMode;
            var meshRotationBySpeed = particleRenderTrailConverter.BuildMeshRotationBySpeedMetadata(
                system,
                particleRenderMode,
                scaling.particleAxisSigns,
                diagnostics);
            var trailSemantics = particleRenderTrailConverter.BuildTrailSemanticsMetadata(
                system,
                renderMode,
                diagnostics);
            var startColorSemantics = materialSemantics.consumesParticleColor
                ? particleRenderTrailConverter.BuildStartColorMetadata(
                    main.startColor,
                    materialSemantics.particleColorMultiplier,
                    diagnostics)
                : null;
            var trailInheritParticleColor = particleRenderTrailConverter.BuildTrailInheritParticleColorMetadata(
                system,
                renderMode,
                materialSemantics.consumesParticleColor,
                diagnostics);
            var sizeOverLifetime = particleRenderTrailConverter.BuildSizeOverLifetimeMetadata(
                system,
                particleRenderMode,
                diagnostics);
            var customData = materialSemantics.shaderProfile == null
                ? null
                : materialSemantics.shaderProfile.BuildParticleCustomDataMetadata(system, diagnostics);
            var simulationSpeed = particleEmissionShapeConverter.BuildSimulationSpeedMetadata(
                system,
                diagnostics);
            var meshScalarRotation = particleRenderTrailConverter.BuildMeshScalarRotationMetadata(
                system,
                particleRenderMode,
                scaling.particleAxisSigns,
                diagnostics);
            var meshVelocityAlignment = geometryRendererConverter.BuildMeshVelocityAlignmentMetadata(
                system,
                renderer,
                particleRenderMode,
                diagnostics);
            var meshCameraAlignment = geometryRendererConverter.BuildMeshCameraAlignmentMetadata(
                system,
                renderer,
                particleRenderMode,
                diagnostics);
            var subEmitterInheritance = Json.Array();
            var behaviors = BuildBehaviors(
                system,
                renderMode,
                particleRenderMode,
                velocityOverLifetime != null,
                forceOverLifetime != null,
                gravity != null,
                noiseSemantics != null,
                sizeOverLifetime != null,
                materialSemantics.consumesParticleColor,
                trailInheritParticleColor != null,
                customData != null,
                scaling.particleAxisSigns,
                subEmitterInheritance,
                diagnostics);
            var textureSheet = system.textureSheetAnimation;
            var textureSheetAnimation = particleRenderTrailConverter.BuildTextureSheetAnimationMetadata(
                system,
                diagnostics);
            QuarksParticleDiagnostics.DiagnoseProjectColorSpace(diagnostics);

            particleEmissionShapeConverter.DiagnoseEmitterConfiguration(
                system,
                materialSemantics.consumesParticleColor,
                startDelaySemantics,
                diagnostics);
            var rendererSettings = particleRenderTrailConverter.BuildRendererEmitterSettings(
                system,
                renderer,
                renderMode,
                diagnostics);
            var headRendererSettings = headRenderMode == 1 && renderer != null
                ? particleRenderTrailConverter.BuildParticleHeadStretchedBillboardSettings(
                    system,
                    renderer,
                    diagnostics)
                : null;
            var startRotation = particleRenderTrailConverter.BuildStartRotation(
                main,
                particleRenderMode,
                scaling.particleAxisSigns,
                diagnostics);
            var startSize = particleRenderTrailConverter.BuildStartSize(
                system,
                renderMode,
                headRenderMode,
                diagnostics);
            var ps = Json.Object()
                .Add("version", Json.String("3.0"))
                .Add("autoDestroy", Json.Boolean(false))
                .Add("looping", Json.Boolean(main.loop))
                .Add("prewarm", Json.Boolean(main.prewarm && main.loop))
                .Add("duration", Json.Number(Mathf.Max(0.01f, main.duration)))
                .Add("shape", shape)
                .Add("startLife", particleMotionConverter.BuildStartLifetime(main, diagnostics))
                .Add("startSpeed", particleMotionConverter.BuildStartSpeed(main, diagnostics))
                .Add("startRotation", startRotation)
                .Add("startSize", startSize)
                .Add("startColor", materialSemantics.consumesParticleColor
                    ? particleRenderTrailConverter.BuildStartColorValue(
                        main.startColor,
                        materialSemantics.particleColorMultiplier,
                        diagnostics)
                    : ConstantColor(materialSemantics.particleColor))
                .Add("emissionOverTime", particleEmissionShapeConverter.BuildEmissionOverTime(system.emission, diagnostics))
                .Add("emissionOverDistance", particleEmissionShapeConverter.BuildEmissionOverDistance(system.emission, diagnostics))
                .Add("emissionBursts", particleEmissionShapeConverter.BuildBursts(
                    system.emission,
                    diagnostics))
                .Add("onlyUsedByOther", Json.Boolean(subEmitterSystems.Contains(system)))
                .Add("rendererEmitterSettings", rendererSettings)
                .Add("instancingGeometry", Json.String(geometryId))
                .Add("renderMode", Json.Number(renderMode))
                .Add("renderOrder", Json.Number(renderer == null ? 0 : renderer.sortingOrder))
                .Add("material", Json.String(materialId))
                .Add("layers", Json.Number(1))
                .Add("startTileIndex", Json.Object().Add("type", Json.String("ConstantValue")).Add("value", Json.Number(0)))
                .Add("uTileCount", Json.Number(textureSheet.enabled && textureSheet.mode == ParticleSystemAnimationMode.Grid ? Mathf.Max(1, textureSheet.numTilesX) : 1))
                .Add("vTileCount", Json.Number(textureSheet.enabled && textureSheet.mode == ParticleSystemAnimationMode.Grid ? Mathf.Max(1, textureSheet.numTilesY) : 1))
                .Add("blendTiles", Json.Boolean(false))
                .Add("softParticles", Json.Boolean(materialSemantics.softParticles))
                .Add("softFarFade", Json.Number(materialSemantics.softFarFade))
                .Add("softNearFade", Json.Number(materialSemantics.softNearFade))
                .Add("behaviors", behaviors)
                .Add("worldSpace", Json.Boolean(main.simulationSpace == ParticleSystemSimulationSpace.World));

            var exporterUserData = Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.user_data.v1"))
                .Add("subEmitterInheritance", subEmitterInheritance)
                .Add("particleCapacity", Json.Object()
                    .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.particle_capacity.v1"))
                    .Add("maxParticles", Json.Number(Mathf.Max(0, main.maxParticles))));
            var rendererAlignment = geometryRendererConverter.BuildRendererAlignmentMetadata(
                system,
                renderer,
                renderMode,
                diagnostics);
            if (rendererAlignment != null)
                exporterUserData.Add("rendererAlignment", rendererAlignment);
            var rendererPivot = geometryRendererConverter.BuildRendererPivotMetadata(renderer, diagnostics);
            if (rendererPivot != null)
                exporterUserData.Add("rendererPivot", rendererPivot);
            if (headRenderMode >= 0 && !string.IsNullOrEmpty(headGeometryId) && !string.IsNullOrEmpty(headMaterialId))
            {
                var alignment = geometryRendererConverter.ResolveRendererAlignment(renderer);
                var particleHeadMetadata = Json.Object()
                    .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.particle_head.v1"))
                    .Add("geometry", Json.String(headGeometryId))
                    .Add("material", Json.String(headMaterialId))
                    .Add("materialColor", ColorJson(headMaterialSemantics == null ? Color.white : headMaterialSemantics.sourceColor))
                    .Add("restoreMaterialColor", Json.Boolean(headMaterialSemantics != null && headMaterialSemantics.restoreMaterialColor))
                    .Add("materialProjectColorSpace", Json.String(QualitySettings.activeColorSpace == ColorSpace.Linear ? "linear" : "gamma"))
                    .Add("renderMode", Json.Number(headRenderMode))
                    .Add("renderOrder", Json.Number(renderer == null ? 0 : renderer.sortingOrder))
                    .Add("layers", Json.Number(1))
                    .Add("uTileCount", Json.Number(textureSheet.enabled && textureSheet.mode == ParticleSystemAnimationMode.Grid ? Mathf.Max(1, textureSheet.numTilesX) : 1))
                    .Add("vTileCount", Json.Number(textureSheet.enabled && textureSheet.mode == ParticleSystemAnimationMode.Grid ? Mathf.Max(1, textureSheet.numTilesY) : 1))
                    .Add("blendTiles", Json.Boolean(false))
                    .Add("softParticles", Json.Boolean(headMaterialSemantics != null && headMaterialSemantics.softParticles))
                    .Add("softFarFade", Json.Number(headMaterialSemantics == null ? 1f : headMaterialSemantics.softFarFade))
                    .Add("softNearFade", Json.Number(headMaterialSemantics == null ? 0f : headMaterialSemantics.softNearFade))
                    .Add("worldSpace", Json.Boolean(main.simulationSpace == ParticleSystemSimulationSpace.World))
                    .Add("rotation", Json.Object()
                        .Add("alignment", Json.String(alignment))
                        .Add("preserveAuthored", Json.Boolean(true)));
                if (headRendererSettings != null)
                {
                    particleHeadMetadata.Add("rendererEmitterSettings", headRendererSettings);
                }
                exporterUserData.Add("particleHead", particleHeadMetadata);
                diagnostics.mapped.Add("trails.particleHeadRenderer.metadata.v1");
                diagnostics.mapped.Add("trails.particleHeadRenderer.material.independent");
                diagnostics.mapped.Add("trails.particleHeadRenderer.renderOrder.independent");
                diagnostics.mapped.Add("trails.particleHeadRenderer.worldSpace.independent");
                diagnostics.requiresPairedRuntime = true;
            }
            diagnostics.requiresPairedRuntime = true;
            if (!string.IsNullOrEmpty(materialSemantics.shaderProfileId))
            {
                exporterUserData.Add("materialProfile", Json.Object()
                    .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.material.profile.v1"))
                    .Add("profileId", Json.String(materialSemantics.shaderProfileId))
                    .Add("profileVersion", Json.String(materialSemantics.shaderProfileVersion))
                    .Add("sourceShader", Json.String(materialSemantics.sourceShaderName))
                    .Add("runtimeTier", Json.String(materialSemantics.shaderRuntimeTier))
                    .Add("fidelity", Json.String(materialSemantics.shaderFidelity))
                    .Add("resolvedProperties", StringArray(materialSemantics.resolvedProperties))
                    .Add("missingProperties", StringArray(materialSemantics.missingProperties))
                    .Add("unmappedProperties", StringArray(materialSemantics.unmappedProperties))
                    .Add("conflicts", StringArray(materialSemantics.profileConflicts))
                    .Add("alphaClip", Json.Boolean(materialSemantics.alphaTest > 0))
                    .Add("doubleSided", Json.Boolean(materialSemantics.doubleSided))
                     .Add("softParticles", Json.Boolean(materialSemantics.softParticles)));
            }
            if (materialSemantics.alphaMetadata != null)
                exporterUserData.Add("materialAlpha", materialSemantics.alphaMetadata);
            if (materialSemantics.blendMetadata != null)
                exporterUserData.Add("materialBlend", materialSemantics.blendMetadata);
            if (materialSemantics.textureUvMetadata != null)
                exporterUserData.Add("materialTextureUv", materialSemantics.textureUvMetadata);
            if (materialSemantics.shaderParameters != null)
                exporterUserData.Add("materialShaderParameters", materialSemantics.shaderParameters);
            if (customData != null)
                exporterUserData.Add("customData", customData);
            if (materialSemantics.restoreMaterialColor)
            {
                if (QualitySettings.activeColorSpace == ColorSpace.Gamma)
                {
                    exporterUserData.Add("colorSemantics", Json.Object()
                        .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.gamma_color.v1"))
                        .Add("materialColor", ColorJson(materialSemantics.sourceColor)));
                }
                else
                {
                    exporterUserData.Add("colorSemantics", Json.Object()
                        .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.color.v2"))
                        .Add("sourceProjectColorSpace", Json.String("linear"))
                        .Add("outputColorSpace", Json.String("srgb"))
                        .Add("materialColor", ColorJson(materialSemantics.sourceColor)));
                }
            }
            if (!string.Equals(materialSemantics.fragmentColorMode, "stock", StringComparison.Ordinal) ||
                materialSemantics.cameraFade || !string.IsNullOrEmpty(materialSemantics.shaderProfileMetadataKey) ||
                materialSemantics.alphaMetadata != null || materialSemantics.blendMetadata != null)
            {
                var materialMetadata = Json.Object()
                    .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.material.v1"))
                    .Add("fragmentColorMode", Json.String(materialSemantics.fragmentColorMode));
                if (!string.IsNullOrEmpty(materialSemantics.shaderProfileMetadataKey))
                {
                    materialMetadata.Add("profileMetadataKey", Json.String(materialSemantics.shaderProfileMetadataKey))
                        .Add("profileId", Json.String(materialSemantics.shaderProfileId));
                }
                if (materialSemantics.cameraFade)
                {
                    materialMetadata.Add("cameraFade", Json.Object()
                        .Add("near", Json.Number(materialSemantics.cameraFadeNear))
                        .Add("far", Json.Number(materialSemantics.cameraFadeFar))
                        .Add("smoothness", Json.Number(materialSemantics.cameraFadeSmoothness)));
                }
                if (!string.Equals(materialSemantics.baseColorChannel, "rgb", StringComparison.Ordinal))
                    materialMetadata.Add("baseColorChannel", Json.String(materialSemantics.baseColorChannel));
                exporterUserData.Add("materialSemantics", materialMetadata);
            }
            if (velocityOverLifetime != null)
            {
                exporterUserData.Add("velocityOverLifetime", velocityOverLifetime);
            }
            if (forceOverLifetime != null)
            {
                exporterUserData.Add("forceOverLifetime", forceOverLifetime);
            }
            if (gravity != null)
            {
                exporterUserData.Add("gravity", gravity);
            }
            if (limitVelocityOverLifetime != null)
            {
                exporterUserData.Add("limitVelocityOverLifetime", limitVelocityOverLifetime);
            }
            if (inheritVelocity != null)
            {
                exporterUserData.Add("inheritVelocity", inheritVelocity);
            }
            if (noiseSemantics != null)
            {
                exporterUserData.Add("noise", noiseSemantics);
            }
            if (lightsSemantics != null)
            {
                exporterUserData.Add("lights", lightsSemantics);
            }
            if (shapeSemantics != null)
            {
                exporterUserData.Add("shapeSemantics", shapeSemantics);
            }
            if (startDelaySemantics != null) exporterUserData.Add("startDelay", startDelaySemantics);
            if (lifetimeByEmitterSpeed != null) exporterUserData.Add("lifetimeByEmitterSpeed", lifetimeByEmitterSpeed);
            if (meshRotationBySpeed != null) exporterUserData.Add("meshRotationBySpeed", meshRotationBySpeed);
            if (trailSemantics != null) exporterUserData.Add("trailSemantics", trailSemantics);
            if (startColorSemantics != null)
            {
                exporterUserData.Add("startColorSemantics", startColorSemantics);
            }
            if (trailInheritParticleColor != null)
            {
                exporterUserData.Add("trailInheritParticleColor", trailInheritParticleColor);
            }
            if (sizeOverLifetime != null)
            {
                exporterUserData.Add("sizeOverLifetime", sizeOverLifetime);
            }
            if (simulationSpeed != null)
            {
                exporterUserData.Add("simulationSpeed", simulationSpeed);
            }
            if (meshScalarRotation != null)
            {
                exporterUserData.Add("meshScalarRotation", meshScalarRotation);
            }
            if (meshVelocityAlignment != null)
            {
                exporterUserData.Add("meshVelocityAlignment", meshVelocityAlignment);
            }
            if (meshCameraAlignment != null)
            {
                exporterUserData.Add("meshCameraAlignment", meshCameraAlignment);
            }
            if (textureSheetAnimation != null)
            {
                exporterUserData.Add("textureSheetAnimation", textureSheetAnimation);
            }

            var emitter = Json.Object()
                .Add("uuid", Json.String(emitterIds[system]))
                .Add("type", Json.String("ParticleEmitter"))
                .Add("name", Json.String(system.name))
                .Add("layers", Json.Number(1))
                .Add("matrix", MatrixArray(localMatrix))
                .Add("userData", Json.Object()
                    .Add("unityParticleQuarks", exporterUserData)
                    .Add("unityParticleQuarks", exporterUserData))
                .Add("ps", ps);
            return invalidVisibleRenderer && !preserveInvisibleControl ? null : emitter;
        }

        private JsonArray BuildBehaviors(
            ParticleSystem system,
            int renderMode,
            int particleRenderMode,
            bool linearVelocityMapped,
            bool forceOverLifetimeMapped,
            bool gravityMapped,
            bool exactNoiseMapped,
            bool exactSizeOverLifetimeMapped,
            bool materialConsumesParticleColor,
            bool trailInheritParticleColorMapped,
            bool customDataMapped,
            Vector3 particleAxisSigns,
            JsonArray subEmitterInheritance,
            ConversionDiagnostics diagnostics)
        {
            var result = Json.Array();
            particleRenderTrailConverter.AddAppearanceBehaviors(
                result,
                system,
                renderMode,
                particleRenderMode,
                exactSizeOverLifetimeMapped,
                materialConsumesParticleColor,
                trailInheritParticleColorMapped,
                particleAxisSigns,
                diagnostics);
            particleMotionConverter.AddBehaviors(
                result,
                system,
                forceOverLifetimeMapped,
                gravityMapped,
                exactNoiseMapped,
                diagnostics);
            particleRenderTrailConverter.AddTextureSheetBehavior(result, system, diagnostics);
            AddSubEmitters(system, result, subEmitterInheritance, diagnostics);
            particleDiagnostics.DiagnoseUnsupportedModules(
                system,
                linearVelocityMapped,
                customDataMapped,
                diagnostics);
            return result;
        }

        private void AddSubEmitters(
            ParticleSystem system,
            JsonArray behaviors,
            JsonArray inheritanceMetadata,
            ConversionDiagnostics diagnostics)
        {
            var module = system.subEmitters;
            if (!module.enabled) return;
            for (var index = 0; index < module.subEmittersCount; index++)
            {
                var subSystem = module.GetSubEmitterSystem(index);
                var type = module.GetSubEmitterType(index);
                if (subSystem == null || !emitterIds.TryGetValue(subSystem, out var id))
                {
                    diagnostics.unsupported.Add("subEmitters.missingOrExcluded");
                    diagnostics.approximated.Add("subEmitters.missingOrExcluded.omittedFallback");
                    diagnostics.warnings.Add("A configured subemitter is missing or excluded from this effect. Best-effort explicitly omits that trigger; strict export fails.");
                    continue;
                }
                if (type == ParticleSystemSubEmitterType.Collision || type == ParticleSystemSubEmitterType.Trigger)
                {
                    particleDiagnostics.ReportPhysicsCollisionFeature(
                        "subEmitters." + type,
                        diagnostics);
                    continue;
                }
                if (type != ParticleSystemSubEmitterType.Birth && type != ParticleSystemSubEmitterType.Death)
                {
                    diagnostics.unsupported.Add("subEmitters." + type);
                    diagnostics.approximated.Add("subEmitters." + type + ".omittedFallback");
                    diagnostics.warnings.Add("The " + type + " subemitter trigger has no mapped runtime event. Best-effort explicitly omits it; strict export fails.");
                    continue;
                }
                var properties = module.GetSubEmitterProperties(index);
                var emitProbability = module.GetSubEmitterEmitProbability(index);
                var mode = type == ParticleSystemSubEmitterType.Death ? 0 : 1;
                behaviors.Add(Json.Object()
                    .Add("type", Json.String("EmitSubParticleSystem"))
                    .Add("subParticleSystem", Json.String(id))
                    .Add("useVelocityAsBasis", Json.Boolean(false))
                    .Add("mode", Json.Number(mode))
                    .Add("emitProbability", Json.Number(emitProbability)));
                inheritanceMetadata.Add(Json.Object()
                    .Add("index", Json.Number(index))
                    .Add("subParticleSystem", Json.String(id))
                    .Add("mode", Json.Number(mode))
                    .Add("inheritColor", Json.Boolean((properties & ParticleSystemSubEmitterProperties.InheritColor) != 0))
                    .Add("inheritSize", Json.Boolean((properties & ParticleSystemSubEmitterProperties.InheritSize) != 0))
                    .Add("inheritRotation", Json.Boolean((properties & ParticleSystemSubEmitterProperties.InheritRotation) != 0))
                    .Add("inheritLifetime", Json.Boolean((properties & ParticleSystemSubEmitterProperties.InheritLifetime) != 0))
                    .Add("inheritDuration", Json.Boolean((properties & ParticleSystemSubEmitterProperties.InheritDuration) != 0)));
                diagnostics.mapped.Add("subEmitters." + type);
                diagnostics.mapped.Add("subEmitters." + type + ".emitProbability");
                diagnostics.mapped.Add("subEmitters." + type + ".triggerTransform");
                if (properties == ParticleSystemSubEmitterProperties.InheritNothing)
                {
                    diagnostics.mapped.Add("subEmitters." + type + ".inheritNothing");
                }
                else
                {
                    DiagnoseSubEmitterProperties(type, properties, diagnostics);
                }
                diagnostics.warnings.Add("The SDK VFX runtime applies Unity subemitter trigger transforms and supported inheritance metadata; stock Quarks playback without that compatibility layer is not equivalent.");
            }
        }

        private static void DiagnoseSubEmitterProperties(
            ParticleSystemSubEmitterType type,
            ParticleSystemSubEmitterProperties properties,
            ConversionDiagnostics diagnostics)
        {
            var prefix = "subEmitters." + type + ".";
            var known = ParticleSystemSubEmitterProperties.InheritColor |
                        ParticleSystemSubEmitterProperties.InheritSize |
                        ParticleSystemSubEmitterProperties.InheritRotation |
                        ParticleSystemSubEmitterProperties.InheritLifetime |
                        ParticleSystemSubEmitterProperties.InheritDuration;

            if ((properties & ParticleSystemSubEmitterProperties.InheritColor) != 0)
                diagnostics.mapped.Add(prefix + "inheritColor");
            if ((properties & ParticleSystemSubEmitterProperties.InheritSize) != 0)
                diagnostics.mapped.Add(prefix + "inheritSize");
            if ((properties & ParticleSystemSubEmitterProperties.InheritRotation) != 0)
                diagnostics.mapped.Add(prefix + "inheritRotation");
            if ((properties & ParticleSystemSubEmitterProperties.InheritLifetime) != 0)
                diagnostics.mapped.Add(prefix + "inheritLifetime");
            if ((properties & ParticleSystemSubEmitterProperties.InheritDuration) != 0)
            {
                diagnostics.mapped.Add(prefix + "inheritDuration.runtime");
                diagnostics.approximated.Add(prefix + "inheritDuration.stockChildDurationFallback");
            }
            if ((((int)properties) & ~((int)known)) != 0)
            {
                diagnostics.unsupported.Add(prefix + "inheritUnknown");
                diagnostics.approximated.Add(prefix + "inheritUnknown.omittedFallback");
            }

            if ((properties & ParticleSystemSubEmitterProperties.InheritDuration) != 0 ||
                ((((int)properties) & ~((int)known)) != 0))
            {
                diagnostics.warnings.Add("The paired SDK temporarily applies the parent remaining lifetime to the child emitter during Birth/Death emission. Unknown inheritance flags remain explicit and unsupported.");
            }
        }


        private bool IsRequiredForSubEmitterSemantics(ParticleSystem system)
        {
            if (subEmitterSystems.Contains(system)) return true;
            var module = system.subEmitters;
            return module.enabled && module.subEmittersCount > 0;
        }

        private static JsonArray StringArray(string[] values)
        {
            var result = Json.Array();
            foreach (var value in values ?? Array.Empty<string>())
                result.Add(Json.String(value));
            return result;
        }

        private static JsonArray ValuesByKey(Dictionary<string, JsonObject> values)
        {
            var array = Json.Array();
            foreach (var pair in values.OrderBy(item => item.Key, StringComparer.Ordinal)) array.Add(pair.Value);
            return array;
        }

        internal static string GetPath(Transform rootTransform, Transform target)
        {
            var names = new List<string>();
            var current = target;
            while (current != null)
            {
                names.Add(current.name);
                if (current == rootTransform) break;
                current = current.parent;
            }
            names.Reverse();
            return string.Join("/", names);
        }
    }
}
