using System.Collections.Generic;
using UnityEngine;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class BuiltInParticleVertexLitShaderProfile : ShaderProfile
    {
        private static readonly string[] Names =
        {
            "Legacy Shaders/Particles/VertexLit Blended",
            "Mobile/Particles/VertexLit Blended"
        };

        public override string Name => "BuiltInParticleVertexLit";
        public override string DiagnosticId => "builtin.particleVertexLit";
        public override IReadOnlyList<string> ShaderNames => Names;
        public override bool UsesLitMaterial => true;
        public override bool ConsumesParticleColor => true;
        public override bool FixedTransparent => true;
        public override bool DoubleSidedByDefault => true;
        public override ShaderProfileConversionKind ConversionKind => ShaderProfileConversionKind.VertexLit;
        public override bool SupportsParticleColorMode => true;
        public override IReadOnlyList<string> PreferredMainTextureProperties => new[] { "_MainTex" };

        public override string GetProfileId(Material material)
        {
            var shaderName = material == null || material.shader == null ? string.Empty : material.shader.name;
            return ShaderNameEquals(shaderName, "Mobile/Particles/VertexLit Blended")
                ? "builtin.mobileParticleVertexLit"
                : "builtin.particleVertexLit";
        }

        public override string[] GetPropertyAliases(Material material)
        {
            return string.IsNullOrEmpty(GetProfileId(material))
                ? base.GetPropertyAliases(material)
                : new[] { "_MainTex", "_Color", "_EmisColor" };
        }

        public override void ConfigureMaterial(ShaderProfileMaterialContext context)
        {
            if (context.material == null || !context.material.HasProperty("_EmisColor")) return;
            context.materialEmissionOverride = context.material.GetColor("_EmisColor");
            context.diagnostics.mapped.Add("material.emissive.legacyVertexLit");
        }
    }
}
