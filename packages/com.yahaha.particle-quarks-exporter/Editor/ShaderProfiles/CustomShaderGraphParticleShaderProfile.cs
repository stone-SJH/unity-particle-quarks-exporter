using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class CustomShaderGraphParticleShaderProfile : ShaderProfile
    {
        private static readonly string[] Names =
        {
            "Shader Graphs/Fx_ParticleDissolve_add",
            "Shader Graphs/Fx_ParticleDissolve_apb"
        };
        private static readonly string[] TextureProperties =
        {
            "_MainTex", "_BaseMap", "Texture2D_F593E37E", "Texture2D_EDA87E5",
            "Texture2D_FF0A21CE", "_SampleTexture2D_A2DE9010_Texture_1",
            "_SampleTexture2D_1F6DF57E_Texture_1"
        };
        public override string Name => "CustomShaderGraphParticle";
        public override string DiagnosticId => "custom.shadergraph.particle";
        public override IReadOnlyList<string> ShaderNames => Names;
        public override bool ConsumesParticleColor => true;
        public override bool FixedTransparent => true;
        public override bool CustomParticle => true;
        public override ShaderProfileConversionKind ConversionKind => ShaderProfileConversionKind.UnlitParticle;
        public override IReadOnlyList<string> PreferredMainTextureProperties => TextureProperties;
        public override string GetProfileId(Material material) => "custom.shadergraph.particle";
        public override string[] GetPropertyAliases(Material material) => new[] { "_MainTex", "_BaseMap", "Texture2D_F593E37E", "Texture2D_EDA87E5", "Texture2D_FF0A21CE", "_BaseColor", "_AlphaClip", "_AlphaCutoff", "_SrcBlend", "_DstBlend", "_ZWrite" };

        public override string ResolveBaseColorChannel(Material material)
        {
            if (material == null || material.shader == null) return "rgb";
            var shaderName = material.shader.name ?? string.Empty;
            return shaderName.EndsWith("Fx_ParticleDissolve_add", StringComparison.OrdinalIgnoreCase) ||
                   shaderName.EndsWith("Fx_ParticleDissolve_apb", StringComparison.OrdinalIgnoreCase)
                ? "r"
                : "rgb";
        }

        public override void ConfigureMaterial(ShaderProfileMaterialContext context)
        {
            if (context.material == null || context.material.shader == null) return;
            var shaderName = context.material.shader.name ?? string.Empty;
            var blend = new MaterialBlendState();
            if (shaderName.EndsWith("_add", StringComparison.OrdinalIgnoreCase))
            {
                blend.blending = 2;
                context.blendStateOverride = blend;
            }
            else if (shaderName.EndsWith("_apb", StringComparison.OrdinalIgnoreCase))
            {
                SetCustomBlend(blend, 204, 205, 100, "stock");
                context.blendStateOverride = blend;
            }
        }

        public override bool TryResolveSoftParticleSettings(
            Material material,
            ConversionDiagnostics diagnostics,
            out ShaderProfileSoftParticleSettings settings)
        {
            settings = null;
            if (material == null || material.shader == null ||
                (!material.shader.name.EndsWith("Fx_ParticleDissolve_add", StringComparison.OrdinalIgnoreCase) &&
                 !material.shader.name.EndsWith("Fx_ParticleDissolve_apb", StringComparison.OrdinalIgnoreCase)) ||
                !material.HasProperty("Boolean_52F3CBA5") ||
                material.GetFloat("Boolean_52F3CBA5") <= 0.5f)
                return false;
            diagnostics.mapped.Add("material.softParticles.shaderGraphSceneDepth");
            diagnostics.approximated.Add("material.softParticles.shaderGraphDepthFallback");
            diagnostics.warnings.Add("ShaderGraph particle alpha uses a Scene Depth soft-particle branch. Exported playback requires a host depth texture; hosts without depth retain the explicit paired-runtime fallback.");
            settings = new ShaderProfileSoftParticleSettings();
            return true;
        }
    }
}
