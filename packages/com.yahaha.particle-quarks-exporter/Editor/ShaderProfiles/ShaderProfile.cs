using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class MaterialBlendState
    {
        public int blending = 1;
        public int blendSrc = 204;
        public int blendDst = 205;
        public int blendEquation = 100;
        public int blendSrcAlpha = 204;
        public int blendDstAlpha = 205;
        public int blendEquationAlpha = 100;
        public bool custom;
        public bool customAlpha;
        public bool sourcePremultipliedAlpha;
        public string fragmentColorMode = "stock";
    }

    internal sealed class ShaderProfileMaterialContext
    {
        public ShaderProfileMaterialContext(
            Material materialValue,
            ConversionDiagnostics diagnosticsValue,
            bool sourceBuiltInPipelineValue)
        {
            material = materialValue;
            diagnostics = diagnosticsValue;
            sourceBuiltInPipeline = sourceBuiltInPipelineValue;
        }

        public readonly Material material;
        public readonly ConversionDiagnostics diagnostics;
        public readonly bool sourceBuiltInPipeline;

        public Color? materialColorOverride;
        public Color? materialEmissionOverride;
        public MaterialBlendState blendStateOverride;
        public float? alphaTestOverride;
        public bool? doubleSidedOverride;
        public string mainTexturePropertyOverride;
        public string baseColorChannelOverride;
        public JsonObject shaderParametersOverride;
        public bool invisibleFallback;
    }

    internal enum ShaderProfileConversionKind
    {
        RegisteredSubset,
        UnlitParticle,
        UnlitNoVertexColor,
        VertexLit,
        SyntyParticleLit
    }

    internal sealed class ShaderProfileSoftParticleSettings
    {
        public float near;
        public float far = 1;
    }

    internal sealed class ShaderProfileCameraFadeSettings
    {
        public float near;
        public float far = 1;
        public float smoothness = 1;
    }

    internal sealed class ShaderProfileLitMapSettings
    {
        public string normalMapProperty = "_BumpMap";
        public string normalScaleProperty = "_BumpScale";
        public bool objectSpaceNormal;
        public string emissionMapProperty = "_EmissionMap";
        public bool emissionMapActive;
    }

    internal abstract class ShaderProfile
    {
        private static readonly string[] NoShaderNames = Array.Empty<string>();
        private static readonly string[] NoPropertyAliases = Array.Empty<string>();
        private static readonly string[] DefaultAlphaFactorProperties =
        {
            "_AlphaOverride", "_Mask", "_MaskMap", "_MaskTex", "_MaskTex1",
            "_DissolveTex", "_DissolveMap", "_Noise", "_NoiseTex", "_NoiseMap",
            "_DetailNoise"
        };

        public abstract string Name { get; }
        public abstract string DiagnosticId { get; }

        public virtual IReadOnlyList<string> ShaderNames => NoShaderNames;
        public virtual bool UsesLitMaterial => false;
        public virtual bool IsSupported => true;
        public virtual bool ConsumesParticleColor => false;
        public virtual bool FixedTransparent => false;
        public virtual bool CustomParticle => false;
        public virtual bool DoubleSidedByDefault => false;
        public virtual bool UsesSyntyPipelineCull => false;
        public virtual bool SupportsPackedMetallicGloss => false;
        public virtual bool SpecularWorkflow => false;
        public virtual ShaderProfileConversionKind ConversionKind => ShaderProfileConversionKind.RegisteredSubset;
        public virtual bool SupportsParticleColorMode => false;
        public virtual string UnlitNormalMapProperty => "_BumpMap";
        public virtual IReadOnlyList<string> PreferredMainTextureProperties => NoShaderNames;
        public virtual IReadOnlyList<string> AlphaFactorTextureProperties => DefaultAlphaFactorProperties;

        public virtual string GetProfileId(Material material) => IsSupported ? DiagnosticId : string.Empty;
        public virtual string GetProfileVersion(Material material) => "v1";
        public virtual string[] GetPropertyAliases(Material material) => NoPropertyAliases;

        public virtual bool MatchesShaderName(string shaderName)
        {
            foreach (var candidate in ShaderNames)
            {
                if (ShaderNameEquals(shaderName, candidate)) return true;
            }
            return false;
        }

        public virtual bool RequiresPairedRuntime(Material material, string fragmentColorMode)
        {
            return !string.Equals(fragmentColorMode, "stock", StringComparison.Ordinal) || CustomParticle;
        }

        public virtual void ConfigureMaterial(ShaderProfileMaterialContext context)
        {
        }

        public virtual string ResolveCullPropertyName(
            bool builtInPipeline,
            bool hasBuiltInCullMode,
            bool hasCull,
            bool hasCullMode)
        {
            if (UsesSyntyPipelineCull && builtInPipeline && hasBuiltInCullMode)
                return "_BUILTIN_CullMode";
            if (hasCull) return "_Cull";
            if (hasCullMode) return "_CullMode";
            return string.Empty;
        }

        public virtual string ResolveMainTextureProperty(Material material)
        {
            if (material == null) return string.Empty;
            foreach (var property in PreferredMainTextureProperties)
            {
                if (material.HasProperty(property) && material.GetTexture(property) != null)
                    return property;
            }
            foreach (var property in PreferredMainTextureProperties)
            {
                if (material.HasProperty(property)) return property;
            }
            if (material.HasProperty("_BaseMap")) return "_BaseMap";
            if (material.HasProperty("_MainTex")) return "_MainTex";
            if (material.HasProperty("_BaseColorMap")) return "_BaseColorMap";
            if (material.HasProperty("_UnlitColorMap")) return "_UnlitColorMap";
            return string.Empty;
        }

        public virtual Color ResolveMaterialColor(Material material, ConversionDiagnostics diagnostics)
        {
            if (material == null) return new Color(1, 1, 1, 0);
            if (material.HasProperty("_UnlitColor"))
            {
                diagnostics.mapped.Add("material.unlitColor");
                return material.GetColor("_UnlitColor");
            }
            if (material.HasProperty("_BaseColor"))
            {
                diagnostics.mapped.Add("material.baseColor");
                return material.GetColor("_BaseColor");
            }
            if (material.HasProperty("_Color"))
            {
                diagnostics.mapped.Add("material.color");
                return material.GetColor("_Color");
            }
            if (material.HasProperty("_TintColor"))
            {
                diagnostics.mapped.Add("material.tintColor");
                return material.GetColor("_TintColor");
            }
            diagnostics.mapped.Add("material.whiteTint");
            return Color.white;
        }

        public virtual Color ResolveMaterialEmission(Material material, ConversionDiagnostics diagnostics)
        {
            if (material == null) return Color.black;
            if (material.HasProperty("_EmissionColor") && material.IsKeywordEnabled("_EMISSION"))
            {
                diagnostics.mapped.Add("material.emissive.standard");
                return material.GetColor("_EmissionColor");
            }
            if (material.HasProperty("_EmissiveColor") && material.GetColor("_EmissiveColor").maxColorComponent > 0)
            {
                diagnostics.mapped.Add("material.emissive.hdrp");
                return material.GetColor("_EmissiveColor");
            }
            return Color.black;
        }

        public virtual float ResolveAlphaTest(Material material)
        {
            if (material == null) return 0;
            var enableProperty = FirstProperty(material, "_AlphaCutoffEnable", "_AlphaClipEnable", "_AlphaTestEnable");
            var thresholdProperty = FirstProperty(material, "_AlphaCutoff", "_Alpha_Clip_Threshold", "_AlphaClipThreshold", "_Cutoff");
            if (enableProperty != null && thresholdProperty != null && material.GetFloat(enableProperty) > 0.5f)
                return Mathf.Clamp01(material.GetFloat(thresholdProperty));
            if (material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") > 0.5f && thresholdProperty != null)
                return Mathf.Clamp01(material.GetFloat(thresholdProperty));
            if (!material.HasProperty("_Cutoff")) return 0;
            if (material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") > 0.5f)
                return material.GetFloat("_Cutoff");
            var shaderName = material.shader == null ? string.Empty : material.shader.name;
            var active = material.IsKeywordEnabled("_ALPHATEST_ON") ||
                         shaderName.IndexOf("Cutout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         shaderName.IndexOf("AlphaTest", StringComparison.OrdinalIgnoreCase) >= 0;
            return active ? material.GetFloat("_Cutoff") : 0;
        }

        public virtual string ResolveBaseColorChannel(Material material) => "rgb";

        public virtual bool TryResolveTexturePanning(
            Material material,
            string textureProperty,
            out Vector2 panning,
            out string diagnosticLabel)
        {
            panning = Vector2.zero;
            diagnosticLabel = string.Empty;
            return false;
        }

        public virtual bool IsAlphaTextureFactorActive(Material material, string property) => true;

        public virtual string ResolveAlphaTextureChannel(Material material, string property)
        {
            if (material == null) return "r";
            var channelProperty = property == "_AlphaOverride"
                ? "_AlphaOverrideChannel"
                : property == "_Mask" || property == "_MaskTex" || property == "_MaskTex1"
                    ? "_MaskChannel"
                    : property == "_DissolveTex" || property == "_DissolveMap"
                        ? "_DetailDisolveChannel"
                        : string.Empty;
            if (!string.IsNullOrEmpty(channelProperty) && material.HasProperty(channelProperty))
                return ResolveColorChannel(material.GetColor(channelProperty));
            return "r";
        }

        public virtual string ResolveAlphaChannel(Material material, Texture mainTexture)
        {
            if (UsesSameTextureAlphaOverride(material, mainTexture))
                return ResolveAlphaTextureChannel(material, "_AlphaOverride");
            if (material != null && material.HasProperty("_MainTextureChannel"))
                return ResolveColorChannel(material.GetColor("_MainTextureChannel"));
            return "a";
        }

        public virtual Color? ResolveAlphaChannelWeights(Material material, Texture mainTexture) => null;
        public virtual Color? ResolveAlphaTextureChannelWeights(Material material, string property) => null;
        public virtual Color? ResolveMainTextureColorScale(Material material) => null;
        public virtual bool UsesSameTextureAlphaOverride(Material material, Texture mainTexture) => false;

        public virtual bool TryResolveSoftParticleSettings(
            Material material,
            ConversionDiagnostics diagnostics,
            out ShaderProfileSoftParticleSettings settings)
        {
            settings = null;
            if (!IsSoftParticleMaterial(material)) return false;
            if (material.HasProperty("_SoftParticlesNearFadeDistance") &&
                material.HasProperty("_SoftParticlesFarFadeDistance"))
            {
                var near = Mathf.Max(0, material.GetFloat("_SoftParticlesNearFadeDistance"));
                var far = Mathf.Max(near + 0.000001f, material.GetFloat("_SoftParticlesFarFadeDistance"));
                diagnostics.mapped.Add("material.softParticles.urpFadeDistance");
                settings = new ShaderProfileSoftParticleSettings { near = near, far = far };
                return true;
            }
            if (material.HasProperty("_InvFade"))
            {
                var inverseFade = Mathf.Max(0.000001f, material.GetFloat("_InvFade"));
                diagnostics.mapped.Add("material.softParticles.legacyInvFade");
                settings = new ShaderProfileSoftParticleSettings { far = 1 / inverseFade };
                return true;
            }
            diagnostics.approximated.Add("material.softParticles.unitFadeFallback");
            diagnostics.warnings.Add("The shader enables soft particles without an exposed fade-distance property. Best-effort uses near=0 and far=1.");
            settings = new ShaderProfileSoftParticleSettings();
            return true;
        }

        public virtual bool TryResolveCameraFadeSettings(
            Material material,
            ConversionDiagnostics diagnostics,
            out ShaderProfileCameraFadeSettings settings)
        {
            settings = null;
            return false;
        }

        public virtual bool SuppressCameraFadeToggleDiagnostic(Material material) => false;

        public virtual ShaderProfileLitMapSettings GetLitMapSettings(Material material)
        {
            return new ShaderProfileLitMapSettings
            {
                emissionMapActive = material != null &&
                                    material.HasProperty("_EmissionMap") &&
                                    material.IsKeywordEnabled("_EMISSION")
            };
        }

        public virtual float ResolvePackedSmoothness(Material material)
        {
            return material != null && material.HasProperty("_GlossMapScale")
                ? Mathf.Clamp01(material.GetFloat("_GlossMapScale"))
                : 1;
        }

        public virtual void DiagnoseMaterialFeatures(
            Material material,
            bool litMaterial,
            ConversionDiagnostics diagnostics)
        {
            if (material == null || !SupportsParticleColorMode ||
                !material.HasProperty("_ColorMode") ||
                Mathf.RoundToInt(material.GetFloat("_ColorMode")) == 0)
                return;
            diagnostics.unsupported.Add("material.particleColorMode");
            diagnostics.approximated.Add("material.particleColorMode.multiplyFallback");
            diagnostics.warnings.Add("The source particle shader Color Mode is not Multiply. Best-effort uses the documented material-times-particle-color path; strict export fails because Additive/Subtractive/Overlay/Color/Difference are shader color-composition operations, not framebuffer blend modes.");
        }

        public virtual void DiagnoseCommonMaterialFeatures(
            Material material,
            bool litMaterial,
            ConversionDiagnostics diagnostics)
        {
            if (material == null) return;

            DiagnoseActiveToggle(material, "_FlipbookBlending", "material.flipbookBlending", "frameSelectionFallback", diagnostics);
            if (!SuppressCameraFadeToggleDiagnostic(material))
                DiagnoseActiveToggle(material, "_CameraFadingEnabled", "material.cameraFading", "omittedFallback", diagnostics);
            DiagnoseActiveToggle(material, "_DistortionEnabled", "material.distortion", "omittedFallback", diagnostics);
            DiagnoseActiveToggle(material, "_DistortionEnable", "material.distortion", "omittedFallback", diagnostics);

            if (!litMaterial && HasActiveMaterialEmission(material))
            {
                diagnostics.unsupported.Add("material.emission.unlitComposition");
                diagnostics.approximated.Add("material.emission.unlitComposition.foldedParticleColorFallback");
                diagnostics.warnings.Add("The source adds emission independently of base-texture and ParticleSystem color, but stock Quarks unlit batches expose only texture-times-particle-color. Best-effort folds emissive color into particle color before the base texture; strict export fails.");
            }

            if (!litMaterial && material.IsKeywordEnabled("_NORMALMAP") &&
                material.HasProperty("_BumpMap") && material.GetTexture("_BumpMap") != null)
            {
                diagnostics.unsupported.Add("material.normalMap.unlitRenderer");
                diagnostics.approximated.Add("material.normalMap.unlitRenderer.omittedFallback");
                diagnostics.warnings.Add("An active normal map cannot affect the stock Quarks unlit particle shader. Best-effort omits it; strict export fails.");
            }
            if (!litMaterial && !string.IsNullOrEmpty(UnlitNormalMapProperty) &&
                material.IsKeywordEnabled("_NORMALMAP") &&
                material.HasProperty(UnlitNormalMapProperty) &&
                material.GetTexture(UnlitNormalMapProperty) != null)
            {
                diagnostics.unsupported.Add("material.normalMap.unlitRenderer");
                diagnostics.approximated.Add("material.normalMap.unlitRenderer.omittedFallback");
                diagnostics.warnings.Add("An active profile normal map cannot affect the stock Quarks unlit particle shader. Best-effort omits it; strict export fails.");
            }

            DiagnosePackedMetallicGloss(material, diagnostics);
            DiagnoseUnsupportedMap(material, "_SpecGlossMap", "_SPECGLOSSMAP", "material.specGlossMap", diagnostics);
            DiagnoseUnsupportedMap(material, "_OcclusionMap", "_OCCLUSIONMAP", "material.occlusionMap", diagnostics);
            DiagnoseUnsupportedMap(material, "_ParallaxMap", "_PARALLAXMAP", "material.parallaxMap", diagnostics);
            DiagnoseUnsupportedMap(material, "_DetailAlbedoMap", "_DETAIL_MULX2", "material.detailAlbedoMap", diagnostics);
            DiagnoseUnsupportedMap(material, "_DetailNormalMap", "_DETAIL_MULX2", "material.detailNormalMap", diagnostics);
            DiagnoseUnsupportedMap(material, "_MaskMap", "_MASKMAP", "material.hdrpMaskMap", diagnostics);
            DiagnoseUnsupportedMap(material, "_DetailMap", "_DETAIL_MAP", "material.hdrpDetailMap", diagnostics);
            DiagnoseUnsupportedMap(material, "_HeightMap", "_HEIGHTMAP", "material.hdrpHeightMap", diagnostics);
            DiagnoseUnsupportedMap(material, "_BentNormalMap", "_BENTNORMALMAP", "material.hdrpBentNormalMap", diagnostics);
            DiagnoseUnsupportedMap(material, "_TangentMap", "_TANGENTMAP", "material.hdrpTangentMap", diagnostics);
            DiagnoseUnsupportedMap(material, "_AnisotropyMap", "_ANISOTROPYMAP", "material.hdrpAnisotropyMap", diagnostics);
            DiagnoseUnsupportedMap(material, "_SubsurfaceMaskMap", "_SUBSURFACE_MASK_MAP", "material.hdrpSubsurfaceMaskMap", diagnostics);
            DiagnoseUnsupportedMap(material, "_ThicknessMap", "_THICKNESSMAP", "material.hdrpThicknessMap", diagnostics);
            DiagnoseUnsupportedMap(material, "_IridescenceThicknessMap", "_IRIDESCENCE_THICKNESSMAP", "material.hdrpIridescenceThicknessMap", diagnostics);
            DiagnoseUnsupportedMap(material, "_SpecularColorMap", "_SPECULARCOLORMAP", "material.hdrpSpecularColorMap", diagnostics);

            DiagnoseActiveKeyword(material, "_ALPHAMODULATE_ON", "material.urpAlphaModulate", "stockBlendFallback", diagnostics);
            DiagnoseActiveKeyword(material, "_SPECULARHIGHLIGHTS_OFF", "material.specularHighlightsDisabled", "standardPbrFallback", diagnostics);
            DiagnoseActiveKeyword(material, "_GLOSSYREFLECTIONS_OFF", "material.builtinGlossyReflectionsDisabled", "standardPbrFallback", diagnostics);
            DiagnoseActiveKeyword(material, "_ENVIRONMENTREFLECTIONS_OFF", "material.urpEnvironmentReflectionsDisabled", "standardPbrFallback", diagnostics);
            DiagnoseActiveKeyword(material, "_RECEIVE_SHADOWS_OFF", "material.urpReceiveShadowsDisabled", "standardPbrFallback", diagnostics);
            DiagnoseActiveKeyword(material, "_CLEARCOAT", "material.urpClearCoat", "omittedFallback", diagnostics);
            DiagnoseActiveKeyword(material, "_CLEARCOATMAP", "material.urpClearCoatMap", "omittedFallback", diagnostics);
            DiagnoseActiveKeyword(material, "_MAPPING_PLANAR", "material.hdrpBaseMappingPlanar", "uv0Fallback", diagnostics);
            DiagnoseActiveKeyword(material, "_MAPPING_TRIPLANAR", "material.hdrpBaseMappingTriplanar", "uv0Fallback", diagnostics);
            DiagnoseActiveKeyword(material, "_EMISSIVE_MAPPING_PLANAR", "material.hdrpEmissiveMappingPlanar", "uv0Fallback", diagnostics);
            DiagnoseActiveKeyword(material, "_EMISSIVE_MAPPING_TRIPLANAR", "material.hdrpEmissiveMappingTriplanar", "uv0Fallback", diagnostics);
            DiagnoseActiveKeyword(material, "_MATERIAL_FEATURE_SUBSURFACE_SCATTERING", "material.hdrpSubsurfaceScattering", "standardPbrFallback", diagnostics);
            DiagnoseActiveKeyword(material, "_MATERIAL_FEATURE_TRANSMISSION", "material.hdrpTransmission", "standardPbrFallback", diagnostics);
            DiagnoseActiveKeyword(material, "_MATERIAL_FEATURE_ANISOTROPY", "material.hdrpAnisotropy", "standardPbrFallback", diagnostics);
            DiagnoseActiveKeyword(material, "_MATERIAL_FEATURE_CLEAR_COAT", "material.hdrpClearCoat", "standardPbrFallback", diagnostics);
            DiagnoseActiveKeyword(material, "_MATERIAL_FEATURE_IRIDESCENCE", "material.hdrpIridescence", "standardPbrFallback", diagnostics);
            DiagnoseActiveKeyword(material, "_MATERIAL_FEATURE_SPECULAR_COLOR", "material.hdrpSpecularColor", "standardPbrFallback", diagnostics);

            DiagnoseNonDefaultRange(material, "_AlphaRemapMin", 0, "material.hdrpAlphaRemap", diagnostics);
            DiagnoseNonDefaultRange(material, "_AlphaRemapMax", 1, "material.hdrpAlphaRemap", diagnostics);
        }

        public virtual bool DiagnoseShaderConversion(
            bool litMaterial,
            ConversionDiagnostics diagnostics)
        {
            switch (ConversionKind)
            {
                case ShaderProfileConversionKind.UnlitParticle:
                    diagnostics.approximated.Add("material.shader.meshBasicSubset");
                    diagnostics.warnings.Add("The Unity shader is reduced to the documented unlit texture, tint, alpha-test, culling, and basic blend subset represented by Three MeshBasicMaterial.");
                    return true;
                case ShaderProfileConversionKind.UnlitNoVertexColor:
                    diagnostics.approximated.Add("material.shader.unlitNoVertexColorToParticleColor");
                    diagnostics.warnings.Add("The source unlit shader does not consume ParticleSystem vertex color. Its base color and emission are represented as a constant Quarks particle color while ParticleSystem color modules remain explicitly inactive.");
                    return true;
                case ShaderProfileConversionKind.VertexLit:
                    diagnostics.approximated.Add("material.shader.vertexLitToStandard");
                    diagnostics.warnings.Add(litMaterial
                        ? "Legacy VertexLit Blended is converted to Three MeshStandardMaterial with base color, emission, texture, alpha state, and mesh normals. Three PBR fragment lighting is an explicit approximation of Unity's legacy per-vertex lighting."
                        : "Legacy VertexLit Blended requires a Mesh renderer for lit Quarks playback. This non-Mesh renderer uses the explicit unlit material subset.");
                    return true;
                case ShaderProfileConversionKind.SyntyParticleLit:
                    diagnostics.approximated.Add(litMaterial
                        ? "material.shader.syntyGenericParticlesLitToThreePbr"
                        : "material.shader.syntyGenericParticlesLitBillboardUnlitFallback");
                    diagnostics.warnings.Add(litMaterial
                        ? "Synty Generic_ParticlesLit is mapped to Three MeshStandardMaterial with authored albedo, normal, metallic, smoothness, emission, alpha-clip, blend, culling, and ParticleSystem vertex-color inputs. Unity URP lighting remains an explicit cross-lighting-model approximation."
                        : "Synty Generic_ParticlesLit uses an unlit Quarks billboard fallback because Quarks billboard batches cannot use PBR materials; authored texture, tint, alpha, blend, culling, soft-particle, and ParticleSystem vertex-color semantics remain mapped where available.");
                    return true;
                case ShaderProfileConversionKind.RegisteredSubset:
                    if (UsesLitMaterial)
                    {
                        diagnostics.approximated.Add(litMaterial
                            ? "material.shader.litProfileToThreePbr"
                            : "material.shader.litProfileBillboardUnlitFallback");
                        diagnostics.warnings.Add(litMaterial
                            ? "The source lit shader profile is converted to Three MeshStandard/Physical material. Mapped PBR parameters remain an explicit cross-lighting-model approximation."
                            : "Stock Quarks cannot light billboard particles with a PBR material. Base color/emission use the documented unlit particle-color fallback; source vertex-color consumption is preserved by profile.");
                        return true;
                    }
                    if (IsSupported)
                    {
                        diagnostics.approximated.Add("material.shader.registeredProfileSubset");
                        diagnostics.warnings.Add("The registered shader profile is converted through its declared material capabilities and custom mapping hook. Shader behavior outside that profile contract remains an explicit approximation.");
                        return true;
                    }
                    return false;
                default:
                    return false;
            }
        }

        public virtual JsonObject BuildParticleCustomDataMetadata(
            ParticleSystem system,
            ConversionDiagnostics diagnostics) => null;

        public override string ToString() => Name;

        protected static bool ShaderNameEquals(string actual, string expected)
        {
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        protected static bool IsEnabled(Material material, params string[] properties)
        {
            if (material == null) return false;
            foreach (var property in properties ?? Array.Empty<string>())
            {
                if (!material.HasProperty(property)) continue;
                return material.GetFloat(property) > 0.5f;
            }
            return false;
        }

        protected static bool IsEnabledOrMissing(Material material, params string[] properties)
        {
            if (material == null) return false;
            var property = FirstProperty(material, properties);
            return property == null || material.GetFloat(property) > 0.5f;
        }

        protected static string FirstProperty(Material material, params string[] properties)
        {
            if (material == null) return null;
            foreach (var property in properties ?? Array.Empty<string>())
            {
                if (material.HasProperty(property)) return property;
            }
            return null;
        }

        protected static bool HasEnabledFloat(Material material, string property)
        {
            return material != null && material.HasProperty(property) && material.GetFloat(property) > 0.5f;
        }

        private bool HasActivePackedMetallicGloss(Material material)
        {
            if (material == null || !SupportsPackedMetallicGloss ||
                !material.HasProperty("_MetallicGlossMap") ||
                material.GetTexture("_MetallicGlossMap") == null)
                return false;
            return material.IsKeywordEnabled("_METALLICGLOSSMAP") ||
                   material.IsKeywordEnabled("_METALLICSPECGLOSSMAP");
        }

        private bool CanMapPackedMetallicGloss(Material material)
        {
            return HasActivePackedMetallicGloss(material) &&
                   !UsesSpecularWorkflow(material) &&
                   !material.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A") &&
                   material.GetTexture("_MetallicGlossMap") is Texture2D;
        }

        private bool UsesSpecularWorkflow(Material material)
        {
            return SpecularWorkflow || (material != null && material.HasProperty("_WorkflowMode") &&
                   Mathf.RoundToInt(material.GetFloat("_WorkflowMode")) == 0);
        }

        private void DiagnosePackedMetallicGloss(
            Material material,
            ConversionDiagnostics diagnostics)
        {
            if (!HasActivePackedMetallicGloss(material) || CanMapPackedMetallicGloss(material)) return;
            diagnostics.unsupported.Add("material.metallicGlossMap");
            diagnostics.approximated.Add("material.metallicGlossMap.scalarFallback");
            if (UsesSpecularWorkflow(material))
            {
                diagnostics.warnings.Add("The packed map is active in a specular workflow. Best-effort keeps scalar PBR parameters; strict export fails because RGB specular plus alpha smoothness requires a separately validated Three specular-map conversion.");
            }
            else if (material.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A"))
            {
                diagnostics.warnings.Add("Smoothness comes from the base-map alpha rather than the packed-map alpha. Best-effort keeps scalar roughness; strict export fails until the cross-texture channel conversion is validated.");
            }
            else if (!(material.GetTexture("_MetallicGlossMap") is Texture2D))
            {
                diagnostics.warnings.Add("The active metallic-gloss source is not a Texture2D. Best-effort keeps scalar PBR parameters; strict export fails.");
            }
            else
            {
                diagnostics.warnings.Add("The active metallic-gloss packing is outside the validated metallic profile. Best-effort keeps scalar PBR parameters; strict export fails.");
            }
        }

        private static bool HasActiveMaterialEmission(Material material)
        {
            if (material == null) return false;
            if (material.HasProperty("_EmisColor") &&
                material.GetColor("_EmisColor").maxColorComponent > 0)
                return true;
            if (material.HasProperty("_EmissionColor") && material.IsKeywordEnabled("_EMISSION") &&
                material.GetColor("_EmissionColor").maxColorComponent > 0)
                return true;
            if (HasEnabledFloat(material, "_Enable_Emission") &&
                material.HasProperty("_Emission_Color") &&
                material.GetColor("_Emission_Color").maxColorComponent > 0)
                return true;
            return material.HasProperty("_EmissiveColor") &&
                   material.GetColor("_EmissiveColor").maxColorComponent > 0;
        }

        private static void DiagnoseActiveKeyword(
            Material material,
            string keyword,
            string field,
            string fallback,
            ConversionDiagnostics diagnostics)
        {
            if (!material.IsKeywordEnabled(keyword)) return;
            diagnostics.unsupported.Add(field);
            diagnostics.approximated.Add(field + "." + fallback);
            diagnostics.warnings.Add(keyword + " is active. Best-effort uses the named " + fallback + "; strict export fails.");
        }

        private static void DiagnoseNonDefaultRange(
            Material material,
            string property,
            float defaultValue,
            string field,
            ConversionDiagnostics diagnostics)
        {
            if (!material.HasProperty(property) ||
                Mathf.Abs(material.GetFloat(property) - defaultValue) <= 0.000001f)
                return;
            diagnostics.unsupported.Add(field);
            diagnostics.approximated.Add(field + ".identityFallback");
            diagnostics.warnings.Add(property + " differs from its identity value. Best-effort omits the remap; strict export fails.");
        }

        private static void DiagnoseActiveToggle(
            Material material,
            string property,
            string field,
            string fallback,
            ConversionDiagnostics diagnostics)
        {
            if (!material.HasProperty(property) || material.GetFloat(property) <= 0.5f) return;
            diagnostics.unsupported.Add(field);
            diagnostics.approximated.Add(field + "." + fallback);
            diagnostics.warnings.Add(property + " is active. Best-effort uses the named " + fallback + "; strict export fails.");
        }

        private static void DiagnoseUnsupportedMap(
            Material material,
            string property,
            string keyword,
            string field,
            ConversionDiagnostics diagnostics)
        {
            if (!material.HasProperty(property) || material.GetTexture(property) == null ||
                !material.IsKeywordEnabled(keyword)) return;
            diagnostics.unsupported.Add(field);
            diagnostics.approximated.Add(field + ".scalarFallback");
            diagnostics.warnings.Add(property + " is active but its channel packing has no direct Three material equivalent in the current offline exporter. Best-effort keeps scalar material values; strict export fails.");
        }

        protected static bool IsSoftParticleMaterial(Material material)
        {
            if (material == null) return false;
            if (material.HasProperty("_SoftParticlesEnabled"))
                return material.GetFloat("_SoftParticlesEnabled") > 0.5f;
            if (material.HasProperty("_InvFade"))
                return QualitySettings.softParticles && material.GetFloat("_InvFade") > 0;
            if (material.IsKeywordEnabled("SOFTPARTICLES_ON")) return true;
            foreach (var keyword in material.shaderKeywords ?? Array.Empty<string>())
            {
                if (string.Equals(keyword, "SOFTPARTICLES_ON", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        protected static bool IsEnabledFloat(Material material, params string[] properties)
        {
            var property = FirstProperty(material, properties);
            return property == null || material.GetFloat(property) > 0.5f;
        }

        protected static bool TexturesReferToSameAsset(Texture first, Texture second)
        {
            if (first == null || second == null) return false;
            if (first == second) return true;
            var firstPath = AssetDatabase.GetAssetPath(first);
            var secondPath = AssetDatabase.GetAssetPath(second);
            return !string.IsNullOrEmpty(firstPath) &&
                   string.Equals(firstPath, secondPath, StringComparison.Ordinal);
        }

        protected static string ResolveColorChannel(Color channel)
        {
            var channels = new[] { channel.r, channel.g, channel.b, channel.a };
            var index = 0;
            var value = channels[0];
            for (var i = 1; i < channels.Length; i++)
            {
                if (channels[i] > value)
                {
                    index = i;
                    value = channels[i];
                }
            }
            return index == 1 ? "g" : index == 2 ? "b" : index == 3 ? "a" : "r";
        }

        protected static void SetCustomBlend(
            MaterialBlendState result,
            int source,
            int destination,
            int equation,
            string fragmentColorMode)
        {
            result.blending = 5;
            result.blendSrc = source;
            result.blendDst = destination;
            result.blendEquation = equation;
            result.fragmentColorMode = fragmentColorMode;
            result.custom = true;
        }

        protected static bool TryMapBlendFactor(int unity, out int three)
        {
            switch ((BlendMode)unity)
            {
                case BlendMode.Zero: three = 200; return true;
                case BlendMode.One: three = 201; return true;
                case BlendMode.SrcColor: three = 202; return true;
                case BlendMode.OneMinusSrcColor: three = 203; return true;
                case BlendMode.SrcAlpha: three = 204; return true;
                case BlendMode.OneMinusSrcAlpha: three = 205; return true;
                case BlendMode.DstAlpha: three = 206; return true;
                case BlendMode.OneMinusDstAlpha: three = 207; return true;
                case BlendMode.DstColor: three = 208; return true;
                case BlendMode.OneMinusDstColor: three = 209; return true;
                case BlendMode.SrcAlphaSaturate: three = 210; return true;
                default: three = 0; return false;
            }
        }

        protected static JsonObject ColorJson(Color color)
        {
            return Json.Object()
                .Add("r", Json.Number(color.r))
                .Add("g", Json.Number(color.g))
                .Add("b", Json.Number(color.b))
                .Add("a", Json.Number(color.a));
        }
    }
}
