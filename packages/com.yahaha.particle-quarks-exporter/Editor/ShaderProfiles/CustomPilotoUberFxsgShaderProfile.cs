using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class CustomPilotoUberFxsgShaderProfile : ShaderProfile
    {
        private static readonly string[] Names = { "Piloto Studio/UberFXSG" };
        public override string Name => "CustomPilotoUberFxsg";
        public override string DiagnosticId => "custom.piloto.uberfxsg";
        public override IReadOnlyList<string> ShaderNames => Names;
        public override bool ConsumesParticleColor => true;
        public override bool FixedTransparent => true;
        public override bool CustomParticle => true;
        public override ShaderProfileConversionKind ConversionKind => ShaderProfileConversionKind.UnlitParticle;
        public override IReadOnlyList<string> AlphaFactorTextureProperties => new[] { "_AlphaOverride" };
        public override string GetProfileId(Material material) => "custom.piloto.uberfxsg";
        public override string GetProfileVersion(Material material) => "v2";
        public override string[] GetPropertyAliases(Material material) => new[] { "_MainTex", "_BaseMap", "_AlphaOverride", "_MainTextureChannel", "_MainAlphaChannel", "_AlphaOverrideChannel", "_Desaturate", "_MiddlePointPos", "_MiddlePointPos1", "_LastColor", "_MidColor", "_WhiteColor", "_FresnelColor", "_FresnelScale", "_FresnelPower", "_UseAlphaOverride", "_UseSoftAlpha", "_SoftFadeFactor", "_USERAMP", "_FRESNEL", "_AlphaClip", "_AlphaCutoff", "_AlphaSrcBlend", "_AlphaDstBlend", "_BUILTIN_SrcBlend", "_BUILTIN_DstBlend", "_ZWrite" };

        public override void ConfigureMaterial(ShaderProfileMaterialContext context)
        {
            var material = context.material;
            if (material == null) return;
            ConfigureBlend(material, context);
            context.shaderParametersOverride = BuildShaderParameters(material, context.diagnostics);
        }

        public override bool TryResolveTexturePanning(
            Material material,
            string textureProperty,
            out Vector2 panning,
            out string diagnosticLabel)
        {
            panning = Vector2.zero;
            diagnosticLabel = string.Empty;
            var panningProperty = string.Equals(textureProperty, "_MainTex", StringComparison.Ordinal)
                ? "_MainTexturePanning"
                : string.Equals(textureProperty, "_AlphaOverride", StringComparison.Ordinal)
                    ? "_AlphaOverridePanning"
                    : string.Empty;
            if (material == null || string.IsNullOrEmpty(panningProperty) || !material.HasProperty(panningProperty))
                return false;
            var value = material.GetVector(panningProperty);
            panning = new Vector2(value.x, value.y);
            diagnosticLabel = panningProperty.TrimStart('_');
            return true;
        }

        public override bool IsAlphaTextureFactorActive(Material material, string property)
        {
            if (material == null) return false;
            if (string.Equals(property, "_AlphaOverride", StringComparison.Ordinal))
                return IsEnabledFloat(material, "_UseAlphaOverride", "_USEALPHAOVERRIDE");
            if (string.Equals(property, "_DissolveTex", StringComparison.Ordinal) ||
                string.Equals(property, "_DissolveMap", StringComparison.Ordinal))
                return IsEnabledFloat(material, "_UseAlphaDisolve", "_UseAlphaDissolve");
            return false;
        }

        public override string ResolveAlphaChannel(Material material, Texture mainTexture)
        {
            if (UsesSameTextureAlphaOverride(material, mainTexture) && material.HasProperty("_AlphaOverrideChannel"))
                return ResolveColorChannel(material.GetColor("_AlphaOverrideChannel"));
            return material != null && material.HasProperty("_MainAlphaChannel")
                ? ResolveColorChannel(material.GetColor("_MainAlphaChannel"))
                : base.ResolveAlphaChannel(material, mainTexture);
        }

        public override Color? ResolveAlphaChannelWeights(Material material, Texture mainTexture)
        {
            if (UsesSameTextureAlphaOverride(material, mainTexture) && material.HasProperty("_AlphaOverrideChannel"))
                return material.GetColor("_AlphaOverrideChannel");
            return material != null && material.HasProperty("_MainAlphaChannel")
                ? material.GetColor("_MainAlphaChannel")
                : (Color?)null;
        }

        public override Color? ResolveAlphaTextureChannelWeights(Material material, string property)
        {
            return material != null && string.Equals(property, "_AlphaOverride", StringComparison.Ordinal) &&
                   material.HasProperty("_AlphaOverrideChannel")
                ? material.GetColor("_AlphaOverrideChannel")
                : (Color?)null;
        }

        public override Color? ResolveMainTextureColorScale(Material material)
        {
            return material != null && material.HasProperty("_MainTextureChannel")
                ? material.GetColor("_MainTextureChannel")
                : (Color?)null;
        }

        public override bool UsesSameTextureAlphaOverride(Material material, Texture mainTexture)
        {
            return material != null && mainTexture != null && material.HasProperty("_AlphaOverride") &&
                   TexturesReferToSameAsset(material.GetTexture("_AlphaOverride"), mainTexture) &&
                   IsEnabledFloat(material, "_UseAlphaOverride", "_USEALPHAOVERRIDE");
        }

        public override bool TryResolveSoftParticleSettings(
            Material material,
            ConversionDiagnostics diagnostics,
            out ShaderProfileSoftParticleSettings settings)
        {
            settings = null;
            if (!IsEnabledFloat(material, "_UseSoftAlpha", "_USESOFTALPHA")) return false;
            var factor = material != null && material.HasProperty("_SoftFadeFactor")
                ? Mathf.Max(0.000001f, material.GetFloat("_SoftFadeFactor"))
                : 1;
            settings = new ShaderProfileSoftParticleSettings { far = 1 / factor };
            diagnostics.mapped.Add("material.softParticles.pilotoSoftAlpha");
            return true;
        }

        private static void ConfigureBlend(Material material, ShaderProfileMaterialContext context)
        {
            var sourceAlias = FirstProperty(material, "_BUILTIN_SrcBlend", "_SrcBlend", "_AlphaSrcBlend");
            var destinationAlias = FirstProperty(material, "_BUILTIN_DstBlend", "_DstBlend", "_AlphaDstBlend");
            if (sourceAlias == null || destinationAlias == null ||
                !TryMapBlendFactor(Mathf.RoundToInt(material.GetFloat(sourceAlias)), out var source) ||
                !TryMapBlendFactor(Mathf.RoundToInt(material.GetFloat(destinationAlias)), out var destination) ||
                (material.renderQueue < (int)RenderQueue.Transparent && source == 201 && destination == 200)) return;

            var blend = new MaterialBlendState();
            SetCustomBlend(blend, source, destination, 100, "stock");
            var alphaSourceAlias = FirstProperty(material, "_AlphaSrcBlend");
            var alphaDestinationAlias = FirstProperty(material, "_AlphaDstBlend");
            if (alphaSourceAlias != null && alphaDestinationAlias != null &&
                TryMapBlendFactor(Mathf.RoundToInt(material.GetFloat(alphaSourceAlias)), out var alphaSource) &&
                TryMapBlendFactor(Mathf.RoundToInt(material.GetFloat(alphaDestinationAlias)), out var alphaDestination))
            {
                blend.blendSrcAlpha = alphaSource;
                blend.blendDstAlpha = alphaDestination;
                blend.blendEquationAlpha = 100;
                blend.customAlpha = true;
            }
            context.blendStateOverride = blend;
        }

        private static JsonObject BuildShaderParameters(Material material, ConversionDiagnostics diagnostics)
        {
            var parameters = Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.material.shader_parameters.v2"))
                .Add("profile", Json.String("custom.piloto.uberfxsg"))
                .Add("useColorRamp", Json.Boolean(IsEnabled(material, "_USERAMP")))
                .Add("useFresnel", Json.Boolean(IsEnabled(material, "_FRESNEL")))
                .Add("useAlphaOverride", Json.Boolean(IsEnabledOrMissing(material, "_UseAlphaOverride", "_USEALPHAOVERRIDE")))
                .Add("useSoftAlpha", Json.Boolean(IsEnabledOrMissing(material, "_UseSoftAlpha", "_USESOFTALPHA")))
                .Add("emissionMode", Json.String("baseColorAdditive"))
                .Add("emissionScale", Json.Number(1f))
                .Add("colorOperation", Json.String("channelPickerSaturation"))
                .Add("alphaOperation", Json.String("channelPickerAdd"));
            foreach (var name in new[] { "_MainTextureChannel", "_MainAlphaChannel", "_AlphaOverrideChannel", "_LastColor", "_MidColor", "_WhiteColor", "_FresnelColor" })
                if (material.HasProperty(name)) parameters.Add(name.TrimStart('_'), ColorJson(material.GetColor(name)));
            foreach (var name in new[] { "_FresnelScale", "_FresnelPower", "_Desaturate", "_MiddlePointPos", "_MiddlePointPos1", "_FresnelBlend" })
                if (material.HasProperty(name)) parameters.Add(name.TrimStart('_'), Json.Number(material.GetFloat(name)));
            diagnostics.mapped.Add("material.shaderParameters.uberfxsg.v2");
            diagnostics.mapped.Add("material.shaderParameters.uberfxsg.v2.channelPicker");
            diagnostics.mapped.Add("material.shaderParameters.uberfxsg.v2.baseColorEmission");
            diagnostics.mapped.Add("material.shaderParameters.uberfxsg.v2.alphaOverride");
            if (IsEnabled(material, "_USERAMP")) diagnostics.mapped.Add("material.shaderParameters.uberfxsg.colorRamp");
            return parameters;
        }
    }
}
