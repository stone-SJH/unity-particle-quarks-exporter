using System.Collections.Generic;
using UnityEngine;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class UrpParticleUnlitShaderProfile : ShaderProfile
    {
        private static readonly string[] Names = { "Universal Render Pipeline/Particles/Unlit" };
        public override string Name => "UrpParticleUnlit";
        public override string DiagnosticId => "urp.particleUnlit";
        public override IReadOnlyList<string> ShaderNames => Names;
        public override bool ConsumesParticleColor => true;
        public override bool FixedTransparent => true;
        public override ShaderProfileConversionKind ConversionKind => ShaderProfileConversionKind.UnlitParticle;
        public override bool SupportsParticleColorMode => true;
        public override string GetProfileId(Material material) => "urp.particleUnlit";
        public override string[] GetPropertyAliases(Material material) => new[] { "_BaseMap", "_BaseColor", "_MainTex", "_Color", "_EmissionMap", "_EmissionColor", "_AlphaClip", "_Cutoff", "_SoftParticlesEnabled" };
    }
}
