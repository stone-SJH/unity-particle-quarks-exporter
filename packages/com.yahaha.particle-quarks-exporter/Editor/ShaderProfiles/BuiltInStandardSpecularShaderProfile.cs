using System.Collections.Generic;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class BuiltInStandardSpecularShaderProfile : ShaderProfile
    {
        private static readonly string[] Names = { "Standard (Specular setup)" };
        public override string Name => "BuiltInStandardSpecular";
        public override string DiagnosticId => "builtin.standardSpecular";
        public override IReadOnlyList<string> ShaderNames => Names;
        public override bool UsesLitMaterial => true;
        public override bool SpecularWorkflow => true;

        public override void ConfigureMaterial(ShaderProfileMaterialContext context)
        {
            BuiltInStandardMetallicShaderProfile.ConfigureAlphaTest(context);
        }
    }
}
