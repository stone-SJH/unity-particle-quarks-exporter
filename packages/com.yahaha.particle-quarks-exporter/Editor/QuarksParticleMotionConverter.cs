using UnityEngine;
using static UnityParticleQuarksExporter.Editor.QuarksCoordinateUtility;
using static UnityParticleQuarksExporter.Editor.QuarksParticleSemanticsUtility;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class QuarksParticleMotionConverter
    {
        internal JsonValue BuildStartLifetime(
            ParticleSystem.MainModule main,
            ConversionDiagnostics diagnostics)
        {
            return Curve(main.startLifetime, diagnostics, "main.startLifetime");
        }

        internal JsonValue BuildStartSpeed(
            ParticleSystem.MainModule main,
            ConversionDiagnostics diagnostics)
        {
            return Curve(main.startSpeed, diagnostics, "main.startSpeed");
        }

        internal JsonObject BuildVelocityOverLifetimeMetadata(
            ParticleSystem system,
            ScalingContext scaling,
            Matrix4x4 particleToThreeWorld,
            ConversionDiagnostics diagnostics)
        {
            var velocity = system.velocityOverLifetime;
            var linearHasEffect = velocity.enabled &&
                                  (CurveHasEffect(velocity.x) || CurveHasEffect(velocity.y) || CurveHasEffect(velocity.z));
            var orbitalHasEffect = velocity.enabled &&
                                   (CurveHasEffect(velocity.orbitalX) || CurveHasEffect(velocity.orbitalY) || CurveHasEffect(velocity.orbitalZ));
            var offsetHasEffect = velocity.enabled &&
                                  (CurveHasEffect(velocity.orbitalOffsetX) || CurveHasEffect(velocity.orbitalOffsetY) || CurveHasEffect(velocity.orbitalOffsetZ));
            var radialHasEffect = velocity.enabled && CurveHasEffect(velocity.radial);
            var hasEffect = linearHasEffect || orbitalHasEffect || offsetHasEffect || radialHasEffect;
            if (!hasEffect ||
                velocity.space == ParticleSystemSimulationSpace.Custom ||
                system.main.simulationSpace == ParticleSystemSimulationSpace.Custom)
            {
                return null;
            }

            var moduleToParticleStorage = BuildVectorModuleBasis(
                system,
                scaling,
                particleToThreeWorld,
                velocity.space);
            if (linearHasEffect)
            {
                diagnostics.mapped.Add("velocityOverLifetime.linear");
            }
            if (orbitalHasEffect)
            {
                diagnostics.mapped.Add("velocityOverLifetime.orbital.runtime");
            }
            if (offsetHasEffect)
            {
                diagnostics.mapped.Add("velocityOverLifetime.orbitalOffset.runtime");
            }
            if (radialHasEffect)
            {
                diagnostics.mapped.Add("velocityOverLifetime.radial.runtime");
            }
            diagnostics.warnings.Add("Velocity over Lifetime linear, orbital, offset, and radial fields are preserved in exporter userData and applied by the paired SDK runtime using Unity's local-space basis and per-frame position-delta formula. Stock Quarks playback retains only the loadable linear fallback.");
            return Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.velocity_over_lifetime.v2"))
                .Add("space", Json.String(velocity.space == ParticleSystemSimulationSpace.World ? "world" : "local"))
                .Add("basisX", VectorArray(moduleToParticleStorage.MultiplyVector(Vector3.right)))
                .Add("basisY", VectorArray(moduleToParticleStorage.MultiplyVector(Vector3.up)))
                .Add("basisZ", VectorArray(moduleToParticleStorage.MultiplyVector(Vector3.forward)))
                .Add("origin", VectorArray(moduleToParticleStorage.MultiplyPoint3x4(Vector3.zero)))
                .Add("x", VelocityCurveMetadata(velocity.x, diagnostics, "velocityOverLifetime.x"))
                .Add("y", VelocityCurveMetadata(velocity.y, diagnostics, "velocityOverLifetime.y"))
                .Add("z", VelocityCurveMetadata(velocity.z, diagnostics, "velocityOverLifetime.z"))
                .Add("orbitalX", VelocityCurveMetadata(velocity.orbitalX, diagnostics, "velocityOverLifetime.orbitalX"))
                .Add("orbitalY", VelocityCurveMetadata(velocity.orbitalY, diagnostics, "velocityOverLifetime.orbitalY"))
                .Add("orbitalZ", VelocityCurveMetadata(velocity.orbitalZ, diagnostics, "velocityOverLifetime.orbitalZ"))
                .Add("orbitalOffsetX", VelocityCurveMetadata(velocity.orbitalOffsetX, diagnostics, "velocityOverLifetime.orbitalOffsetX"))
                .Add("orbitalOffsetY", VelocityCurveMetadata(velocity.orbitalOffsetY, diagnostics, "velocityOverLifetime.orbitalOffsetY"))
                .Add("orbitalOffsetZ", VelocityCurveMetadata(velocity.orbitalOffsetZ, diagnostics, "velocityOverLifetime.orbitalOffsetZ"))
                .Add("radial", VelocityCurveMetadata(velocity.radial, diagnostics, "velocityOverLifetime.radial"))
                .Add("speedModifier", VelocityCurveMetadata(velocity.speedModifier, diagnostics, "velocityOverLifetime.speedModifier"));
        }

        internal JsonObject BuildForceOverLifetimeMetadata(
            ParticleSystem system,
            ScalingContext scaling,
            Matrix4x4 particleToThreeWorld,
            ConversionDiagnostics diagnostics)
        {
            var force = system.forceOverLifetime;
            var hasEffect = force.enabled &&
                            (CurveHasEffect(force.x) || CurveHasEffect(force.y) || CurveHasEffect(force.z));
            if (!hasEffect || system.main.simulationSpace == ParticleSystemSimulationSpace.Custom)
            {
                if (hasEffect && system.main.simulationSpace == ParticleSystemSimulationSpace.Custom)
                {
                    diagnostics.unsupported.Add("forceOverLifetime.customParticleSimulationSpace");
                    diagnostics.approximated.Add("forceOverLifetime.customParticleSimulationSpace.omittedFallback");
                    diagnostics.warnings.Add("Force over Lifetime cannot be placed into custom particle storage. Best-effort explicitly omits the force; strict export fails.");
                }
                return null;
            }

            var customSpace = force.space == ParticleSystemSimulationSpace.Custom
                ? system.main.customSimulationSpace
                : null;
            if (force.space == ParticleSystemSimulationSpace.Custom && customSpace == null)
            {
                diagnostics.unsupported.Add("forceOverLifetime.customSimulationSpace.missingTransform");
                diagnostics.approximated.Add("forceOverLifetime.customSimulationSpace.omittedFallback");
                diagnostics.warnings.Add("Force over Lifetime custom space has no Main customSimulationSpace Transform. Best-effort explicitly omits the force; strict export fails.");
                return null;
            }
            var basis = force.space == ParticleSystemSimulationSpace.Custom
                ? particleToThreeWorld.inverse * UnityWorldToThreeWorld * customSpace.localToWorldMatrix
                : BuildVectorModuleBasis(system, scaling, particleToThreeWorld, force.space);
            if (force.space == ParticleSystemSimulationSpace.Custom)
            {
                diagnostics.mapped.Add("forceOverLifetime.customSimulationSpace.runtime");
                diagnostics.approximated.Add("forceOverLifetime.customSimulationSpace.stockOmittedFallback");
            }
            return Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.force_over_lifetime.v1"))
                .Add("space", Json.String(force.space == ParticleSystemSimulationSpace.World ? "world" : force.space == ParticleSystemSimulationSpace.Custom ? "custom" : "local"))
                .Add("customTransformName", customSpace == null ? null : Json.String(customSpace.name))
                .Add("basisX", VectorArray(basis.MultiplyVector(Vector3.right)))
                .Add("basisY", VectorArray(basis.MultiplyVector(Vector3.up)))
                .Add("basisZ", VectorArray(basis.MultiplyVector(Vector3.forward)))
                .Add("x", VelocityCurveMetadata(force.x, diagnostics, "forceOverLifetime.x"))
                .Add("y", VelocityCurveMetadata(force.y, diagnostics, "forceOverLifetime.y"))
                .Add("z", VelocityCurveMetadata(force.z, diagnostics, "forceOverLifetime.z"));
        }

        internal JsonObject BuildGravityMetadata(
            ParticleSystem system,
            Matrix4x4 particleToThreeWorld,
            ConversionDiagnostics diagnostics)
        {
            var main = system.main;
            if (!CurveHasEffect(main.gravityModifier))
            {
                return null;
            }
            if (main.simulationSpace == ParticleSystemSimulationSpace.Custom)
            {
                diagnostics.unsupported.Add("main.gravityModifier.customSimulationSpace");
                diagnostics.approximated.Add("main.gravityModifier.customSimulationSpace.omittedFallback");
                diagnostics.warnings.Add("Gravity cannot be transformed into custom particle storage. Best-effort explicitly omits gravity; strict export fails.");
                return null;
            }

            var gravity = UnityWorldToThreeWorld.MultiplyVector(Physics.gravity);
            if (main.simulationSpace != ParticleSystemSimulationSpace.World)
            {
                gravity = particleToThreeWorld.inverse.MultiplyVector(gravity);
            }
            diagnostics.mapped.Add("main.gravityModifier.runtimeBasis");
            return Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.gravity.v1"))
                .Add("acceleration", VectorArray(gravity))
                .Add("modifier", VelocityCurveMetadata(main.gravityModifier, diagnostics, "main.gravityModifier"));
        }

        internal JsonObject BuildLimitVelocityOverLifetimeMetadata(
            ParticleSystem system,
            ConversionDiagnostics diagnostics)
        {
            var limit = system.limitVelocityOverLifetime;
            if (!limit.enabled || limit.separateAxes)
            {
                if (!limit.enabled) return null;
            }

            var hasLimit = CurveHasEffect(limit.limit);
            var hasDrag = CurveHasEffect(limit.drag);
            var hasSeparateLimit = limit.separateAxes &&
                (CurveHasEffect(limit.limitX) || CurveHasEffect(limit.limitY) || CurveHasEffect(limit.limitZ));
            if (!hasLimit && !hasDrag && !hasSeparateLimit) return null;
            if (hasLimit || hasSeparateLimit)
            {
                diagnostics.mapped.Add("limitVelocityOverLifetime.limit.runtime");
                if (limit.limit.mode == ParticleSystemCurveMode.TwoCurves)
                {
                    diagnostics.approximated.Add("limitVelocityOverLifetime.limit.twoCurves.stockMeanFallback");
                    diagnostics.warnings.Add("Limit Velocity TwoCurves is preserved for stable per-particle evaluation by the paired SDK runtime. Stock Quarks playback uses the arithmetic-mean curve fallback.");
                }
            }
            return Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.limit_velocity_over_lifetime.v3"))
                .Add("separateAxes", Json.Boolean(limit.separateAxes))
                .Add("limit", !limit.separateAxes && hasLimit ? VelocityCurveMetadata(limit.limit, diagnostics, "limitVelocityOverLifetime.limit") : null)
                .Add("limitX", hasSeparateLimit ? VelocityCurveMetadata(limit.limitX, diagnostics, "limitVelocityOverLifetime.limitX") : null)
                .Add("limitY", hasSeparateLimit ? VelocityCurveMetadata(limit.limitY, diagnostics, "limitVelocityOverLifetime.limitY") : null)
                .Add("limitZ", hasSeparateLimit ? VelocityCurveMetadata(limit.limitZ, diagnostics, "limitVelocityOverLifetime.limitZ") : null)
                .Add("dampen", Json.Number(limit.dampen))
                .Add("drag", hasDrag ? VelocityCurveMetadata(limit.drag, diagnostics, "limitVelocityOverLifetime.drag") : null)
                .Add("multiplyDragByParticleSize", Json.Boolean(limit.multiplyDragByParticleSize))
                .Add("multiplyDragByParticleVelocity", Json.Boolean(limit.multiplyDragByParticleVelocity));
        }

        internal JsonObject BuildInheritVelocityMetadata(
            ParticleSystem system,
            ConversionDiagnostics diagnostics)
        {
            var inherit = system.inheritVelocity;
            if (!inherit.enabled) return null;
            if (!CurveHasEffect(inherit.curve))
            {
                diagnostics.inactive.Add("inheritVelocity");
                return null;
            }
            if (inherit.mode != ParticleSystemInheritVelocityMode.Initial &&
                inherit.mode != ParticleSystemInheritVelocityMode.Current)
            {
                diagnostics.unsupported.Add("inheritVelocity." + inherit.mode);
                diagnostics.approximated.Add("inheritVelocity." + inherit.mode + ".omittedFallback");
                diagnostics.warnings.Add("Inherit Velocity mode " + inherit.mode + " is active and has no mapped Quarks/runtime implementation. Best-effort explicitly omits the module; strict export fails.");
                return null;
            }

            var mode = inherit.mode == ParticleSystemInheritVelocityMode.Current ? "current" : "initial";
            diagnostics.mapped.Add("inheritVelocity." + mode + ".runtime");
            diagnostics.approximated.Add("inheritVelocity." + mode + ".stockOmittedFallback");
            diagnostics.warnings.Add((mode == "current"
                ? "Current Inherit Velocity adds the emitter's frame-to-frame velocity on every simulation step."
                : "Initial Inherit Velocity is applied once at particle birth from the emitter's frame-to-frame velocity.") + " Stock Quarks playback explicitly omits the module.");
            return Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.inherit_velocity.v2"))
                .Add("mode", Json.String(mode))
                .Add("curve", VelocityCurveMetadata(inherit.curve, diagnostics, "inheritVelocity.curve"));
        }

        internal JsonObject BuildNoiseMetadata(
            ParticleSystem system,
            ConversionDiagnostics diagnostics)
        {
            var noise = system.noise;
            var hasStrength = noise.separateAxes
                ? CurveHasEffect(noise.strengthX) || CurveHasEffect(noise.strengthY) || CurveHasEffect(noise.strengthZ)
                : CurveHasEffect(noise.strength);
            if (!noise.enabled || !hasStrength || !CurveHasEffect(noise.positionAmount) ||
                system.main.simulationSpace == ParticleSystemSimulationSpace.Custom)
            {
                return null;
            }

            var frequency = Mathf.Max(0.001f, noise.frequency);
            var unityToParticle = system.main.simulationSpace == ParticleSystemSimulationSpace.World
                ? UnityWorldToThreeWorld
                : UnityLocalToQuarksLocal;
            var particleToUnity = unityToParticle.inverse;
            var strengthX = noise.separateAxes ? noise.strengthX : noise.strength;
            var strengthY = noise.separateAxes ? noise.strengthY : noise.strength;
            var strengthZ = noise.separateAxes ? noise.strengthZ : noise.strength;
            var qualityDimensions = Mathf.Clamp((int)noise.quality + 1, 1, 3);

            diagnostics.mapped.Add("noise.position.runtime");
            diagnostics.mapped.Add("noise.spatialCurl.runtime");
            diagnostics.mapped.Add("noise.quality." + noise.quality + ".runtime");
            diagnostics.mapped.Add(noise.damping ? "noise.damping.runtime" : "noise.undamped.runtime");
            if (noise.separateAxes) diagnostics.mapped.Add("noise.separateAxes.runtime");
            if (noise.octaveCount > 1) diagnostics.mapped.Add("noise.octaves.runtime");
            if (CurveHasEffect(noise.scrollSpeed)) diagnostics.mapped.Add("noise.scrollSpeed.runtime");
            if (noise.remapEnabled)
            {
                diagnostics.mapped.Add("noise.remap.runtime");
                diagnostics.approximated.Add("noise.remap.lutEquivalent");
            }
            diagnostics.warnings.Add("The paired SDK replaces stock Quarks temporal position Noise with Unity's seeded spatial Perlin-derivative curl velocity field, including quality, octaves, damping, scroll, and per-axis Strength curves.");

            var metadata = Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.noise.v1"))
                .Add("simulationSpace", Json.String(system.main.simulationSpace == ParticleSystemSimulationSpace.World ? "world" : "local"))
                .Add("particleToNoiseBasisX", VectorArray(particleToUnity.MultiplyVector(Vector3.right)))
                .Add("particleToNoiseBasisY", VectorArray(particleToUnity.MultiplyVector(Vector3.up)))
                .Add("particleToNoiseBasisZ", VectorArray(particleToUnity.MultiplyVector(Vector3.forward)))
                .Add("noiseToParticleBasisX", VectorArray(unityToParticle.MultiplyVector(Vector3.right)))
                .Add("noiseToParticleBasisY", VectorArray(unityToParticle.MultiplyVector(Vector3.up)))
                .Add("noiseToParticleBasisZ", VectorArray(unityToParticle.MultiplyVector(Vector3.forward)))
                .Add("randomSeed", Json.Number(system.randomSeed))
                .Add("separateAxes", Json.Boolean(noise.separateAxes))
                .Add("frequency", Json.Number(frequency))
                .Add("damping", Json.Boolean(noise.damping))
                .Add("qualityDimensions", Json.Number(qualityDimensions))
                .Add("octaveCount", Json.Number(Mathf.Max(1, noise.octaveCount)))
                .Add("octaveMultiplier", Json.Number(noise.octaveMultiplier))
                .Add("octaveScale", Json.Number(noise.octaveScale))
                .Add("strengthX", VelocityCurveMetadata(strengthX, diagnostics, "noise.strengthX"))
                .Add("strengthY", VelocityCurveMetadata(strengthY, diagnostics, "noise.strengthY"))
                .Add("strengthZ", VelocityCurveMetadata(strengthZ, diagnostics, "noise.strengthZ"))
                .Add("positionAmount", VelocityCurveMetadata(noise.positionAmount, diagnostics, "noise.positionAmount"))
                .Add("scrollSpeed", VelocityCurveMetadata(noise.scrollSpeed, diagnostics, "noise.scrollSpeed"))
                .Add("remapEnabled", Json.Boolean(noise.remapEnabled));
            if (noise.remapEnabled)
            {
                metadata
                    .Add("remapX", VelocityCurveMetadata(noise.separateAxes ? noise.remapX : noise.remap, diagnostics, "noise.remapX"))
                    .Add("remapY", VelocityCurveMetadata(noise.separateAxes ? noise.remapY : noise.remap, diagnostics, "noise.remapY"))
                    .Add("remapZ", VelocityCurveMetadata(noise.separateAxes ? noise.remapZ : noise.remap, diagnostics, "noise.remapZ"));
            }
            return metadata;
        }

        internal void AddBehaviors(
            JsonArray result,
            ParticleSystem system,
            bool forceOverLifetimeMapped,
            bool gravityMapped,
            bool exactNoiseMapped,
            ConversionDiagnostics diagnostics)
        {
            var velocity = system.velocityOverLifetime;
            if (velocity.enabled && CurveDiffersFrom(velocity.speedModifier, 1))
            {
                result.Add(Json.Object().Add("type", Json.String("SpeedOverLife"))
                    .Add("speed", Curve(velocity.speedModifier, diagnostics, "velocityOverLifetime.speedModifier")));
                diagnostics.mapped.Add("velocityOverLifetime.speedModifier");
            }

            var force = system.forceOverLifetime;
            var forceHasEffect = force.enabled &&
                                 (CurveHasEffect(force.x) || CurveHasEffect(force.y) || CurveHasEffect(force.z));
            if (forceHasEffect && forceOverLifetimeMapped)
            {
                diagnostics.mapped.Add("forceOverLifetime.runtimeBasis");
                diagnostics.approximated.Add("forceOverLifetime.stockOmittedFallback");
                diagnostics.warnings.Add("Force over Lifetime uses exporter basis metadata in the paired SDK runtime. Stock Quarks playback explicitly omits the force rather than applying a second emitter inverse transform.");
            }
            else if (force.enabled && !forceHasEffect)
            {
                diagnostics.inactive.Add("forceOverLifetime");
            }

            var main = system.main;
            if (CurveHasEffect(main.gravityModifier) && gravityMapped)
            {
                diagnostics.approximated.Add("main.gravityModifier");
                diagnostics.approximated.Add("main.gravityModifier.stockOmittedFallback");
                diagnostics.warnings.Add("The paired SDK applies Unity world gravity through exporter basis metadata. Integration order remains approximate; stock Quarks playback explicitly omits gravity instead of applying the emitter inverse twice.");
            }

            var noise = system.noise;
            var noiseHasEffect = noise.separateAxes
                ? CurveHasEffect(noise.strengthX) || CurveHasEffect(noise.strengthY) || CurveHasEffect(noise.strengthZ)
                : CurveHasEffect(noise.strength);
            var noiseAffectsOutput = CurveHasEffect(noise.positionAmount) ||
                                     CurveHasEffect(noise.rotationAmount) ||
                                     CurveHasEffect(noise.sizeAmount);
            if (noise.enabled && noiseHasEffect && noiseAffectsOutput)
            {
                var frequency = Mathf.Max(0.001f, noise.frequency);
                var strength = noise.separateAxes ? noise.strengthX : noise.strength;
                var dampingScale = noise.damping ? 1f / frequency : 1f;
                result.Add(Json.Object().Add("type", Json.String("Noise"))
                    .Add("frequency", Constant(frequency))
                    .Add("power", ScaleCurve(strength, dampingScale, diagnostics, "noise.strength"))
                    .Add("positionAmount", Curve(noise.positionAmount, diagnostics, "noise.positionAmount"))
                    .Add("rotationAmount", Curve(noise.rotationAmount, diagnostics, "noise.rotationAmount")));
                diagnostics.approximated.Add("noise.stockTemporalPositionFallback");
                diagnostics.approximated.Add(noise.damping ? "noise.dampedPower" : "noise.undampedPower");
                diagnostics.approximated.Add("noise.frequency.temporal");
                diagnostics.approximated.Add("noise.quality." + noise.quality + ".singleTemporalFieldFallback");
                diagnostics.warnings.Add(exactNoiseMapped
                    ? "Stock Quarks playback retains a scalar temporal position-Noise fallback. The paired SDK replaces it with the exporter-authored Unity spatial curl velocity behavior."
                    : "Unity Noise is a spatial velocity field, while stock Quarks Noise is a temporal position offset. Damped scalar strength is divided by frequency before mapping to Quarks power; the behavior remains report-labelled approximate.");
                diagnostics.warnings.Add("Unity Noise quality " + noise.quality + " controls the dimensional/coherence cost of its spatial field. Quarks exposes no 1D/2D/3D quality switch, so best-effort uses its single temporal field explicitly.");

                if (noise.separateAxes)
                {
                    diagnostics.approximated.Add("noise.separateAxes.strengthXFallback");
                    if (!exactNoiseMapped) diagnostics.unsupported.Add("noise.separateAxes");
                    diagnostics.warnings.Add(exactNoiseMapped
                        ? "The paired SDK applies independent Unity X/Y/Z Strength curves to the curl field. Stock Quarks fallback uses strengthX as one scalar power and can introduce motion on an authored zero-strength axis."
                        : "Stock Quarks Noise has one scalar power generator; best-effort uses Unity strengthX and reports the omitted Y/Z distinction.");
                }
                if (noise.octaveCount != 1)
                {
                    if (!exactNoiseMapped) diagnostics.unsupported.Add("noise.octaves");
                    diagnostics.approximated.Add("noise.octaves.singleOctaveFallback");
                    diagnostics.warnings.Add(exactNoiseMapped
                        ? "The paired SDK accumulates Unity Noise octaves. Stock Quarks fallback retains one temporal field."
                        : "Unity Noise octaveCount is active. Best-effort uses Quarks' single noise field; strict export fails.");
                }
                if (CurveHasEffect(noise.scrollSpeed))
                {
                    diagnostics.approximated.Add("noise.scrollSpeed.omittedFallback");
                    if (!exactNoiseMapped) diagnostics.nonBlockingUnsupported.Add("noise.scrollSpeed");
                    diagnostics.warnings.Add(exactNoiseMapped
                        ? "The paired SDK advances Unity's spatial Noise field from the authored scrollSpeed curve. Stock Quarks fallback omits independent field scrolling."
                        : "Unity Noise scrollSpeed moves a spatial field; stock Quarks Noise has no independent spatial scroll parameter. The omission is explicit but does not block strict export.");
                }
                if (noise.remapEnabled)
                {
                    diagnostics.mapped.Add("noise.remap.runtime");
                    diagnostics.approximated.Add("noise.remap.lutEquivalent");
                    diagnostics.warnings.Add("The paired SDK remaps each Unity Noise axis before applying position influence. Stock Quarks fallback omits the remap.");
                }
                if (CurveHasEffect(noise.rotationAmount))
                {
                    diagnostics.mapped.Add("noise.rotationAmount");
                    diagnostics.approximated.Add("noise.rotationAmount.temporalFallback");
                    diagnostics.warnings.Add("Quarks applies the authored rotation influence to its temporal Noise field. Unity uses a spatial Noise field, so the rotation influence is preserved but remains algorithmically approximate.");
                }
                if (CurveHasEffect(noise.sizeAmount))
                {
                    diagnostics.unsupported.Add("noise.sizeAmount");
                    diagnostics.approximated.Add("noise.sizeAmount.omittedFallback");
                    diagnostics.warnings.Add("Unity Noise sizeAmount is active. Best-effort explicitly omits size noise; strict export fails.");
                }
            }
            else if (noise.enabled)
            {
                diagnostics.inactive.Add("noise");
            }

            var limit = system.limitVelocityOverLifetime;
            if (limit.enabled)
            {
                if (limit.separateAxes)
                {
                    diagnostics.mapped.Add("limitVelocityOverLifetime.separateAxes.runtime");
                    diagnostics.approximated.Add("limitVelocityOverLifetime.separateAxes.stockOmittedFallback");
                    diagnostics.warnings.Add("Separate-axis Limit Velocity is preserved by the paired SDK runtime; stock Quarks omits the axis-specific limit.");
                    result.Add(Json.Object().Add("type", Json.String("LimitSpeedOverLife"))
                        .Add("speed", Json.Number(0))
                        .Add("dampen", Json.Number(limit.dampen)));
                }
                else
                {
                    result.Add(Json.Object().Add("type", Json.String("LimitSpeedOverLife"))
                        .Add("speed", LimitVelocityStockFallback(limit.limit, diagnostics))
                        .Add("dampen", Json.Number(limit.dampen)));
                    diagnostics.mapped.Add("limitVelocityOverLifetime.scalar");
                }

                if (CurveHasEffect(limit.drag))
                {
                    diagnostics.mapped.Add("limitVelocityOverLifetime.drag.runtime");
                    diagnostics.approximated.Add("limitVelocityOverLifetime.drag.stockOmittedFallback");
                    diagnostics.warnings.Add("Limit Velocity drag is preserved in exporter metadata and evaluated by the paired SDK with Unity's area and velocity-dependent drag formula. Stock Quarks playback omits drag rather than applying a different scalar limit.");
                }
                else
                {
                    diagnostics.inactive.Add("limitVelocityOverLifetime.drag");
                }
            }
        }
    }
}
