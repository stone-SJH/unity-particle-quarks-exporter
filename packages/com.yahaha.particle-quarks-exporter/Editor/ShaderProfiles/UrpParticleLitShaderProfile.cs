using System.Collections.Generic;
using UnityEngine;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class UrpParticleLitShaderProfile : ShaderProfile
    {
        private static readonly string[] Names = { "Universal Render Pipeline/Particles/Lit" };
        public override string Name => "UrpParticleLit";
        public override string DiagnosticId => "urp.particleLit";
        public override IReadOnlyList<string> ShaderNames => Names;
        public override bool UsesLitMaterial => true;
        public override bool ConsumesParticleColor => true;
        public override bool FixedTransparent => true;
        public override bool SupportsPackedMetallicGloss => true;
        public override bool SupportsParticleColorMode => true;
        public override string GetProfileId(Material material) => "urp.particleLit";
        public override string[] GetPropertyAliases(Material material) => new[] { "_BaseMap", "_BaseColor", "_BumpMap", "_MetallicGlossMap", "_EmissionMap", "_EmissionColor", "_AlphaClip", "_Cutoff", "_SoftParticlesEnabled" };
        public override float ResolvePackedSmoothness(Material material)
        {
            return material != null && material.HasProperty("_Smoothness")
                ? Mathf.Clamp01(material.GetFloat("_Smoothness"))
                : 1;
        }
    }
}
