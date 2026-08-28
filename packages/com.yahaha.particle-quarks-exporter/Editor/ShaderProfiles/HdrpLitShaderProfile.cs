using System.Collections.Generic;
using UnityEngine;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class HdrpLitShaderProfile : ShaderProfile
    {
        private static readonly string[] Names = { "HDRP/Lit" };
        public override string Name => "HdrpLit";
        public override string DiagnosticId => "hdrp.lit";
        public override IReadOnlyList<string> ShaderNames => Names;
        public override bool UsesLitMaterial => true;
        public override IReadOnlyList<string> PreferredMainTextureProperties => new[] { "_BaseColorMap" };

        public override ShaderProfileLitMapSettings GetLitMapSettings(Material material)
        {
            return new ShaderProfileLitMapSettings
            {
                normalMapProperty = "_NormalMap",
                normalScaleProperty = "_NormalScale",
                objectSpaceNormal = material != null && material.HasProperty("_NormalMapSpace") &&
                                    Mathf.RoundToInt(material.GetFloat("_NormalMapSpace")) != 0,
                emissionMapProperty = "_EmissiveColorMap",
                emissionMapActive = material != null && material.HasProperty("_EmissiveColorMap") &&
                                    material.GetTexture("_EmissiveColorMap") != null
            };
        }

        public override void DiagnoseMaterialFeatures(Material material, bool litMaterial, ConversionDiagnostics diagnostics)
        {
            if (material == null || !material.HasProperty("_EmissiveExposureWeight") ||
                Mathf.Abs(material.GetFloat("_EmissiveExposureWeight") - 1) <= 0.000001f) return;
            diagnostics.unsupported.Add("material.hdrpEmissiveExposureWeight");
            diagnostics.approximated.Add("material.hdrpEmissiveExposureWeight.directColorFallback");
            diagnostics.warnings.Add("HDRP emissive exposure weighting is camera/exposure dependent. Best-effort uses the serialized final emissive color directly; strict export fails.");
        }
    }
}
