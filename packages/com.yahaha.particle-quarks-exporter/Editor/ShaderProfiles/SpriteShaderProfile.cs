using System.Collections.Generic;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class SpriteShaderProfile : ShaderProfile
    {
        private static readonly string[] Names = { "Sprites/Default" };
        public override string Name => "Sprite";
        public override string DiagnosticId => "builtin.sprite";
        public override IReadOnlyList<string> ShaderNames => Names;
        public override bool ConsumesParticleColor => true;
        public override bool FixedTransparent => true;
        public override bool DoubleSidedByDefault => true;
        public override ShaderProfileConversionKind ConversionKind => ShaderProfileConversionKind.UnlitParticle;
    }
}
