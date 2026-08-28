using System.Collections.Generic;
using UnityEngine;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class BuiltInStandardMetallicShaderProfile : ShaderProfile
    {
        private static readonly string[] Names = { "Standard" };
        public override string Name => "BuiltInStandardMetallic";
        public override string DiagnosticId => "builtin.standardMetallic";
        public override IReadOnlyList<string> ShaderNames => Names;
        public override bool UsesLitMaterial => true;
        public override bool SupportsPackedMetallicGloss => true;

        public override void ConfigureMaterial(ShaderProfileMaterialContext context)
        {
            ConfigureAlphaTest(context);
        }

        internal static void ConfigureAlphaTest(ShaderProfileMaterialContext context)
        {
            var material = context.material;
            if (material == null || !material.HasProperty("_Cutoff")) return;
            var cutoutMode = material.HasProperty("_Mode") &&
                             Mathf.RoundToInt(material.GetFloat("_Mode")) == 1;
            if (cutoutMode || material.IsKeywordEnabled("_ALPHATEST_ON"))
                context.alphaTestOverride = material.GetFloat("_Cutoff");
        }
    }
}
