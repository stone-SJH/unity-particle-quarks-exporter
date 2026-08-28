using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class CustomVehicleEffectShaderProfile : ShaderProfile
    {
        private static readonly string[] Names =
        {
            "Effect/Add_Blend_UPR",
            "Effect/Gradient_Add_Blend_URP",
            "Effect/Noise_UV_Mask_URP",
            "Effect/SoftDissolve_Additive_URP"
        };
        public override string Name => "CustomVehicleEffect";
        public override string DiagnosticId => "custom.vehicle.effect";
        public override IReadOnlyList<string> ShaderNames => Names;
        public override bool ConsumesParticleColor => true;
        public override bool FixedTransparent => true;
        public override bool CustomParticle => true;
        public override ShaderProfileConversionKind ConversionKind => ShaderProfileConversionKind.UnlitParticle;
        public override IReadOnlyList<string> AlphaFactorTextureProperties => new[] { "_Mask", "_MaskTex", "_MaskTex1", "_DissolveTex", "_Noise" };
        public override string GetProfileId(Material material) => "custom.vehicle.effect";
        public override string[] GetPropertyAliases(Material material) => new[] { "_MainTex", "_BaseMap", "_Mask", "_MaskTex", "_MaskTex1", "_DissolveTex", "_DissolveProgress", "_Dst", "_SrcBlend", "_DstBlend", "_Zwrite" };

        public override void ConfigureMaterial(ShaderProfileMaterialContext context)
        {
            if (context.material == null) return;
            var destination = context.material.HasProperty("_Dst")
                ? Mathf.RoundToInt(context.material.GetFloat("_Dst"))
                : (int)BlendMode.One;
            if (!TryMapBlendFactor(destination, out var mappedDestination)) mappedDestination = 201;
            var blend = new MaterialBlendState();
            SetCustomBlend(blend, 204, mappedDestination, 100, "stock");
            blend.blendSrcAlpha = 201;
            blend.blendDstAlpha = 205;
            blend.blendEquationAlpha = 100;
            blend.customAlpha = true;
            context.blendStateOverride = blend;
        }

        public override bool TryResolveTexturePanning(
            Material material,
            string textureProperty,
            out Vector2 panning,
            out string diagnosticLabel)
        {
            panning = Vector2.zero;
            diagnosticLabel = string.Empty;
            if (material == null || !material.HasProperty("_Speed1")) return false;
            var speed = material.GetVector("_Speed1");
            if (string.Equals(textureProperty, "_MainTex", StringComparison.Ordinal))
            {
                panning = new Vector2(speed.x, speed.y);
                diagnosticLabel = "Speed1.xy";
                return true;
            }
            if (string.Equals(textureProperty, "_Mask", StringComparison.Ordinal) ||
                string.Equals(textureProperty, "_MaskTex", StringComparison.Ordinal) ||
                string.Equals(textureProperty, "_MaskTex1", StringComparison.Ordinal))
            {
                panning = new Vector2(speed.z, speed.w);
                diagnosticLabel = "Speed1.zw";
                return true;
            }
            return false;
        }
    }
}
