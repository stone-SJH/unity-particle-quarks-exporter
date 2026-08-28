using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class BuiltInParticleAnimAlphaBlendedShaderProfile : ShaderProfile
    {
        private static readonly string[] Names = { "Legacy Shaders/Particles/Anim Alpha Blended" };

        public override string Name => "BuiltInParticleAnimAlphaBlended";
        public override string DiagnosticId => "builtin.particleAnimAlphaBlended";
        public override IReadOnlyList<string> ShaderNames => Names;
        public override bool ConsumesParticleColor => true;
        public override bool FixedTransparent => true;
        public override bool DoubleSidedByDefault => true;
        public override ShaderProfileConversionKind ConversionKind => ShaderProfileConversionKind.UnlitParticle;
        public override bool SupportsParticleColorMode => true;
        public override IReadOnlyList<string> PreferredMainTextureProperties => new[] { "_MainTex" };
        public override string GetProfileId(Material material) => "builtin.particleAnimAlphaBlended";
        public override string[] GetPropertyAliases(Material material) => new[] { "_MainTex", "_TintColor", "_InvFade" };

        public override void ConfigureMaterial(ShaderProfileMaterialContext context)
        {
            if (context.material == null || !context.material.HasProperty("_TintColor")) return;
            context.materialColorOverride = context.material.GetColor("_TintColor") * 2;
            context.diagnostics.mapped.Add("material.tintColor.legacyDouble");
        }
    }
}
