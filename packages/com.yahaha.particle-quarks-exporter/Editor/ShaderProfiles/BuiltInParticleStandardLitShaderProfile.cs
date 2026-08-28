using System.Collections.Generic;
using UnityEngine;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class BuiltInParticleStandardLitShaderProfile : ShaderProfile
    {
        private static readonly string[] Names = { "Particles/Standard Surface" };
        public override string Name => "BuiltInParticleStandardLit";
        public override string DiagnosticId => "builtin.particleStandardLit";
        public override IReadOnlyList<string> ShaderNames => Names;
        public override bool UsesLitMaterial => true;
        public override bool ConsumesParticleColor => true;
        public override bool FixedTransparent => true;
        public override bool SupportsPackedMetallicGloss => true;
        public override bool SupportsParticleColorMode => true;
        public override string GetProfileId(Material material) => "builtin.particlesStandardSurface";
        public override string[] GetPropertyAliases(Material material) => new[] { "_MainTex", "_Color", "_BumpMap", "_MetallicGlossMap", "_EmissionMap", "_Cutoff" };
    }
}
