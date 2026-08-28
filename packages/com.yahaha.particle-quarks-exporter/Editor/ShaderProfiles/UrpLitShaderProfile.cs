using System.Collections.Generic;
using UnityEngine;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class UrpLitShaderProfile : ShaderProfile
    {
        private static readonly string[] Names = { "Universal Render Pipeline/Lit" };
        public override string Name => "UrpLit";
        public override string DiagnosticId => "urp.lit";
        public override IReadOnlyList<string> ShaderNames => Names;
        public override bool UsesLitMaterial => true;
        public override bool SupportsPackedMetallicGloss => true;
        public override string GetProfileId(Material material) => "urp.lit";
        public override string[] GetPropertyAliases(Material material) => new[] { "_BaseMap", "_BaseColor", "_BumpMap", "_MetallicGlossMap", "_EmissionMap", "_EmissionColor", "_AlphaClip", "_Cutoff", "_SoftParticlesEnabled" };
        public override float ResolvePackedSmoothness(Material material)
        {
            return material != null && material.HasProperty("_Smoothness")
                ? Mathf.Clamp01(material.GetFloat("_Smoothness"))
                : 1;
        }
    }
}
