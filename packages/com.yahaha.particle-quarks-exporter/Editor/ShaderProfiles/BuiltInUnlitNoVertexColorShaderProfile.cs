using System.Collections.Generic;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class BuiltInUnlitNoVertexColorShaderProfile : ShaderProfile
    {
        private static readonly string[] Names =
        {
            "Unlit/Color",
            "Unlit/Texture",
            "Unlit/Transparent",
            "Unlit/Transparent Cutout"
        };
        public override string Name => "BuiltInUnlitNoVertexColor";
        public override string DiagnosticId => "builtin.unlitNoVertexColor";
        public override IReadOnlyList<string> ShaderNames => Names;
        public override ShaderProfileConversionKind ConversionKind => ShaderProfileConversionKind.UnlitNoVertexColor;
    }
}
