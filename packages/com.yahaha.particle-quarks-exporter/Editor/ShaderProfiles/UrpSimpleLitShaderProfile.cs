using System.Collections.Generic;
using UnityEngine;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class UrpSimpleLitShaderProfile : ShaderProfile
    {
        private static readonly string[] Names = { "Universal Render Pipeline/Simple Lit" };
        public override string Name => "UrpSimpleLit";
        public override string DiagnosticId => "urp.simpleLit";
        public override IReadOnlyList<string> ShaderNames => Names;
        public override bool UsesLitMaterial => true;
        public override string GetProfileId(Material material) => "urp.simpleLit";
        public override string[] GetPropertyAliases(Material material) => new[] { "_BaseMap", "_BaseColor", "_BumpMap", "_MetallicGlossMap", "_EmissionMap", "_EmissionColor", "_AlphaClip", "_Cutoff", "_SoftParticlesEnabled" };
    }
}
