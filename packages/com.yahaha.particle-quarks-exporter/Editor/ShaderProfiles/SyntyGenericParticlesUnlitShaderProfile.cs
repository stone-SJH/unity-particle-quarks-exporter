using System.Collections.Generic;
using UnityEngine;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class SyntyGenericParticlesUnlitShaderProfile : ShaderProfile
    {
        private static readonly string[] Names = { "Synty/Generic_ParticlesUnlit" };
        public override string Name => "SyntyGenericParticlesUnlit";
        public override string DiagnosticId => "synty.genericParticlesUnlit";
        public override IReadOnlyList<string> ShaderNames => Names;
        public override bool ConsumesParticleColor => true;
        public override bool FixedTransparent => true;
        public override bool DoubleSidedByDefault => true;
        public override bool UsesSyntyPipelineCull => true;
        public override ShaderProfileConversionKind ConversionKind => ShaderProfileConversionKind.UnlitParticle;
        public override bool SupportsParticleColorMode => true;
        public override string UnlitNormalMapProperty => "_Normal_Map";
        public override IReadOnlyList<string> PreferredMainTextureProperties => new[] { "_Albedo_Map" };

        public override bool TryResolveSoftParticleSettings(
            Material material,
            ConversionDiagnostics diagnostics,
            out ShaderProfileSoftParticleSettings settings)
        {
            settings = null;
            if (!HasEnabledFloat(material, "_Enable_Soft_Particles")) return false;
            var far = material != null && material.HasProperty("_Soft_Distance")
                ? Mathf.Max(0.000001f, material.GetFloat("_Soft_Distance"))
                : 1;
            diagnostics.mapped.Add("material.softParticles.syntyDistance");
            if (material != null && material.HasProperty("_Soft_Power") &&
                Mathf.Abs(material.GetFloat("_Soft_Power") - 1) > 0.000001f)
            {
                diagnostics.approximated.Add("material.softParticles.syntyPowerLinearFallback");
                diagnostics.warnings.Add("The Synty Generic Particles shader exposes a soft-particle power curve. The exporter maps the fade distance and uses the existing linear soft-particle runtime fallback.");
            }
            settings = new ShaderProfileSoftParticleSettings { far = far };
            return true;
        }

        public override bool TryResolveCameraFadeSettings(
            Material material,
            ConversionDiagnostics diagnostics,
            out ShaderProfileCameraFadeSettings settings)
        {
            settings = null;
            if (!HasEnabledFloat(material, "_Enable_Camera_Fade") &&
                !HasEnabledFloat(material, "_CameraFadingEnabled")) return false;
            var near = material != null && material.HasProperty("_Camera_Fade_Near")
                ? Mathf.Max(0, material.GetFloat("_Camera_Fade_Near"))
                : 0;
            var far = material != null && material.HasProperty("_Camera_Fade_Far")
                ? Mathf.Max(near + 0.000001f, material.GetFloat("_Camera_Fade_Far"))
                : Mathf.Max(near + 0.000001f, 1);
            var smoothness = material != null && material.HasProperty("_Camera_Fade_Smoothness")
                ? Mathf.Max(0.000001f, material.GetFloat("_Camera_Fade_Smoothness"))
                : 1;
            diagnostics.mapped.Add("material.synty.cameraFade.runtime");
            diagnostics.approximated.Add("material.synty.cameraFade.stockOmittedFallback");
            diagnostics.warnings.Add("Synty Generic particle camera fade is exported as near/far/smoothness metadata and applied by the paired shader patch using camera-relative particle distance. Stock Quarks playback omits the fade.");
            settings = new ShaderProfileCameraFadeSettings { near = near, far = far, smoothness = smoothness };
            return true;
        }

        public override bool SuppressCameraFadeToggleDiagnostic(Material material)
        {
            return HasEnabledFloat(material, "_Enable_Camera_Fade") || HasEnabledFloat(material, "_CameraFadingEnabled");
        }

        public override void DiagnoseMaterialFeatures(Material material, bool litMaterial, ConversionDiagnostics diagnostics)
        {
            base.DiagnoseMaterialFeatures(material, litMaterial, diagnostics);
            if (material == null) return;
            if (HasEnabledFloat(material, "_Use_View_Edge_Compensation"))
            {
                diagnostics.nonBlockingUnsupported.Add("material.synty.viewEdgeCompensation");
                diagnostics.approximated.Add("material.synty.viewEdgeCompensation.stockFallback");
                diagnostics.warnings.Add("Synty Generic_ParticlesUnlit view-edge compensation is omitted by the stock Quarks shader. The profile keeps texture, tint, vertex color, alpha clip, blend, culling, and soft-particle distance behavior and records this visual-only omission explicitly.");
            }
            if (HasEnabledFloat(material, "_Enable_Scene_Fog"))
            {
                diagnostics.unsupported.Add("material.synty.sceneFog");
                diagnostics.approximated.Add("material.synty.sceneFog.omittedFallback");
                diagnostics.warnings.Add("Synty Generic_ParticlesUnlit scene fog is environment-renderer state, not an effect-local Quarks material property. Best-effort omits it; strict export fails.");
            }
        }
    }
}
