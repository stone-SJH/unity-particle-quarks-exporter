using System.Collections.Generic;
using UnityEngine;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class UrpParticleSimpleLitShaderProfile : ShaderProfile
    {
        private static readonly string[] Names = { "Universal Render Pipeline/Particles/Simple Lit" };
        public override string Name => "UrpParticleSimpleLit";
        public override string DiagnosticId => "urp.particleSimpleLit";
        public override IReadOnlyList<string> ShaderNames => Names;
        public override bool UsesLitMaterial => true;
        public override bool ConsumesParticleColor => true;
        public override bool FixedTransparent => true;
        public override bool SupportsParticleColorMode => true;
        public override string GetProfileId(Material material) => "urp.particleSimpleLit";
        public override string[] GetPropertyAliases(Material material) => new[] { "_BaseMap", "_BaseColor", "_BumpMap", "_MetallicGlossMap", "_EmissionMap", "_EmissionColor", "_AlphaClip", "_Cutoff", "_SoftParticlesEnabled" };
    }
}
