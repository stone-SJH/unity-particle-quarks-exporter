using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class BuiltInParticleUnlitShaderProfile : ShaderProfile
    {
        private static readonly string[] Names =
        {
            "Legacy Shaders/Particles/Additive",
            "Legacy Shaders/Particles/Additive (Soft)",
            "Legacy Shaders/Particles/Alpha Blended",
            "Legacy Shaders/Particles/Alpha Blended Premultiply",
            "Legacy Shaders/Particles/Multiply",
            "Legacy Shaders/Particles/Multiply (Double)",
            "Mobile/Particles/Additive",
            "Mobile/Particles/Alpha Blended",
            "Mobile/Particles/Multiply",
            "Particles/Standard Unlit"
        };

        public override string Name => "BuiltInParticleUnlit";
        public override string DiagnosticId => "builtin.particleUnlit";
        public override IReadOnlyList<string> ShaderNames => Names;
        public override bool ConsumesParticleColor => true;
        public override bool FixedTransparent => true;
        public override bool DoubleSidedByDefault => true;
        public override ShaderProfileConversionKind ConversionKind => ShaderProfileConversionKind.UnlitParticle;
        public override bool SupportsParticleColorMode => true;
        public override IReadOnlyList<string> PreferredMainTextureProperties => new[] { "_MainTex" };

        public override string GetProfileId(Material material)
        {
            var shaderName = material == null || material.shader == null ? string.Empty : material.shader.name;
            if (ShaderNameEquals(shaderName, "Legacy Shaders/Particles/Alpha Blended")) return "builtin.particleAlphaBlended";
            if (ShaderNameEquals(shaderName, "Legacy Shaders/Particles/Additive")) return "builtin.particleAdditive";
            if (ShaderNameEquals(shaderName, "Legacy Shaders/Particles/Multiply")) return "builtin.particleMultiply";
            if (ShaderNameEquals(shaderName, "Legacy Shaders/Particles/Additive (Soft)")) return "builtin.particleAdditiveSoft";
            if (ShaderNameEquals(shaderName, "Legacy Shaders/Particles/Alpha Blended Premultiply")) return "builtin.particleAlphaBlendedPremultiply";
            if (ShaderNameEquals(shaderName, "Legacy Shaders/Particles/Multiply (Double)")) return "builtin.particleMultiplyDouble";
            if (ShaderNameEquals(shaderName, "Mobile/Particles/Alpha Blended")) return "builtin.mobileParticleAlphaBlended";
            if (ShaderNameEquals(shaderName, "Mobile/Particles/Additive")) return "builtin.mobileParticleAdditive";
            if (ShaderNameEquals(shaderName, "Mobile/Particles/Multiply")) return "builtin.mobileParticleMultiply";
            if (ShaderNameEquals(shaderName, "Particles/Standard Unlit")) return "builtin.particlesStandardUnlit";
            return string.Empty;
        }

        public override string[] GetPropertyAliases(Material material)
        {
            switch (GetProfileId(material))
            {
                case "builtin.particleAlphaBlended":
                case "builtin.mobileParticleAlphaBlended":
                    return new[] { "_MainTex", "_TintColor", "_InvFade" };
                case "builtin.particleAdditiveSoft":
                    return new[] { "_MainTex", "_TintColor", "_InvFade" };
                case "builtin.particlesStandardUnlit":
                    return new[] { "_MainTex", "_Color", "_TintColor", "_EmissionColor", "_Cutoff" };
                default:
                    return new[] { "_MainTex", "_TintColor" };
            }
        }

        public override void ConfigureMaterial(ShaderProfileMaterialContext context)
        {
            var material = context.material;
            if (material == null || material.shader == null) return;
            var shaderName = material.shader.name ?? string.Empty;
            if (shaderName.StartsWith("Legacy Shaders/Particles/", StringComparison.OrdinalIgnoreCase) &&
                material.HasProperty("_TintColor"))
            {
                context.materialColorOverride = material.GetColor("_TintColor") * 2;
                context.diagnostics.mapped.Add("material.tintColor.legacyDouble");
            }

            var blend = new MaterialBlendState();
            if (ShaderNameEquals(shaderName, "Legacy Shaders/Particles/Additive") ||
                ShaderNameEquals(shaderName, "Mobile/Particles/Additive"))
            {
                blend.blending = 2;
                context.blendStateOverride = blend;
            }
            else if (ShaderNameEquals(shaderName, "Legacy Shaders/Particles/Additive (Soft)"))
            {
                SetCustomBlend(blend, 201, 203, 100, "legacySoftAdditive");
                blend.sourcePremultipliedAlpha = true;
                context.blendStateOverride = blend;
            }
            else if (ShaderNameEquals(shaderName, "Legacy Shaders/Particles/Alpha Blended Premultiply"))
            {
                SetCustomBlend(blend, 201, 205, 100, "legacyAlphaPremultiply");
                blend.sourcePremultipliedAlpha = true;
                context.blendStateOverride = blend;
            }
            else if (ShaderNameEquals(shaderName, "Legacy Shaders/Particles/Multiply") ||
                     ShaderNameEquals(shaderName, "Mobile/Particles/Multiply"))
            {
                SetCustomBlend(blend, 200, 202, 100, "legacyMultiply");
                context.blendStateOverride = blend;
            }
            else if (ShaderNameEquals(shaderName, "Legacy Shaders/Particles/Multiply (Double)"))
            {
                SetCustomBlend(blend, 208, 202, 100, "legacyMultiplyDouble");
                context.blendStateOverride = blend;
            }
        }
    }
}
