using System.Collections.Generic;
using UnityEngine;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class UrpUnlitShaderProfile : ShaderProfile
    {
        private static readonly string[] Names = { "Universal Render Pipeline/Unlit" };
        public override string Name => "UrpUnlit";
        public override string DiagnosticId => "urp.unlit";
        public override IReadOnlyList<string> ShaderNames => Names;
        public override ShaderProfileConversionKind ConversionKind => ShaderProfileConversionKind.UnlitNoVertexColor;
        public override string GetProfileId(Material material) => "urp.unlit";
        public override string[] GetPropertyAliases(Material material) => new[] { "_BaseMap", "_BaseColor", "_MainTex", "_Color", "_Cutoff" };
    }
}
