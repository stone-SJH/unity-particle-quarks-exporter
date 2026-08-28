using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class CustomHovlParticlesShaderProfile : ShaderProfile
    {
        private static readonly string[] Names =
        {
            "Hovl/Particles/Add_CenterGlow",
            "Hovl/Particles/Add_Fresnel",
            "Hovl/Particles/Blend_CenterGlow",
            "Hovl/Particles/Blend_TwoSides",
            "Hovl/Particles/BlendDistort",
            "Hovl/Particles/DissolveNoise",
            "Hovl/Particles/Distortion",
            "Hovl/Particles/Explosion",
            "Hovl/Particles/Ice",
            "Hovl/Particles/Lightning",
            "Hovl/Particles/Scroll",
            "Hovl/Particles/SwordSlash"
        };

        public override string Name => "CustomHovlParticles";
        public override string DiagnosticId => "custom.hovl.particles";
        public override IReadOnlyList<string> ShaderNames => Names;
        public override bool ConsumesParticleColor => true;
        public override bool FixedTransparent => true;
        public override bool CustomParticle => true;
        public override ShaderProfileConversionKind ConversionKind => ShaderProfileConversionKind.UnlitParticle;
        public override IReadOnlyList<string> PreferredMainTextureProperties => new[] { "_MainTexture", "_MainTex", "_BaseMap" };
        public override IReadOnlyList<string> AlphaFactorTextureProperties => new[] { "_Mask", "_Noise" };
        public override string GetProfileId(Material material) => "custom.hovl.particles";
        public override string[] GetPropertyAliases(Material material) => new[] { "_MainTex", "_MainTexture", "_EmissionTex", "_Dissolve", "_BaseMap", "_Color", "_TintColor", "_AlphaOverride", "_Opacity", "_Blend2", "_Mask", "_Noise", "_NormalMap", "_SpeedMainTexUVNoiseZW", "_Emission", "_Remap", "_AddColor", "_Desaturation", "_Usesmoothdissolve", "_Usedepth", "_SrcBlend", "_DstBlend", "_ZWrite" };

        public override bool RequiresPairedRuntime(Material material, string fragmentColorMode)
        {
            var shaderName = material == null || material.shader == null ? string.Empty : material.shader.name;
            return !ShaderNameEquals(shaderName, "Hovl/Particles/Distortion") &&
                   base.RequiresPairedRuntime(material, fragmentColorMode);
        }

        public override void ConfigureMaterial(ShaderProfileMaterialContext context)
        {
            var material = context.material;
            if (material == null || material.shader == null) return;
            var shaderName = material.shader.name ?? string.Empty;
            var blend = new MaterialBlendState();
            if (ShaderNameEquals(shaderName, "Hovl/Particles/Distortion"))
            {
                context.diagnostics.unsupported.Add("material.shader.hovlDistortion.grabPass");
                context.diagnostics.approximated.Add("material.shader.hovlDistortion.invisibleFallback");
                context.diagnostics.warnings.Add("Hovl/Particles/Distortion requires Unity GrabPass and screen-space NormalMap distortion. Best-effort emits an invisible review fallback; strict export fails until a paired distortion profile is available.");
                context.invisibleFallback = true;
                blend.fragmentColorMode = "invisibleFallback";
                context.blendStateOverride = blend;
                return;
            }
            if (ShaderNameEquals(shaderName, "Hovl/Particles/Add_CenterGlow"))
            {
                var destination = material.HasProperty("_Blend2")
                    ? Mathf.RoundToInt(material.GetFloat("_Blend2"))
                    : (int)BlendMode.One;
                if (!TryMapBlendFactor(destination, out var mappedDestination)) mappedDestination = 201;
                SetCustomBlend(blend, 201, mappedDestination, 100, "hovlAdditivePremultiply");
                context.blendStateOverride = blend;
                return;
            }
            SetCustomBlend(blend, 204, 205, 100, "stock");
            context.blendStateOverride = blend;

            if (ShaderNameEquals(shaderName, "Hovl/Particles/Blend_TwoSides") && material.HasProperty("_Cutoff"))
                context.alphaTestOverride = Mathf.Clamp01(material.GetFloat("_Cutoff"));
        }

        public override bool TryResolveTexturePanning(
            Material material,
            string textureProperty,
            out Vector2 panning,
            out string diagnosticLabel)
        {
            panning = Vector2.zero;
            diagnosticLabel = string.Empty;
            if (!string.Equals(textureProperty, "_MainTexture", StringComparison.Ordinal) ||
                material == null || !material.HasProperty("_SpeedMainTexUVNoiseZW"))
                return false;
            var value = material.GetVector("_SpeedMainTexUVNoiseZW");
            panning = new Vector2(value.x, value.y);
            diagnosticLabel = "SpeedMainTexUVNoiseZW";
            return true;
        }

        public override bool IsAlphaTextureFactorActive(Material material, string property)
        {
            if (material == null) return false;
            var shaderName = material.shader == null ? string.Empty : material.shader.name;
            if (ShaderNameEquals(shaderName, "Hovl/Particles/Blend_TwoSides"))
                return string.Equals(property, "_Mask", StringComparison.Ordinal) ||
                       string.Equals(property, "_Noise", StringComparison.Ordinal);
            if (!string.Equals(property, "_Noise", StringComparison.Ordinal)) return false;
            return ShaderNameEquals(shaderName, "Hovl/Particles/Add_CenterGlow") ||
                   ShaderNameEquals(shaderName, "Hovl/Particles/Blend_CenterGlow") ||
                   ShaderNameEquals(shaderName, "Hovl/Particles/BlendDistort");
        }

        public override string ResolveAlphaTextureChannel(Material material, string property)
        {
            return string.Equals(property, "_Noise", StringComparison.Ordinal)
                ? "a"
                : base.ResolveAlphaTextureChannel(material, property);
        }
    }
}
