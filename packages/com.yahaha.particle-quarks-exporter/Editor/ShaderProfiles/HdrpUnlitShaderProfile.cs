using System.Collections.Generic;
using UnityEngine;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class HdrpUnlitShaderProfile : ShaderProfile
    {
        private static readonly string[] Names = { "HDRP/Unlit" };
        public override string Name => "HdrpUnlit";
        public override string DiagnosticId => "hdrp.unlit";
        public override IReadOnlyList<string> ShaderNames => Names;
        public override ShaderProfileConversionKind ConversionKind => ShaderProfileConversionKind.UnlitNoVertexColor;
        public override IReadOnlyList<string> PreferredMainTextureProperties => new[] { "_UnlitColorMap" };

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
