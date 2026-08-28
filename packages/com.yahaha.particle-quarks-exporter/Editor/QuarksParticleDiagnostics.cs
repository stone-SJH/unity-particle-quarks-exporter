using UnityEngine;
using static UnityParticleQuarksExporter.Editor.QuarksParticleSemanticsUtility;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class QuarksParticleDiagnostics
    {
        private readonly bool presentationTarget;

        internal QuarksParticleDiagnostics(bool isPresentationTarget)
        {
            presentationTarget = isPresentationTarget;
        }

        internal void DiagnoseUnsupportedModules(
            ParticleSystem system,
            bool linearVelocityMapped,
            bool customDataMapped,
            ConversionDiagnostics diagnostics)
        {
            var velocity = system.velocityOverLifetime;
            if (velocity.enabled)
            {
                var linearHasEffect = CurveHasEffect(velocity.x) || CurveHasEffect(velocity.y) || CurveHasEffect(velocity.z);
                var orbitalHasEffect = CurveHasEffect(velocity.orbitalX) || CurveHasEffect(velocity.orbitalY) || CurveHasEffect(velocity.orbitalZ);
                var offsetHasEffect = CurveHasEffect(velocity.orbitalOffsetX) || CurveHasEffect(velocity.orbitalOffsetY) || CurveHasEffect(velocity.orbitalOffsetZ);
                var radialHasEffect = CurveHasEffect(velocity.radial);
                var speedModifierHasEffect = CurveDiffersFrom(velocity.speedModifier, 1);
                if (linearHasEffect && !linearVelocityMapped)
                {
                    diagnostics.unsupported.Add(velocity.space == ParticleSystemSimulationSpace.Custom
                        ? "velocityOverLifetime.customSimulationSpace"
                        : "velocityOverLifetime.linear");
                }
                if (linearHasEffect && !linearVelocityMapped)
                    diagnostics.approximated.Add("velocityOverLifetime.linear.omittedFallback");
                if (orbitalHasEffect)
                {
                    if (velocity.space == ParticleSystemSimulationSpace.Custom || system.main.simulationSpace == ParticleSystemSimulationSpace.Custom)
                    {
                        diagnostics.unsupported.Add("velocityOverLifetime.orbital.customSimulationSpace");
                        diagnostics.approximated.Add("velocityOverLifetime.orbital.customSimulationSpace.omittedFallback");
                    }
                    else
                    {
                        diagnostics.mapped.Add("velocityOverLifetime.orbital.runtime");
                        diagnostics.approximated.Add("velocityOverLifetime.orbital.stockOmittedFallback");
                    }
                }
                if (offsetHasEffect)
                {
                    if (velocity.space == ParticleSystemSimulationSpace.Custom || system.main.simulationSpace == ParticleSystemSimulationSpace.Custom)
                    {
                        diagnostics.unsupported.Add("velocityOverLifetime.orbitalOffset.customSimulationSpace");
                        diagnostics.approximated.Add("velocityOverLifetime.orbitalOffset.customSimulationSpace.omittedFallback");
                    }
                    else
                    {
                        diagnostics.mapped.Add("velocityOverLifetime.orbitalOffset.runtime");
                        diagnostics.approximated.Add("velocityOverLifetime.orbitalOffset.stockOmittedFallback");
                    }
                }
                if (radialHasEffect)
                {
                    if (velocity.space == ParticleSystemSimulationSpace.Custom || system.main.simulationSpace == ParticleSystemSimulationSpace.Custom)
                    {
                        diagnostics.unsupported.Add("velocityOverLifetime.radial.customSimulationSpace");
                        diagnostics.approximated.Add("velocityOverLifetime.radial.customSimulationSpace.omittedFallback");
                    }
                    else
                    {
                        diagnostics.mapped.Add("velocityOverLifetime.radial.runtime");
                        diagnostics.approximated.Add("velocityOverLifetime.radial.stockOmittedFallback");
                    }
                }
                if (orbitalHasEffect || offsetHasEffect || radialHasEffect || (linearHasEffect && !linearVelocityMapped))
                {
                    diagnostics.warnings.Add("Velocity over Lifetime orbital, radial, and offset semantics use the paired SDK compatibility behavior when their module and particle spaces are representable; custom simulation spaces remain unsupported.");
                }
                if (!linearHasEffect && !orbitalHasEffect && !offsetHasEffect && !radialHasEffect && !speedModifierHasEffect)
                {
                    diagnostics.inactive.Add("velocityOverLifetime");
                }
            }
            if (system.collision.enabled) ReportPhysicsCollisionFeature("collision", diagnostics);
            if (system.trigger.enabled) ReportPhysicsCollisionFeature("trigger", diagnostics);
            if (system.externalForces.enabled && system.externalForces.multiplier != 0)
                ReportOmittedModule("externalForces", diagnostics);
            if (system.customData.enabled &&
                (system.customData.GetMode(ParticleSystemCustomData.Custom1) != ParticleSystemCustomDataMode.Disabled ||
                 system.customData.GetMode(ParticleSystemCustomData.Custom2) != ParticleSystemCustomDataMode.Disabled))
            {
                if (customDataMapped)
                {
                    diagnostics.mapped.Add("customData.runtime");
                    diagnostics.approximated.Add("customData.stockOmittedFallback");
                    diagnostics.warnings.Add("ParticleSystem Custom Data is preserved for the paired shader profile. Stock Quarks playback omits the custom vertex streams.");
                }
                else
                {
                    ReportOmittedModule("customData", diagnostics);
                }
            }
        }

        private static void ReportOmittedModule(string field, ConversionDiagnostics diagnostics)
        {
            diagnostics.unsupported.Add(field);
            diagnostics.approximated.Add(field + ".omittedFallback");
            diagnostics.warnings.Add(field + " is active and has no mapped Quarks/runtime implementation. Best-effort explicitly omits the module; strict export fails.");
        }

        internal void ReportPhysicsCollisionFeature(string field, ConversionDiagnostics diagnostics)
        {
            diagnostics.unsupported.Add(field);
            if (presentationTarget)
            {
                diagnostics.approximated.Add(field + ".omittedFallback");
                diagnostics.warnings.Add(field + " is intentionally omitted for the presentation target. The base particle simulation remains playable, but Unity collision/trigger response is not reproduced and the effect is marked partial.");
                return;
            }
            diagnostics.fatalUnsupported.Add(field);
            diagnostics.warnings.Add(field + " depends on Unity ParticleSystem physics collision or trigger events. automatic VFX export does not support particle physics collision behavior in strict or best-effort mode; author the visible result as a target-runtime simulation or a collision-free VFX variant instead.");
        }

        internal static void DiagnoseProjectColorSpace(ConversionDiagnostics diagnostics)
        {
            if (QualitySettings.activeColorSpace == ColorSpace.Gamma)
            {
                diagnostics.mapped.Add("colorSpace.gammaPassThrough");
                diagnostics.warnings.Add("Unity Gamma particle colors, material tint, and texture bytes use raw pass-through. Exported textures use Three NoColorSpace and the paired SDK restores floating-point material RGBA because Quarks 0.17.1 particle_frag has no colorspace_fragment.");
            }
            else
            {
                diagnostics.mapped.Add("colorSpace.linearProject.directConversion");
                diagnostics.mapped.Add("colorSpace.linearProject.textureImporterTags");
            }
        }
    }
}
