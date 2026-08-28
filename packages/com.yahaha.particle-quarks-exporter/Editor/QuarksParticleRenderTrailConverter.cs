using System;
using UnityEngine;
using static UnityParticleQuarksExporter.Editor.QuarksCoordinateUtility;
using static UnityParticleQuarksExporter.Editor.QuarksParticleSemanticsUtility;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class QuarksParticleRenderTrailConverter
    {
        internal JsonValue BuildStartColorValue(
            ParticleSystem.MinMaxGradient color,
            Color particleColorMultiplier,
            ConversionDiagnostics diagnostics)
        {
            return Gradient(color, diagnostics, "main.startColor", particleColorMultiplier);
        }

        internal JsonObject BuildRendererEmitterSettings(
            ParticleSystem system,
            ParticleSystemRenderer renderer,
            int renderMode,
            ConversionDiagnostics diagnostics)
        {
            var settings = Json.Object();
            if (renderMode == 1 && renderer != null)
            {
                var speedFactor = renderer.velocityScale;
                if (Mathf.Abs(renderer.velocityScale) > 0.000001f)
                {
                    var referenceSize = MaximumRenderedParticleSize(system);
                    if (referenceSize > 0.000001f)
                    {
                        speedFactor = renderer.velocityScale / referenceSize;
                        diagnostics.approximated.Add("renderer.stretchedBillboard");
                        diagnostics.approximated.Add("renderer.stretchedBillboard.velocityScale");
                        diagnostics.warnings.Add("Unity velocity stretch is size-independent, while stock Quarks multiplies velocity stretch by current particle size; speedFactor is normalized at the maximum expected particle size.");
                    }
                    else
                    {
                        diagnostics.unsupported.Add("renderer.stretchedBillboard.velocityScale");
                        diagnostics.approximated.Add("renderer.stretchedBillboard.velocityScale.unscaledStockFallback");
                        diagnostics.warnings.Add("Stretched Billboard velocityScale is active but no positive particle-size reference could be derived. Best-effort keeps the unnormalized stock Quarks speed factor; strict export fails.");
                    }
                }
                else
                {
                    diagnostics.mapped.Add("renderer.stretchedBillboard.velocityScale");
                }

                if (Mathf.Abs(renderer.cameraVelocityScale) > 0.000001f)
                {
                    diagnostics.unsupported.Add("renderer.stretchedBillboard.cameraVelocityScale");
                    diagnostics.approximated.Add("renderer.stretchedBillboard.cameraVelocityScale.omittedFallback");
                    diagnostics.warnings.Add("Stretched Billboard cameraVelocityScale has no stock Quarks equivalent. Best-effort explicitly omits camera-velocity stretch; strict export fails.");
                }
                else
                {
                    diagnostics.inactive.Add("renderer.stretchedBillboard.cameraVelocityScale");
                }

                settings
                    .Add("speedFactor", Json.Number(speedFactor))
                    .Add("lengthFactor", Json.Number(renderer.lengthScale));
                diagnostics.mapped.Add("renderer.stretchedBillboard.lengthScale");
                if (Mathf.Abs(renderer.velocityScale) <= 0.000001f)
                {
                    diagnostics.mapped.Add("renderer.stretchedBillboard");
                }
            }
            else if (renderMode == 3)
            {
                settings
                    .Add("startLength", ScaleCurve(
                        system.trails.lifetime,
                        60f,
                        diagnostics,
                        "trails.lifetime"))
                    .Add("followLocalOrigin", Json.Boolean(false));
                diagnostics.approximated.Add("renderer.trail");
                diagnostics.approximated.Add("trails.lifetime.frameSamples");
                diagnostics.warnings.Add("Stock Quarks stores trail length as update-history samples rather than seconds; Unity trail lifetime is approximated at 60 samples per second.");
            }
            return settings;
        }

        internal JsonValue BuildStartRotation(
            ParticleSystem.MainModule main,
            int particleRenderMode,
            Vector3 particleAxisSigns,
            ConversionDiagnostics diagnostics)
        {
            if (main.startRotation3D)
            {
                return Euler(
                    main.startRotationX,
                    main.startRotationY,
                    main.startRotationZ,
                    AngularAxisSigns(particleAxisSigns),
                    diagnostics,
                    "main.startRotation",
                    "ZXY");
            }

            return particleRenderMode == 2
                ? ScalarMeshEuler(
                    main.startRotation,
                    ScalarAngleSign(particleAxisSigns),
                    diagnostics,
                    "main.startRotation")
                : Curve(main.startRotation, diagnostics, "main.startRotation");
        }

        internal JsonValue BuildStartSize(
            ParticleSystem system,
            int renderMode,
            int headRenderMode,
            ConversionDiagnostics diagnostics)
        {
            var main = system.main;
            var trailWidthIsIndependent =
                renderMode == 3 && !system.trails.sizeAffectsWidth && headRenderMode < 0;
            var startSize = trailWidthIsIndependent
                ? TrailHeadWidth(
                    system.trails.widthOverTrail,
                    diagnostics,
                    "trails.widthOverTrail")
                : main.startSize3D
                    ? VectorFunction(
                        main.startSizeX,
                        main.startSizeY,
                        main.startSizeZ,
                        diagnostics,
                        "main.startSize")
                    : Curve(main.startSize, diagnostics, "main.startSize");
            if (trailWidthIsIndependent)
            {
                diagnostics.approximated.Add("trails.headWidthSample");
                diagnostics.warnings.Add("Stock Quarks appends a trail point after WidthOverLength runs; the new point is initialized from widthOverTrail at ribbon time zero so ParticleSystem size cannot create a wide triangular head.");
            }
            return startSize;
        }

        internal JsonObject BuildLightsMetadata(
            ParticleSystem system,
            int renderMode,
            ScalingContext scaling,
            bool materialConsumesParticleColor,
            Color particleColorMultiplier,
            ConversionDiagnostics diagnostics)
        {
            var lights = system.lights;
            if (!lights.enabled) return null;
            if (lights.light == null)
            {
                diagnostics.inactive.Add("lights.missingLightPrefab");
                return null;
            }
            if (lights.ratio <= 0.000001f)
            {
                diagnostics.inactive.Add("lights.zeroRatio");
                return null;
            }
            if (lights.maxLights <= 0)
            {
                diagnostics.inactive.Add("lights.zeroMaxLights");
                return null;
            }

            var light = lights.light;
            if (light.intensity <= 0.000001f)
            {
                diagnostics.inactive.Add("lights.zeroBaseIntensity");
                return null;
            }
            if (light.range <= 0.000001f)
            {
                diagnostics.inactive.Add("lights.zeroBaseRange");
                return null;
            }
            if (light.cullingMask == 0)
            {
                diagnostics.inactive.Add("lights.zeroCullingMask");
                return null;
            }
            if (light.lightmapBakeType == LightmapBakeType.Baked)
            {
                diagnostics.inactive.Add("lights.bakedOnlyPrefab");
                return null;
            }
            if (light.type != LightType.Point)
            {
                var type = light.type.ToString();
                diagnostics.unsupported.Add("lights.lightType." + type);
                diagnostics.approximated.Add("lights.lightType." + type + ".omittedFallback");
                diagnostics.warnings.Add("ParticleSystem Lights currently maps Point Light prefabs only. Best-effort omits the unsupported " + type + " light instances; strict export fails.");
                return null;
            }

            if (light.cookie != null)
            {
                diagnostics.unsupported.Add("lights.cookie");
                diagnostics.approximated.Add("lights.cookie.omittedPointLightFallback");
                diagnostics.warnings.Add("The assigned Point Light uses a cookie. The paired Three.js runtime preserves the light without its cookie; strict export fails.");
            }
            if (light.useColorTemperature)
            {
                diagnostics.unsupported.Add("lights.colorTemperature");
                diagnostics.approximated.Add("lights.colorTemperature.baseColorFallback");
                diagnostics.warnings.Add("The assigned Point Light uses color temperature. The paired runtime preserves its base RGB color without black-body temperature composition; strict export fails.");
            }

            if ((lights.useParticleColor || lights.alphaAffectsIntensity) && !materialConsumesParticleColor)
            {
                diagnostics.unsupported.Add("lights.particleColor.sourceShaderDoesNotConsume");
                diagnostics.approximated.Add("lights.particleColor.renderedColorFallback");
                diagnostics.warnings.Add("The source shader does not consume ParticleSystem color, but the Lights module does. The paired runtime cannot recover the independent Unity particle-color stream from the renderer-neutralized Quarks color; strict export fails.");
            }
            if (lights.useParticleColor &&
                (Mathf.Abs(particleColorMultiplier.r) <= 0.000001f ||
                 Mathf.Abs(particleColorMultiplier.g) <= 0.000001f ||
                 Mathf.Abs(particleColorMultiplier.b) <= 0.000001f))
            {
                diagnostics.unsupported.Add("lights.particleColor.zeroMaterialMultiplier");
                diagnostics.approximated.Add("lights.particleColor.zeroChannelFallback");
                diagnostics.warnings.Add("At least one material color-multiplier channel is zero, so the paired runtime cannot invert the renderer tint to recover Unity particle RGB for the Light; strict export fails.");
            }
            if (lights.alphaAffectsIntensity && Mathf.Abs(particleColorMultiplier.a) <= 0.000001f)
            {
                diagnostics.unsupported.Add("lights.particleAlpha.zeroMaterialMultiplier");
                diagnostics.approximated.Add("lights.particleAlpha.zeroChannelFallback");
                diagnostics.warnings.Add("The material alpha multiplier is zero, so the paired runtime cannot invert the renderer tint to recover Unity particle alpha for Light intensity; strict export fails.");
            }

            var shadowMode = light.shadows == LightShadows.None
                ? "none"
                : light.shadows == LightShadows.Hard
                    ? "hard"
                    : "soft";
            if (light.shadows != LightShadows.None)
            {
                diagnostics.approximated.Add("lights.shadows.threePointShadowFallback");
                diagnostics.warnings.Add("Point Light shadow enablement is preserved, but Three.js shadow filtering and bias are renderer settings rather than Unity Light shadow settings.");
            }

            var rangeUsesRandomBlend = lights.range.mode == ParticleSystemCurveMode.TwoConstants ||
                                       lights.range.mode == ParticleSystemCurveMode.TwoCurves;
            var intensityUsesRandomBlend = lights.intensity.mode == ParticleSystemCurveMode.TwoConstants ||
                                           lights.intensity.mode == ParticleSystemCurveMode.TwoCurves;
            if (rangeUsesRandomBlend || intensityUsesRandomBlend)
            {
                diagnostics.approximated.Add("lights.randomCurveSeed.syntheticParticleFallback");
                diagnostics.warnings.Add("Unity derives Lights range/intensity random blends from each particle's internal randomSeed. Quarks does not expose that seed, so the paired runtime preserves a stable per-particle blend from its deterministic adapter stream instead of claiming seed-identical samples.");
            }

            var main = system.main;
            var renderScaleMode = main.scalingMode == ParticleSystemScalingMode.Hierarchy
                ? "hierarchy"
                : main.scalingMode == ParticleSystemScalingMode.Local
                    ? "local"
                    : "shape";
            var sourceRenderScale = main.scalingMode == ParticleSystemScalingMode.Shape
                ? Vector3.one
                : scaling.emitterScale;
            var sizeOverLifetime = system.sizeOverLifetime;
            var sizeBySpeed = system.sizeBySpeed;
            var uses3DSize = main.startSize3D ||
                             (sizeOverLifetime.enabled && sizeOverLifetime.separateAxes) ||
                             (sizeBySpeed.enabled && sizeBySpeed.separateAxes);

            diagnostics.mapped.UnionWith(new[]
            {
                "lights.point.runtime",
                "lights.ratio.runtime",
                "lights.randomDistribution.runtime",
                "lights.range.runtime",
                "lights.intensity.runtime",
                "lights.color.runtime",
                "lights.sizeAffectsRange.runtime",
                "lights.alphaAffectsIntensity.runtime",
                "lights.maxLights.runtime",
                "lights.position.runtime"
            });
            diagnostics.approximated.Add("lights.stockOmittedFallback");
            diagnostics.approximated.Add("lights.attenuation.threePointLightFallback");
            diagnostics.warnings.Add("The paired SDK creates per-particle Three.js PointLight instances after Quarks simulation. Stock Quarks playback omits them, and Three.js distance attenuation is not pixel-equivalent to Unity's active render pipeline.");

            return Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.lights.v1"))
                .Add("randomSeed", Json.Number(system.randomSeed))
                .Add("ratio", Json.Number(Mathf.Clamp01(lights.ratio)))
                .Add("randomDistribution", Json.Boolean(lights.useRandomDistribution))
                .Add("useParticleColor", Json.Boolean(lights.useParticleColor))
                .Add("sizeAffectsRange", Json.Boolean(lights.sizeAffectsRange))
                .Add("alphaAffectsIntensity", Json.Boolean(lights.alphaAffectsIntensity))
                .Add("maxLights", Json.Number(Mathf.Max(0, lights.maxLights)))
                .Add("uses3DSize", Json.Boolean(uses3DSize))
                .Add("meshSize", Json.Boolean(renderMode == 2))
                .Add("renderScaleMode", Json.String(renderScaleMode))
                .Add("sourceRenderScale", Json.Object()
                    .Add("x", Json.Number(sourceRenderScale.x))
                    .Add("y", Json.Number(sourceRenderScale.y))
                    .Add("z", Json.Number(sourceRenderScale.z)))
                .Add("particleColorMultiplier", ColorJson(particleColorMultiplier))
                .Add("range", VelocityCurveMetadata(lights.range, diagnostics, "lights.range"))
                .Add("intensity", VelocityCurveMetadata(lights.intensity, diagnostics, "lights.intensity"))
                .Add("light", Json.Object()
                    .Add("type", Json.String("point"))
                    .Add("color", ColorJson(light.color))
                    .Add("intensity", Json.Number(Mathf.Max(0, light.intensity)))
                    .Add("range", Json.Number(Mathf.Max(0, light.range)))
                    .Add("cullingMask", Json.Number(light.cullingMask))
                    .Add("shadowMode", Json.String(shadowMode)));
        }

        internal bool HasEffectivePointLights(ParticleSystem system)
        {
            var lights = system.lights;
            var light = lights.enabled ? lights.light : null;
            return light != null &&
                   light.type == LightType.Point &&
                   lights.ratio > 0.000001f &&
                   lights.maxLights > 0 &&
                   light.intensity > 0.000001f &&
                   light.range > 0.000001f &&
                   light.cullingMask != 0 &&
                   light.lightmapBakeType != LightmapBakeType.Baked;
        }

        internal JsonObject BuildSizeOverLifetimeMetadata(
            ParticleSystem system,
            int renderMode,
            ConversionDiagnostics diagnostics)
        {
            var size = system.sizeOverLifetime;
            if (!size.enabled || (renderMode == 3 && !system.trails.sizeAffectsWidth)) return null;
            var hasTwoCurves = size.separateAxes
                ? size.x.mode == ParticleSystemCurveMode.TwoCurves ||
                  size.y.mode == ParticleSystemCurveMode.TwoCurves ||
                  size.z.mode == ParticleSystemCurveMode.TwoCurves
                : size.size.mode == ParticleSystemCurveMode.TwoCurves;
            if (!hasTwoCurves) return null;

            diagnostics.mapped.Add("sizeOverLifetime.twoCurvesRuntime");
            diagnostics.approximated.Add("sizeOverLifetime.twoCurves.stockMeanFallback");
            diagnostics.warnings.Add("Size over Lifetime TwoCurves keeps a stable per-particle blend in exporter metadata for the paired SDK runtime. Stock Quarks playback uses the arithmetic-mean curve fallback and is not variation-equivalent.");
            var metadata = Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.size_over_lifetime.v1"))
                .Add("separateAxes", Json.Boolean(size.separateAxes));
            if (size.separateAxes)
            {
                metadata
                    .Add("x", VelocityCurveMetadata(size.x, diagnostics, "sizeOverLifetime.x"))
                    .Add("y", VelocityCurveMetadata(size.y, diagnostics, "sizeOverLifetime.y"))
                    .Add("z", VelocityCurveMetadata(size.z, diagnostics, "sizeOverLifetime.z"));
            }
            else
            {
                metadata.Add("size", VelocityCurveMetadata(size.size, diagnostics, "sizeOverLifetime.size"));
            }
            return metadata;
        }

        internal JsonObject BuildStartColorMetadata(
            ParticleSystem.MinMaxGradient color,
            Color multiplier,
            ConversionDiagnostics diagnostics)
        {
            switch (color.mode)
            {
                case ParticleSystemGradientMode.Gradient:
                    diagnostics.mapped.Add("main.startColor.gradient.normalizedEmitterTimeRuntime");
                    diagnostics.approximated.Add("main.startColor.gradient.stockAbsoluteTimeFallback");
                    diagnostics.warnings.Add("Unity Start Color Gradient evaluates normalized emitter duration. The paired SDK normalizes Quarks' absolute emission time; stock Quarks playback remains an explicit absolute-time fallback.");
                    return Json.Object()
                        .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.start_color.v1"))
                        .Add("mode", Json.String("gradient"));
                case ParticleSystemGradientMode.TwoGradients:
                    diagnostics.mapped.Add("main.startColor.twoGradients.normalizedEmitterTimeRuntime");
                    diagnostics.approximated.Add("main.startColor.twoGradients.stockAbsoluteTimeFallback");
                    diagnostics.warnings.Add("Unity Start Color Two Gradients evaluates normalized emitter duration with a stable per-particle blend. The paired SDK normalizes Quarks' absolute emission time; stock playback remains an explicit absolute-time fallback.");
                    return Json.Object()
                        .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.start_color.v1"))
                        .Add("mode", Json.String("twoGradients"));
                case ParticleSystemGradientMode.RandomColor:
                    diagnostics.mapped.Add("main.startColor.randomColor.gradientSampleRuntime");
                    diagnostics.approximated.Add("main.startColor.randomColor.stockEmissionTimeGradientFallback");
                    diagnostics.warnings.Add("Unity Start Color Random Color samples one random position from the full gradient per particle. The paired SDK preserves that sampling; stock Quarks playback explicitly uses its emission-time Gradient fallback.");
                    return Json.Object()
                        .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.start_color.v1"))
                        .Add("mode", Json.String("randomColor"))
                        .Add("gradient", GradientJson(
                            color.gradient,
                            diagnostics,
                            "main.startColor.randomColor",
                            multiplier));
                default:
                    return null;
            }
        }

        internal JsonObject BuildParticleHeadStretchedBillboardSettings(
            ParticleSystem system,
            ParticleSystemRenderer renderer,
            ConversionDiagnostics diagnostics)
        {
            var speedFactor = renderer.velocityScale;
            if (Mathf.Abs(renderer.velocityScale) > 0.000001f)
            {
                var referenceSize = MaximumRenderedParticleSize(system);
                if (referenceSize > 0.000001f)
                {
                    speedFactor = renderer.velocityScale / referenceSize;
                    diagnostics.approximated.Add("trails.particleHeadRenderer.stretchedBillboard");
                    diagnostics.approximated.Add("trails.particleHeadRenderer.stretchedBillboard.velocityScale");
                    diagnostics.warnings.Add("Unity particle head velocity stretch is size-independent, while stock Quarks multiplies velocity stretch by current particle size; the companion head speedFactor is normalized at the maximum expected particle size.");
                }
                else
                {
                    diagnostics.unsupported.Add("trails.particleHeadRenderer.stretchedBillboard.velocityScale");
                    diagnostics.approximated.Add("trails.particleHeadRenderer.stretchedBillboard.velocityScale.unscaledStockFallback");
                    diagnostics.warnings.Add("The Unity particle head velocityScale is active but no positive particle-size reference could be derived. Best-effort keeps the unnormalized stock Quarks speed factor; strict export fails.");
                }
            }
            else
            {
                diagnostics.mapped.Add("trails.particleHeadRenderer.stretchedBillboard.velocityScale");
            }

            if (Mathf.Abs(renderer.cameraVelocityScale) > 0.000001f)
            {
                diagnostics.unsupported.Add("trails.particleHeadRenderer.stretchedBillboard.cameraVelocityScale");
                diagnostics.approximated.Add("trails.particleHeadRenderer.stretchedBillboard.cameraVelocityScale.omittedFallback");
                diagnostics.warnings.Add("Unity particle head cameraVelocityScale has no stock Quarks equivalent. Best-effort omits camera-velocity stretch; strict export fails.");
            }
            else
            {
                diagnostics.inactive.Add("trails.particleHeadRenderer.stretchedBillboard.cameraVelocityScale");
            }

            diagnostics.mapped.Add("trails.particleHeadRenderer.stretchedBillboard.lengthScale");
            return Json.Object()
                .Add("speedFactor", Json.Number(speedFactor))
                .Add("lengthFactor", Json.Number(renderer.lengthScale));
        }

        internal JsonObject BuildTrailInheritParticleColorMetadata(
            ParticleSystem system,
            int renderMode,
            bool materialConsumesParticleColor,
            ConversionDiagnostics diagnostics)
        {
            var colorOverLifetime = system.colorOverLifetime;
            if (renderMode != 3 ||
                !materialConsumesParticleColor ||
                !system.trails.inheritParticleColor ||
                !colorOverLifetime.enabled)
            {
                return null;
            }

            return Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.trail_inherit_particle_color.v1"))
                .Add("particleColorOverLifetime", Gradient(
                    colorOverLifetime.color,
                    diagnostics,
                    "colorOverLifetime.color"));
        }

        internal JsonObject BuildTextureSheetAnimationMetadata(
            ParticleSystem system,
            ConversionDiagnostics diagnostics)
        {
            var sheet = system.textureSheetAnimation;
            if (!sheet.enabled) return null;
            if (sheet.mode != ParticleSystemAnimationMode.Grid && sheet.mode != ParticleSystemAnimationMode.Sprites)
            {
                diagnostics.unsupported.Add("textureSheetAnimation.mode." + sheet.mode);
                diagnostics.approximated.Add("textureSheetAnimation.mode.omittedFallback");
                diagnostics.warnings.Add("Texture Sheet Animation mode " + sheet.mode + " has no exporter representation.");
                return null;
            }

            var animation = sheet.mode == ParticleSystemAnimationMode.Sprites
                ? "sprites"
                : sheet.animation == ParticleSystemAnimationType.SingleRow ? "singleRow" : "wholeSheet";
            var frameCount = sheet.mode == ParticleSystemAnimationMode.Sprites
                ? Mathf.Max(1, sheet.spriteCount)
                : sheet.animation == ParticleSystemAnimationType.SingleRow
                    ? Mathf.Max(1, sheet.numTilesX)
                    : Mathf.Max(1, sheet.numTilesX * sheet.numTilesY);
            var cycleCount = Mathf.Max(1, sheet.cycleCount);
            var timeMode = sheet.timeMode == ParticleSystemAnimationTimeMode.FPS
                ? "fps"
                : sheet.timeMode == ParticleSystemAnimationTimeMode.Speed ? "speed" : "lifetime";
            diagnostics.mapped.Add("textureSheetAnimation." + animation);
            diagnostics.mapped.Add("textureSheetAnimation.timeMode." + timeMode + ".runtime");
            if (cycleCount != 1 || CurveHasEffect(sheet.startFrame))
            {
                diagnostics.approximated.Add("textureSheetAnimation.stockSingleCycleFallback");
                diagnostics.warnings.Add("Texture Sheet Animation cycleCount and startFrame are preserved in exporter metadata for the paired SDK runtime. Stock Quarks playback retains one normalized fallback cycle starting at frame zero.");
            }
            var metadata = Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.texture_sheet_animation.v2"))
                .Add("mode", Json.String(sheet.mode == ParticleSystemAnimationMode.Sprites ? "sprites" : "grid"))
                .Add("animation", Json.String(animation))
                .Add("timeMode", Json.String(timeMode))
                .Add("frameCount", Json.Number(frameCount))
                .Add("tileCountX", Json.Number(Mathf.Max(1, sheet.numTilesX)))
                .Add("tileCountY", Json.Number(Mathf.Max(1, sheet.numTilesY)))
                .Add("cycleCount", Json.Number(cycleCount))
                .Add("fps", Json.Number(Mathf.Max(0, sheet.fps)))
                .Add("speedRange", Json.Array()
                    .Add(Json.Number(sheet.speedRange.x))
                    .Add(Json.Number(sheet.speedRange.y)))
                .Add("rowMode", Json.String(sheet.rowMode == ParticleSystemAnimationRowMode.Random ? "random" :
                    sheet.rowMode == ParticleSystemAnimationRowMode.MeshIndex ? "meshIndex" : "custom"))
                .Add("rowIndex", Json.Number(Mathf.Clamp(sheet.rowIndex, 0, Mathf.Max(0, sheet.numTilesY - 1))))
                .Add("frameOverTime", VelocityCurveMetadata(
                    sheet.frameOverTime,
                    diagnostics,
                    "textureSheetAnimation.frameOverTime"))
                .Add("startFrame", VelocityCurveMetadata(
                    sheet.startFrame,
                    diagnostics,
                    "textureSheetAnimation.startFrame"));
            if (sheet.mode == ParticleSystemAnimationMode.Sprites)
            {
                var sprites = Json.Array();
                var validSpriteCount = 0;
                var firstWidth = 0f;
                for (var index = 0; index < sheet.spriteCount; index++)
                {
                    var sprite = sheet.GetSprite(index);
                    if (sprite == null || sprite.texture == null || sprite.texture.width <= 0 || sprite.texture.height <= 0)
                    {
                        diagnostics.unsupported.Add("textureSheetAnimation.sprites.missingSprite");
                        diagnostics.approximated.Add("textureSheetAnimation.sprites.invalidFrameFallback");
                        sprites.Add(DefaultSpriteFrameMetadata());
                        continue;
                    }
                    var rect = sprite.textureRect;
                    if (rect.width <= 0 || rect.height <= 0)
                    {
                        diagnostics.unsupported.Add("textureSheetAnimation.sprites.invalidRect");
                        diagnostics.approximated.Add("textureSheetAnimation.sprites.invalidFrameFallback");
                        sprites.Add(DefaultSpriteFrameMetadata());
                        continue;
                    }
                    if (firstWidth <= 0) firstWidth = rect.width;
                    var pivot = sprite.pivot;
                    var normalizedPivot = new Vector2(
                        rect.width <= 0 ? 0.5f : pivot.x / rect.width,
                        rect.height <= 0 ? 0.5f : pivot.y / rect.height);
                    sprites.Add(Json.Object()
                        .Add("rect", Json.Array()
                            .Add(Json.Number(rect.x / sprite.texture.width))
                            .Add(Json.Number(rect.y / sprite.texture.height))
                            .Add(Json.Number(rect.width / sprite.texture.width))
                            .Add(Json.Number(rect.height / sprite.texture.height)))
                        .Add("sizeMul", Json.Array()
                            .Add(Json.Number(rect.width / firstWidth))
                            .Add(Json.Number(rect.height / firstWidth)))
                        .Add("pivot", Json.Array()
                            .Add(Json.Number(0.5f - normalizedPivot.x))
                            .Add(Json.Number(0.5f - normalizedPivot.y))));
                    validSpriteCount++;
                }
                if (validSpriteCount == 0)
                {
                    diagnostics.unsupported.Add("textureSheetAnimation.sprites.empty");
                    diagnostics.approximated.Add("textureSheetAnimation.sprites.emptyFallback");
                    return null;
                }
                metadata.Add("sprites", sprites);
            }
            return metadata;
        }

        private static JsonObject DefaultSpriteFrameMetadata()
        {
            return Json.Object()
                .Add("rect", Json.Array()
                    .Add(Json.Number(0)).Add(Json.Number(0)).Add(Json.Number(1)).Add(Json.Number(1)))
                .Add("sizeMul", Json.Array().Add(Json.Number(1)).Add(Json.Number(1)))
                .Add("pivot", Json.Array().Add(Json.Number(0)).Add(Json.Number(0)));
        }

        internal JsonObject BuildMeshScalarRotationMetadata(
            ParticleSystem system,
            int renderMode,
            Vector3 particleAxisSigns,
            ConversionDiagnostics diagnostics)
        {
            if (renderMode != 2) return null;

            var main = system.main;
            var rotationOverLifetime = system.rotationOverLifetime;
            var rotationBySpeed = system.rotationBySpeed;
            var scalarStart = !main.startRotation3D && CurveHasEffect(main.startRotation);
            var scalarOverLifetime = rotationOverLifetime.enabled &&
                                     !rotationOverLifetime.separateAxes &&
                                     CurveHasEffect(rotationOverLifetime.z);
            var scalarBySpeed = rotationBySpeed.enabled &&
                                !rotationBySpeed.separateAxes &&
                                CurveIsSpeedIndependent(rotationBySpeed.z) &&
                                CurveHasEffect(rotationBySpeed.z);
            var hasScalarRotation = scalarStart || scalarOverLifetime || scalarBySpeed;
            if (!hasScalarRotation) return null;

            var threeDimensionalStart = main.startRotation3D &&
                                        (CurveHasEffect(main.startRotationX) ||
                                         CurveHasEffect(main.startRotationY) ||
                                         CurveHasEffect(main.startRotationZ));
            var threeDimensionalOverLifetime = rotationOverLifetime.enabled &&
                                               rotationOverLifetime.separateAxes &&
                                               (CurveHasEffect(rotationOverLifetime.x) ||
                                                CurveHasEffect(rotationOverLifetime.y) ||
                                                CurveHasEffect(rotationOverLifetime.z));
            var threeDimensionalBySpeed = rotationBySpeed.enabled &&
                                          rotationBySpeed.separateAxes &&
                                          (CurveHasEffect(rotationBySpeed.x) ||
                                           CurveHasEffect(rotationBySpeed.y) ||
                                           CurveHasEffect(rotationBySpeed.z));
            if (threeDimensionalStart || threeDimensionalOverLifetime || threeDimensionalBySpeed)
            {
                diagnostics.unsupported.Add("rotation.meshScalarAxis.mixed3D");
                diagnostics.approximated.Add("rotation.meshScalarAxis.mixed3D.stockZFallback");
                diagnostics.warnings.Add("Unity Mesh scalar rotation uses Particle.axisOfRotation. Composing that Shape-derived axis with active separate-axis 3D rotation has not been black-box matched and was not silently treated as fixed local Z.");
                return null;
            }

            if (!MeshScalarRotationAxisClassifier.TryClassify(system, out var classification, out var failure))
            {
                diagnostics.unsupported.Add("rotation.meshScalarAxis.unclassified");
                diagnostics.approximated.Add("rotation.meshScalarAxis.unclassified.stockZFallback");
                diagnostics.warnings.Add(failure);
                return null;
            }

            var mode = MeshScalarRotationAxisModeName(classification.mode);
            var localBasis = UnityLocalToQuarksLocal * Matrix4x4.Scale(particleAxisSigns);
            var shape = system.shape;
            var shapeMatrix = shape.enabled
                ? Matrix4x4.TRS(shape.position, Quaternion.Euler(shape.rotation), shape.scale)
                : Matrix4x4.identity;
            if ((classification.mode == MeshScalarRotationAxisMode.Position ||
                 classification.mode == MeshScalarRotationAxisMode.Velocity) &&
                Mathf.Abs(shapeMatrix.determinant) <= 0.0000001f)
            {
                diagnostics.unsupported.Add("rotation.meshScalarAxis.singularShapeTransform");
                diagnostics.approximated.Add("rotation.meshScalarAxis.singularShapeTransform.stockZFallback");
                diagnostics.warnings.Add("Unity Mesh scalar rotation uses a Shape-local direction that cannot be reconstructed from particle position or velocity after a singular Shape scale. Strict export fails instead of inventing an axis.");
                return null;
            }
            var metadata = Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.mesh_scalar_rotation.v2"))
                .Add("axisMode", Json.String(mode))
                .Add("basisX", VectorArray(localBasis.MultiplyVector(Vector3.right)))
                .Add("basisY", VectorArray(localBasis.MultiplyVector(Vector3.up)))
                .Add("basisZ", VectorArray(localBasis.MultiplyVector(Vector3.forward)))
                .Add("shapeOrigin", VectorArray(shapeMatrix.MultiplyPoint3x4(Vector3.zero)))
                .Add("shapeBasisX", VectorArray(shapeMatrix.MultiplyVector(Vector3.right)))
                .Add("shapeBasisY", VectorArray(shapeMatrix.MultiplyVector(Vector3.up)))
                .Add("shapeBasisZ", VectorArray(shapeMatrix.MultiplyVector(Vector3.forward)));
            if (classification.mode == MeshScalarRotationAxisMode.Fixed)
            {
                var quarksAxis = localBasis.MultiplyVector(classification.axis).normalized;
                metadata.Add("axis", VectorArray(quarksAxis));
            }

            diagnostics.mapped.Add("rotation.meshScalarAxis." + mode + "Runtime");
            diagnostics.approximated.Add("rotation.meshScalarAxis.stockZFallback");
            diagnostics.warnings.Add("Unity Mesh scalar rotation is preserved as a stable per-particle axis by the paired SDK runtime. Stock Quarks playback retains the loadable local-Z quaternion fallback and is not orientation-equivalent.");

            if (shape.enabled &&
                Mathf.Abs(shape.randomDirectionAmount) > 0.000001f &&
                !CurveHasEffect(main.startSpeed))
            {
                diagnostics.inactive.Remove("shape.randomDirectionAmount.zeroStartSpeed");
                diagnostics.mapped.Add("shape.randomDirectionAmount.meshRotationAxisRuntime");
                diagnostics.warnings.Add("Zero start speed removes translational velocity, but Shape randomDirectionAmount still affects Unity Mesh Particle.axisOfRotation and is preserved by the paired runtime.");
            }
            return metadata;
        }

        private static string MeshScalarRotationAxisModeName(MeshScalarRotationAxisMode mode)
        {
            switch (mode)
            {
                case MeshScalarRotationAxisMode.Fixed: return "fixed";
                case MeshScalarRotationAxisMode.Position: return "position";
                case MeshScalarRotationAxisMode.Velocity: return "velocity";
                case MeshScalarRotationAxisMode.UniformXY: return "uniformXY";
                default: throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
            }
        }

        internal JsonObject BuildMeshRotationBySpeedMetadata(
            ParticleSystem system,
            int renderMode,
            Vector3 particleAxisSigns,
            ConversionDiagnostics diagnostics)
        {
            if (renderMode != 2) return null;
            var rotation = system.rotationBySpeed;
            if (!rotation.enabled || rotation.separateAxes || CurveIsSpeedIndependent(rotation.z)) return null;
            if (!MeshScalarRotationAxisClassifier.TryClassify(system, out var classification, out var failure))
            {
                diagnostics.unsupported.Add("rotationBySpeed.meshSpeedDependent");
                diagnostics.approximated.Add("rotationBySpeed.meshSpeedDependent.omittedFallback");
                diagnostics.warnings.Add(failure);
                return null;
            }
            var localBasis = UnityLocalToQuarksLocal * Matrix4x4.Scale(particleAxisSigns);
            var metadata = Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.mesh_rotation_by_speed.v1"))
                .Add("axisMode", Json.String(MeshScalarRotationAxisModeName(classification.mode)))
                .Add("speedRange", Json.Array().Add(Json.Number(rotation.range.x)).Add(Json.Number(rotation.range.y)))
                .Add("angularVelocity", VelocityCurveMetadata(rotation.z, diagnostics, "rotationBySpeed.z"))
                .Add("basisX", VectorArray(localBasis.MultiplyVector(Vector3.right)))
                .Add("basisY", VectorArray(localBasis.MultiplyVector(Vector3.up)))
                .Add("basisZ", VectorArray(localBasis.MultiplyVector(Vector3.forward)));
            if (classification.mode == MeshScalarRotationAxisMode.Fixed)
                metadata.Add("axis", VectorArray(localBasis.MultiplyVector(classification.axis).normalized));
            diagnostics.mapped.Add("rotationBySpeed.meshSpeedDependent.runtime");
            diagnostics.approximated.Add("rotationBySpeed.meshSpeedDependent.stockOmittedFallback");
            diagnostics.warnings.Add("The paired SDK applies the single-axis Mesh rotation-by-speed curve as a quaternion delta. Stock Quarks has no quaternion-by-speed behavior.");
            return metadata;
        }

        internal JsonObject BuildTrailSemanticsMetadata(
            ParticleSystem system,
            int renderMode,
            ConversionDiagnostics diagnostics)
        {
            if (renderMode != 3) return null;
            var trails = system.trails;
            var hasColor = GradientHasEffect(trails.colorOverTrail);
            var hasMinVertexDistance = trails.minVertexDistance > 0.000001f;
            if (!hasColor && !trails.worldSpace && !trails.dieWithParticles && !trails.sizeAffectsWidth &&
                !hasMinVertexDistance) return null;
            var metadata = Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.trail_semantics.v1"))
                .Add("worldSpace", Json.Boolean(trails.worldSpace))
                .Add("dieWithParticles", Json.Boolean(trails.dieWithParticles))
                .Add("sizeAffectsWidth", Json.Boolean(trails.sizeAffectsWidth))
                .Add("minVertexDistance", Json.Number(Mathf.Max(0f, trails.minVertexDistance)));
            if (hasMinVertexDistance)
            {
                diagnostics.mapped.Add("trails.minVertexDistance.runtime");
                diagnostics.approximated.Add("trails.minVertexDistance.stockHistoryFilter");
            }
            if (hasColor)
            {
                metadata.Add("colorOverTrail", Gradient(trails.colorOverTrail, diagnostics, "trails.colorOverTrail"));
                diagnostics.mapped.Add("trails.colorOverTrail.runtime");
                diagnostics.approximated.Add("trails.colorOverTrail.stockLifetimeColorFallback");
                diagnostics.warnings.Add("The paired SDK samples colorOverTrail over normalized ribbon history length; stock Quarks has no ColorOverLength behavior.");
            }
            if (trails.worldSpace)
            {
                diagnostics.mapped.Add("trails.worldSpace.runtime");
                diagnostics.approximated.Add("trails.worldSpace.stockParticleSpaceFallback");
            }
            if (trails.dieWithParticles)
            {
                diagnostics.mapped.Add("trails.dieWithParticles.runtime");
                diagnostics.approximated.Add("trails.dieWithParticles.stockDrainFallback");
            }
            if (trails.sizeAffectsWidth)
            {
                diagnostics.mapped.Add("trails.sizeAffectsWidth.runtime");
                diagnostics.approximated.Add("trails.sizeAffectsWidth.stockWidthReplacementFallback");
                diagnostics.warnings.Add("The paired SDK multiplies each WidthOverLength sample by the particle size captured when that trail history point was created. Stock Quarks still replaces the stored width.");
            }
            return metadata;
        }

        internal void AddAppearanceBehaviors(
            JsonArray result,
            ParticleSystem system,
            int renderMode,
            int particleRenderMode,
            bool exactSizeOverLifetimeMapped,
            bool materialConsumesParticleColor,
            bool trailInheritParticleColorMapped,
            Vector3 particleAxisSigns,
            ConversionDiagnostics diagnostics)
        {
            var colorOverLife = system.colorOverLifetime;
            if (renderMode == 3)
            {
                var trails = system.trails;
                if (materialConsumesParticleColor)
                {
                    result.Add(Json.Object().Add("type", Json.String("ColorOverLife"))
                        .Add("color", Gradient(trails.colorOverLifetime, diagnostics, "trails.colorOverLifetime")));
                    diagnostics.mapped.Add("trails.colorOverLifetime");
                }
                else
                {
                    diagnostics.inactive.Add("trails.colorOverLifetime.notConsumedBySourceShader");
                }
                result.Add(Json.Object().Add("type", Json.String("WidthOverLength"))
                    .Add("width", TrailWidthCurve(trails.widthOverTrail, diagnostics, "trails.widthOverTrail")));
                diagnostics.mapped.Add("trails.widthOverTrail");
                if (GradientHasEffect(trails.colorOverTrail))
                {
                    diagnostics.mapped.Add("trails.colorOverTrail.runtime");
                    diagnostics.approximated.Add("trails.colorOverTrail.stockLifetimeColorFallback");
                    diagnostics.warnings.Add("The paired SDK samples colorOverTrail over normalized ribbon history length; stock Quarks has no ColorOverLength behavior.");
                }
                if (colorOverLife.enabled && trails.inheritParticleColor)
                {
                    if (trailInheritParticleColorMapped)
                    {
                        diagnostics.mapped.Add("trails.inheritParticleColor.runtime");
                        diagnostics.approximated.Add("trails.inheritParticleColor.stockOmittedFallback");
                        diagnostics.warnings.Add("The paired SDK multiplies ParticleSystem Color over Lifetime into trail lifetime color. Stock Quarks playback explicitly omits that multiplication.");
                    }
                    else
                    {
                        diagnostics.unsupported.Add("trails.inheritParticleColor");
                        diagnostics.approximated.Add("trails.inheritParticleColor.omittedFallback");
                        diagnostics.warnings.Add("Multiplying ParticleSystem Color over Lifetime into trail color is not representable by stock Quarks. Best-effort explicitly omits that multiplication; strict export fails.");
                    }
                }
                if (Mathf.Abs(trails.ratio - 1f) > 0.000001f)
                {
                    diagnostics.unsupported.Add("trails.ratio");
                    diagnostics.approximated.Add("trails.ratio.allParticlesFallback");
                    diagnostics.warnings.Add("Stock Quarks cannot assign trails to only a ratio of emitted particles. Best-effort gives every emitted particle a trail; strict export fails.");
                }
                if (trails.worldSpace)
                {
                    diagnostics.mapped.Add("trails.worldSpace.runtime");
                    diagnostics.approximated.Add("trails.worldSpace.stockParticleSpaceFallback");
                    diagnostics.warnings.Add("The paired SDK stores trail history in world space without changing particle simulation storage. Stock Quarks follows particle simulation space.");
                }
                if (trails.dieWithParticles)
                {
                    diagnostics.mapped.Add("trails.dieWithParticles.runtime");
                    diagnostics.approximated.Add("trails.dieWithParticles.stockDrainFallback");
                    diagnostics.warnings.Add("The paired SDK clears trail history when the source particle dies. Stock Quarks drains stored history over subsequent frames.");
                }
                if (trails.sizeAffectsLifetime)
                {
                    diagnostics.unsupported.Add("trails.sizeAffectsLifetime");
                    diagnostics.approximated.Add("trails.sizeAffectsLifetime.omittedFallback");
                    diagnostics.warnings.Add("Trail sizeAffectsLifetime has no Quarks equivalent. Best-effort explicitly omits it; strict export fails.");
                }
                if (trails.generateLightingData)
                {
                    diagnostics.unsupported.Add("trails.generateLightingData");
                    diagnostics.approximated.Add("trails.generateLightingData.unlitFallback");
                    diagnostics.warnings.Add("Trail lighting data is not consumed by the unlit Quarks trail shader. Best-effort renders the trail unlit; strict export fails.");
                }
            }
            else if (colorOverLife.enabled && materialConsumesParticleColor)
            {
                result.Add(Json.Object().Add("type", Json.String("ColorOverLife"))
                    .Add("color", Gradient(colorOverLife.color, diagnostics, "colorOverLifetime.color")));
                diagnostics.mapped.Add("colorOverLifetime");
            }
            else if (colorOverLife.enabled)
            {
                diagnostics.inactive.Add("colorOverLifetime.notConsumedBySourceShader");
                diagnostics.warnings.Add("Color over Lifetime is enabled, but the source shader does not consume ParticleSystem vertex color. The module is explicitly omitted from rendered color.");
            }

            var sizeOverLife = system.sizeOverLifetime;
            var trailWidthIsIndependent = renderMode == 3 && !system.trails.sizeAffectsWidth && particleRenderMode == 3;
            if (sizeOverLife.enabled && !trailWidthIsIndependent)
            {
                var size = exactSizeOverLifetimeMapped
                    ? SizeOverLifetimeStockFallback(sizeOverLife, diagnostics)
                    : sizeOverLife.separateAxes
                        ? VectorFunction(sizeOverLife.x, sizeOverLife.y, sizeOverLife.z, diagnostics, "sizeOverLifetime")
                        : Curve(sizeOverLife.size, diagnostics, "sizeOverLifetime.size");
                result.Add(Json.Object().Add("type", Json.String("SizeOverLife")).Add("size", size));
                diagnostics.mapped.Add("sizeOverLifetime");
            }
            else if (sizeOverLife.enabled)
            {
                diagnostics.inactive.Add("sizeOverLifetime.trailWidthIndependent");
                diagnostics.warnings.Add("Size over Lifetime is intentionally omitted because Trail sizeAffectsWidth is disabled and the ParticleSystem has no separately rendered particle in Quarks trail mode.");
            }

            var rotationOverLife = system.rotationOverLifetime;
            if (rotationOverLife.enabled)
            {
                if (rotationOverLife.separateAxes || particleRenderMode == 2)
                {
                    result.Add(Json.Object().Add("type", Json.String("Rotation3DOverLife"))
                        .Add("angularVelocity", rotationOverLife.separateAxes
                            ? Euler(
                                rotationOverLife.x,
                                rotationOverLife.y,
                                rotationOverLife.z,
                                AngularAxisSigns(particleAxisSigns),
                                diagnostics,
                                "rotationOverLifetime",
                                "ZXY")
                            : ScalarMeshEuler(
                                rotationOverLife.z,
                                ScalarAngleSign(particleAxisSigns),
                                diagnostics,
                                "rotationOverLifetime")));
                }
                else
                {
                    result.Add(Json.Object().Add("type", Json.String("RotationOverLife"))
                        .Add("angularVelocity", Curve(rotationOverLife.z, diagnostics, "rotationOverLifetime.z")));
                }
                diagnostics.mapped.Add("rotationOverLifetime");
            }

            var colorBySpeed = system.colorBySpeed;
            if (colorBySpeed.enabled && materialConsumesParticleColor)
            {
                result.Add(Json.Object().Add("type", Json.String("ColorBySpeed"))
                    .Add("color", Gradient(colorBySpeed.color, diagnostics, "colorBySpeed.color"))
                    .Add("speedRange", Interval(colorBySpeed.range.x, colorBySpeed.range.y)));
                diagnostics.mapped.Add("colorBySpeed");
            }
            else if (colorBySpeed.enabled)
            {
                diagnostics.inactive.Add("colorBySpeed.notConsumedBySourceShader");
                diagnostics.warnings.Add("Color by Speed is enabled, but the source shader does not consume ParticleSystem vertex color. The module is explicitly omitted from rendered color.");
            }

            var sizeBySpeed = system.sizeBySpeed;
            if (sizeBySpeed.enabled)
            {
                var size = sizeBySpeed.separateAxes
                    ? VectorFunction(sizeBySpeed.x, sizeBySpeed.y, sizeBySpeed.z, diagnostics, "sizeBySpeed")
                    : Curve(sizeBySpeed.size, diagnostics, "sizeBySpeed.size");
                result.Add(Json.Object().Add("type", Json.String("SizeBySpeed"))
                    .Add("size", size)
                    .Add("speedRange", Interval(sizeBySpeed.range.x, sizeBySpeed.range.y)));
                diagnostics.mapped.Add("sizeBySpeed");
            }

            var rotationBySpeed = system.rotationBySpeed;
            if (rotationBySpeed.enabled)
            {
                if (rotationBySpeed.separateAxes)
                {
                    diagnostics.unsupported.Add("rotationBySpeed.separateAxes");
                    diagnostics.approximated.Add("rotationBySpeed.separateAxes.omittedFallback");
                    diagnostics.warnings.Add("Separate-axis Rotation by Speed has no stock quaternion-by-speed behavior. Best-effort explicitly omits it; strict export fails.");
                }
                else if (particleRenderMode == 2)
                {
                    if (CurveIsSpeedIndependent(rotationBySpeed.z))
                    {
                        result.Add(Json.Object().Add("type", Json.String("Rotation3DOverLife"))
                            .Add("angularVelocity", ScalarMeshEuler(
                                rotationBySpeed.z,
                                ScalarAngleSign(particleAxisSigns),
                                diagnostics,
                                "rotationBySpeed")));
                        diagnostics.mapped.Add("rotationBySpeed.constantMesh");
                    }
                    else
                    {
                        if (MeshScalarRotationAxisClassifier.TryClassify(system, out _, out _))
                        {
                            diagnostics.mapped.Add("rotationBySpeed.meshSpeedDependent.runtime");
                            diagnostics.approximated.Add("rotationBySpeed.meshSpeedDependent.stockOmittedFallback");
                        }
                        else
                        {
                            diagnostics.unsupported.Add("rotationBySpeed.meshSpeedDependent");
                            diagnostics.approximated.Add("rotationBySpeed.meshSpeedDependent.omittedFallback");
                            diagnostics.warnings.Add("Stock Quarks 0.17.1 has no quaternion Rotation3DBySpeed behavior; a speed-dependent Mesh rotation curve was not emitted as a no-op scalar behavior.");
                        }
                    }
                }
                else
                {
                    result.Add(Json.Object().Add("type", Json.String("RotationBySpeed"))
                        .Add("angularVelocity", Curve(rotationBySpeed.z, diagnostics, "rotationBySpeed.z"))
                        .Add("speedRange", Interval(rotationBySpeed.range.x, rotationBySpeed.range.y)));
                    diagnostics.mapped.Add("rotationBySpeed");
                }
            }
        }

        internal void AddTextureSheetBehavior(
            JsonArray result,
            ParticleSystem system,
            ConversionDiagnostics diagnostics)
        {
            var sheet = system.textureSheetAnimation;
            if (sheet.enabled)
            {
                var frameCount = sheet.mode == ParticleSystemAnimationMode.Grid
                    ? sheet.animation == ParticleSystemAnimationType.SingleRow
                        ? Mathf.Max(1, sheet.numTilesX)
                        : Mathf.Max(1, sheet.numTilesX * sheet.numTilesY)
                    : Mathf.Max(1, sheet.spriteCount);
                result.Add(Json.Object().Add("type", Json.String("FrameOverLife"))
                    .Add("frame", ScaleCurve(sheet.frameOverTime, frameCount, diagnostics, "textureSheetAnimation.frameOverTime")));
                diagnostics.approximated.Add("textureSheetAnimation.stockSequencerFallback");
                diagnostics.warnings.Add("Texture Sheet Animation is preserved in exporter metadata and replaced by the paired SDK behavior. Stock Quarks retains a normalized FrameOverLife fallback for loading and non-SDK playback.");
            }
        }
    }
}
