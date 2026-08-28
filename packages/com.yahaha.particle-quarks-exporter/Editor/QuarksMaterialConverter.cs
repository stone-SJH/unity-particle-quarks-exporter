using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class MaterialExportSemantics
    {
        public Color sourceColor = Color.white;
        public Color particleColor = Color.white;
        public Color particleColorMultiplier = Color.white;
        public bool consumesParticleColor = true;
        public bool restoreMaterialColor;
        public bool softParticles;
        public float softNearFade;
        public float softFarFade = 1;
        public bool cameraFade;
        public float cameraFadeNear;
        public float cameraFadeFar = 1;
        public float cameraFadeSmoothness = 1;
        public string fragmentColorMode = "stock";
        public string shaderProfileId = "";
        public string shaderProfileVersion = "";
        public string shaderProfileMetadataKey = "";
        public ShaderProfile shaderProfile;
        public string sourceShaderName = "";
        public string shaderRuntimeTier = "stock";
        public string shaderFidelity = "approx";
        public string[] resolvedProperties = Array.Empty<string>();
        public string[] missingProperties = Array.Empty<string>();
        public string[] unmappedProperties = Array.Empty<string>();
        public string[] profileConflicts = Array.Empty<string>();
        public float alphaTest;
        public bool doubleSided;
        public JsonObject alphaMetadata;
        public JsonObject blendMetadata;
        public JsonObject textureUvMetadata;
        public JsonObject shaderParameters;
        public string baseColorChannel = "rgb";
    }

    internal sealed class QuarksMaterialConverter
    {
        private struct PixelProfile
        {
            public byte minRed;
            public byte maxRed;
            public byte minGreen;
            public byte maxGreen;
            public byte minBlue;
            public byte maxBlue;
            public byte minAlpha;
            public byte maxAlpha;

            public bool HasColorVariation => minRed != maxRed || minGreen != maxGreen || minBlue != maxBlue;
            public bool HasAlphaVariation => minAlpha != maxAlpha;
        }

        private readonly string sourcePath;
        private readonly string outputDirectory;
        private readonly int maxTextureSize;
        private readonly bool sourceBuiltInPipeline;
        private readonly Dictionary<string, JsonObject> materials;
        private readonly Dictionary<string, JsonObject> textures;
        private readonly Dictionary<string, JsonObject> images;
        private readonly SortedSet<string> textureFiles;

        internal QuarksMaterialConverter(
            string prefabPath,
            string effectOutputDirectory,
            int textureLimit,
            bool isSourceBuiltInPipeline,
            Dictionary<string, JsonObject> materialArtifacts,
            Dictionary<string, JsonObject> textureArtifacts,
            Dictionary<string, JsonObject> imageArtifacts,
            SortedSet<string> textureArtifactFiles)
        {
            sourcePath = prefabPath;
            outputDirectory = effectOutputDirectory;
            maxTextureSize = textureLimit;
            sourceBuiltInPipeline = isSourceBuiltInPipeline;
            materials = materialArtifacts ?? throw new ArgumentNullException(nameof(materialArtifacts));
            textures = textureArtifacts ?? throw new ArgumentNullException(nameof(textureArtifacts));
            images = imageArtifacts ?? throw new ArgumentNullException(nameof(imageArtifacts));
            textureFiles = textureArtifactFiles ?? throw new ArgumentNullException(nameof(textureArtifactFiles));
        }

        internal string RegisterMaterial(
            ParticleSystemRenderer renderer,
            string path,
            bool useTrailMaterial,
            ConversionDiagnostics diagnostics,
            out MaterialExportSemantics semantics)
        {
            var material = renderer == null
                ? null
                : useTrailMaterial ? renderer.trailMaterial : renderer.sharedMaterial;
            if (useTrailMaterial)
            {
                if (material == null)
                {
                    diagnostics.unsupported.Add("renderer.trailMaterial");
                    diagnostics.approximated.Add("renderer.trailMaterial.transparentFallback");
                    diagnostics.warnings.Add("The active Unity Trail renderer has no trail material in its dedicated renderer slot.");
                }
                else
                {
                    diagnostics.mapped.Add("renderer.trailMaterial");
                }
            }
            var profile = ClassifyMaterialShader(material);
            var profileContext = new ShaderProfileMaterialContext(material, diagnostics, sourceBuiltInPipeline);
            profile.ConfigureMaterial(profileContext);
            var profileDiagnostic = profile.DiagnosticId;
            var preciseProfileId = profile.GetProfileId(material);
            if (!string.IsNullOrEmpty(profileDiagnostic) &&
                (string.IsNullOrEmpty(preciseProfileId) ||
                 string.Equals(profileDiagnostic, preciseProfileId, StringComparison.Ordinal)))
                diagnostics.mapped.Add("material.shaderProfile." + profileDiagnostic);
            if (!string.IsNullOrEmpty(preciseProfileId))
                diagnostics.mapped.Add("material.shaderProfile." + preciseProfileId);
            var color = profileContext.materialColorOverride ?? profile.ResolveMaterialColor(material, diagnostics);
            var emission = profileContext.materialEmissionOverride ?? profile.ResolveMaterialEmission(material, diagnostics);
            var blend = profileContext.blendStateOverride ?? ResolveMaterialBlendState(material, diagnostics);
            var meshRenderer = !useTrailMaterial && renderer != null &&
                               renderer.renderMode == ParticleSystemRenderMode.Mesh;
            var alphaTest = profileContext.alphaTestOverride ?? profile.ResolveAlphaTest(material);
            var alphaMetadata = BuildMaterialAlphaMetadata(material, profile, alphaTest, diagnostics);
            var blendMetadata = BuildMaterialBlendMetadata(material, profile, blend, alphaTest, diagnostics);
            var shaderParameters = profileContext.shaderParametersOverride;
            var sourceLitMaterial = meshRenderer && ProfileUsesLitMaterial(profile);
            var pbrAlphaAtlasFallback = sourceLitMaterial &&
                                        RequiresPbrAlphaAtlasUnlitFallback(renderer, alphaTest);
            var litMaterial = sourceLitMaterial && !pbrAlphaAtlasFallback;
            var consumesParticleColor = ProfileConsumesParticleColor(profile);
            var unlitColor = UnlitParticleColor(color, emission, blend.sourcePremultipliedAlpha);
            if (profileContext.invisibleFallback)
            {
                unlitColor.a = 0;
            }
            var softParticles = ResolveSoftParticleSettings(material, profile, diagnostics);
            var cameraFade = ResolveCameraFadeSettings(material, profile, diagnostics);
            var pairedMaterialProfile = profile.RequiresPairedRuntime(material, blend.fragmentColorMode);
            var doubleSided = profileContext.doubleSidedOverride ?? IsDoubleSidedMaterial(material, profile);
            var materialProfileReport = string.IsNullOrEmpty(preciseProfileId)
                ? !profile.IsSupported && material != null
                    ? BuildMaterialProfileReport(
                        material,
                        useTrailMaterial ? "trail" : "renderer",
                        AssetDatabase.GetAssetPath(material),
                        profile,
                        "unsupported",
                        "unsupported",
                        "not_applicable")
                    : null
                : BuildMaterialProfileReport(
                    material,
                    useTrailMaterial ? "trail" : "renderer",
                    material == null ? "missing" : AssetDatabase.GetAssetPath(material),
                    profile,
                    preciseProfileId,
                    pairedMaterialProfile ? "paired" : "stock",
                    pairedMaterialProfile ||
                    string.Equals(preciseProfileId, "builtin.particleAnimAlphaBlended", StringComparison.Ordinal)
                    ? "exact"
                    : "approx");
            if (materialProfileReport != null)
            {
                materialProfileReport.blendMode = blend.fragmentColorMode;
                materialProfileReport.alphaClip = alphaTest > 0;
                materialProfileReport.softParticles = softParticles.enabled;
                materialProfileReport.doubleSided = doubleSided;
            }
            semantics = new MaterialExportSemantics
            {
                sourceColor = color,
                consumesParticleColor = consumesParticleColor,
                particleColor = litMaterial || consumesParticleColor ? Color.white : unlitColor,
                particleColorMultiplier = litMaterial
                    ? Color.white
                    : consumesParticleColor ? unlitColor : Color.white,
                restoreMaterialColor = litMaterial,
                softParticles = softParticles.enabled,
                softNearFade = softParticles.near,
                softFarFade = softParticles.far,
                cameraFade = !useTrailMaterial && cameraFade.enabled,
                cameraFadeNear = cameraFade.near,
                cameraFadeFar = cameraFade.far,
                cameraFadeSmoothness = cameraFade.smoothness,
                fragmentColorMode = blend.fragmentColorMode,
                baseColorChannel = profileContext.baseColorChannelOverride ??
                                   profile.ResolveBaseColorChannel(material),
                shaderProfileId = preciseProfileId,
                shaderProfileVersion = profile.GetProfileVersion(material),
                shaderProfile = profile,
                shaderProfileMetadataKey = pairedMaterialProfile
                    ? "unity_particle_quarks_exporter.material." + preciseProfileId + "." + profile.GetProfileVersion(material)
                    : "",
                sourceShaderName = material == null || material.shader == null ? "" : material.shader.name,
                shaderRuntimeTier = pairedMaterialProfile ? "paired" : "stock",
                shaderFidelity = pairedMaterialProfile ||
                                 string.Equals(preciseProfileId, "builtin.particleAnimAlphaBlended", StringComparison.Ordinal)
                    ? "exact"
                    : "approx",
                resolvedProperties = materialProfileReport == null ? Array.Empty<string>() : materialProfileReport.resolvedProperties,
                missingProperties = materialProfileReport == null ? Array.Empty<string>() : materialProfileReport.missingProperties,
                unmappedProperties = materialProfileReport == null ? Array.Empty<string>() : materialProfileReport.unmappedProperties,
                profileConflicts = materialProfileReport == null ? Array.Empty<string>() : materialProfileReport.conflicts,
                alphaTest = alphaTest,
                doubleSided = doubleSided,
                alphaMetadata = alphaMetadata,
                blendMetadata = blendMetadata,
                shaderParameters = shaderParameters
            };
            if (materialProfileReport != null)
                diagnostics.materialProfiles.Add(materialProfileReport);
            DiagnoseMaterialShader(
                material,
                useTrailMaterial ? "trail" : "renderer",
                profile,
                litMaterial,
                pbrAlphaAtlasFallback,
                sourceBuiltInPipeline,
                diagnostics);
            profile.DiagnoseCommonMaterialFeatures(material, litMaterial, diagnostics);
            profile.DiagnoseMaterialFeatures(material, litMaterial, diagnostics);
            var materialPath = material == null ? "missing" : AssetDatabase.GetAssetPath(material);
            var id = UnityParticleQuarksStableId.Create(sourcePath, path + ":" + materialPath, "material");
            if (materials.ContainsKey(id)) return id;

            var colorInt = ColorInt(color);
            var emissionInt = ColorInt(emission);
            if (semantics.restoreMaterialColor &&
                (color.r > 1 || color.g > 1 || color.b > 1 || color.a > 1))
            {
                diagnostics.approximated.Add("material.color.stockClampedFallback");
                diagnostics.warnings.Add("The source material tint is HDR. Stock Three material JSON clamps RGB to 8-bit, while the paired SDK restores the original floating-point RGBA from exporter metadata.");
            }
            var transparent = material == null || useTrailMaterial || blend.sourcePremultipliedAlpha ||
                              IsFixedTransparentProfile(profile) || color.a < 0.999f ||
                              (material != null && material.renderQueue >= (int)RenderQueue.Transparent);
            var side = doubleSided ? 2 : 0;
            if (side == 2) diagnostics.mapped.Add("material.doubleSide");
            var zWriteProperty = FirstMaterialProperty(material, "_ZWrite", "_Zwrite", "_ZWriteControl");
            var depthWrite = zWriteProperty != null
                ? material.GetFloat(zWriteProperty) > 0.5f
                : !transparent && blend.blending != 2;
            var specularWorkflow = UsesSpecularWorkflow(material, profile);

            var json = Json.Object()
                .Add("uuid", Json.String(id))
                .Add("type", Json.String(litMaterial
                    ? specularWorkflow ? "MeshPhysicalMaterial" : "MeshStandardMaterial"
                    : "MeshBasicMaterial"))
                .Add("color", Json.Number(litMaterial ? colorInt : 0xffffff))
                .Add("opacity", Json.Number(material == null ? 0 : litMaterial ? color.a : 1))
                .Add("transparent", Json.Boolean(transparent || blend.blending == 2 || blend.custom || blend.customAlpha))
                .Add("blending", Json.Number(blend.blending))
                .Add("premultipliedAlpha", Json.Boolean(litMaterial && blend.sourcePremultipliedAlpha))
                .Add("side", Json.Number(side))
                .Add("alphaTest", Json.Number(alphaTest))
                .Add("depthWrite", Json.Boolean(depthWrite));
            if (blend.custom)
            {
                json.Add("blendSrc", Json.Number(blend.blendSrc))
                    .Add("blendDst", Json.Number(blend.blendDst))
                    .Add("blendEquation", Json.Number(blend.blendEquation));
                if (blend.customAlpha)
                {
                    json.Add("blendSrcAlpha", Json.Number(blend.blendSrcAlpha))
                        .Add("blendDstAlpha", Json.Number(blend.blendDstAlpha))
                        .Add("blendEquationAlpha", Json.Number(blend.blendEquationAlpha));
                }
            }
            if (litMaterial)
            {
                var packedMetallicGloss = CanMapPackedMetallicGloss(material, profile);
                var metalness = packedMetallicGloss ? 1 : ResolveMetalness(material, specularWorkflow);
                var smoothness = packedMetallicGloss ? 0 : ResolveSmoothness(material);
                json.Add("emissive", Json.Number(emissionInt))
                    .Add("emissiveIntensity", Json.Number(1))
                    .Add("metalness", Json.Number(metalness))
                    .Add("roughness", Json.Number(1 - smoothness));
                if (specularWorkflow)
                {
                    var specular = material != null && material.HasProperty("_SpecColor")
                        ? material.GetColor("_SpecColor")
                        : Color.white;
                    json.Add("specularColor", Json.Number(ColorInt(specular)))
                        .Add("specularIntensity", Json.Number(Mathf.Clamp01(specular.maxColorComponent)))
                        .Add("ior", Json.Number(1.5f));
                    diagnostics.mapped.Add("material.standardSpecularParameters");
                }
                diagnostics.mapped.Add("material.standardPbrParameters");
            }

            var mainTextureProperty = profileContext.mainTexturePropertyOverride ??
                                      profile.ResolveMainTextureProperty(material);
            Texture sourceMainTexture = string.IsNullOrEmpty(mainTextureProperty)
                ? null
                : material.GetTexture(mainTextureProperty);
            if (!useTrailMaterial && renderer != null)
            {
                var particleSystem = renderer.GetComponent<ParticleSystem>();
                var sheet = particleSystem == null ? default(ParticleSystem.TextureSheetAnimationModule) : particleSystem.textureSheetAnimation;
                if (particleSystem != null && sheet.enabled &&
                    sheet.mode == ParticleSystemAnimationMode.Sprites && sheet.spriteCount > 0)
                {
                    Texture2D spriteTexture = null;
                    for (var spriteIndex = 0; spriteIndex < sheet.spriteCount; spriteIndex++)
                    {
                        var sprite = sheet.GetSprite(spriteIndex);
                        if (sprite == null || sprite.texture == null) continue;
                        if (spriteTexture == null) spriteTexture = sprite.texture;
                        else if (sprite.texture != spriteTexture)
                        {
                            diagnostics.unsupported.Add("textureSheetAnimation.sprites.multipleTextures");
                            diagnostics.approximated.Add("textureSheetAnimation.sprites.firstTextureFallback");
                            diagnostics.warnings.Add("Unity Sprite-list Texture Sheet Animation references more than one texture. Unity particle batches require one texture atlas; best-effort uses the first valid sprite texture and strict export fails.");
                        }
                    }
                    if (spriteTexture != null)
                    {
                        sourceMainTexture = spriteTexture;
                        diagnostics.mapped.Add("textureSheetAnimation.sprites.textureOverride");
                    }
                }
            }
            if (sourceMainTexture is Texture2D mainTexture)
            {
                if (!string.IsNullOrEmpty(mainTextureProperty) &&
                    (material.GetTextureOffset(mainTextureProperty) != Vector2.zero ||
                     material.GetTextureScale(mainTextureProperty) != Vector2.one))
                {
                    diagnostics.mapped.Add("material.mainTextureTransform.metadata.v1");
                }
                var textureId = RegisterTexture(mainTexture, diagnostics);
                if (!string.IsNullOrEmpty(textureId)) json.Add("map", Json.String(textureId));
            }
            else if (sourceMainTexture != null)
            {
                diagnostics.unsupported.Add("material.mainTexture.dimension");
                diagnostics.approximated.Add("material.mainTexture.dimension.untexturedFallback");
                diagnostics.warnings.Add("The source main texture is " + sourceMainTexture.dimension + ". Best-effort emits the documented untextured fallback; strict export fails because stock Quarks expects a Texture2D map.");
            }
            else if (IsUnityDefaultParticleMaterial(material))
            {
                var builtinTexture = FindUnityDefaultParticleTexture();
                json.Add("map", Json.String(builtinTexture == null
                    ? RegisterUnityDefaultParticleFallback(diagnostics)
                    : RegisterTexture(builtinTexture, diagnostics)));
            }
            var alphaTextureProperty = ResolveAlphaFactorTextureProperty(
                material,
                profile,
                mainTextureProperty,
                sourceMainTexture);
            semantics.textureUvMetadata = BuildMaterialTextureUvMetadata(
                material,
                profile,
                mainTextureProperty,
                alphaTextureProperty,
                diagnostics);
            if (!string.IsNullOrEmpty(alphaTextureProperty) && material.GetTexture(alphaTextureProperty) is Texture2D alphaTexture)
            {
                var alphaTextureId = RegisterTexture(alphaTexture, diagnostics);
                if (!string.IsNullOrEmpty(alphaTextureId))
                {
                    // Three's alphaMap shader samples the red channel used by
                    // the configured shader profile.
                    json.Add("alphaMap", Json.String(alphaTextureId));
                    json.Add("alphaMapChannel", Json.String(profile.ResolveAlphaTextureChannel(material, alphaTextureProperty)));
                    diagnostics.mapped.Add("material.alpha.textureMap." + alphaTextureProperty.TrimStart('_'));
                }
            }
            // Keep every authored opacity texture declared by the profile
            // available to the paired runtime. Three's stock material has one
            // alphaMap slot, while profiles such as Hovl Blend_TwoSides
            // multiply Mask and Noise before clipping. The first entry remains
            // the backward-compatible alphaMap; additional entries travel
            // through material userData.
            var additionalAlphaMaps = Json.Array();
            foreach (var property in profile.AlphaFactorTextureProperties ?? Array.Empty<string>())
            {
                if (material == null ||
                    string.Equals(property, alphaTextureProperty, StringComparison.Ordinal) ||
                    !material.HasProperty(property) || material.GetTexture(property) is not Texture2D factorTexture ||
                    !IsAlphaTextureFactorActive(material, profile, property) ||
                    TexturesReferToSameAsset(sourceMainTexture, factorTexture)) continue;
                var factorTextureId = RegisterTexture(factorTexture, diagnostics);
                if (string.IsNullOrEmpty(factorTextureId)) continue;
                additionalAlphaMaps.Add(Json.Object()
                    .Add("property", Json.String(property))
                    .Add("texture", Json.String(factorTextureId))
                    .Add("channel", Json.String(profile.ResolveAlphaTextureChannel(material, property))));
                diagnostics.mapped.Add("material.alpha.textureMap." + property.TrimStart('_'));
            }
            json.Add("userData", Json.Object().Add("unityParticleQuarksAlphaMaps", additionalAlphaMaps));
            if (litMaterial) RegisterLitMaterialMaps(material, profile, json, diagnostics);
            materials[id] = json;
            if (material != null) diagnostics.mapped.Add("material.base");
            if (blend.blending == 2) diagnostics.mapped.Add("material.additive");
            if (!string.Equals(blend.fragmentColorMode, "stock", StringComparison.Ordinal))
            {
                diagnostics.mapped.Add("material.fragmentColorRuntime." + blend.fragmentColorMode);
                diagnostics.approximated.Add("material.fragmentColorRuntime.stockShaderFallback");
                diagnostics.warnings.Add("The paired SDK applies the source shader's documented fragment-color formula for profile " + blend.fragmentColorMode + ". Stock Quarks remains a named texture-times-particle-color fallback.");
            }
            if (softParticles.enabled)
            {
                diagnostics.approximated.Add("material.softParticles");
                diagnostics.warnings.Add("Soft particles preserve the source fade-distance formula but still require a compatible browser depth texture and matching eye-depth reconstruction.");
            }
            return id;
        }

        private static JsonObject BuildMaterialTextureUvMetadata(
            Material material,
            ShaderProfile profile,
            string mainTextureProperty,
            string alphaTextureProperty,
            ConversionDiagnostics diagnostics)
        {
            if (material == null) return null;
            var main = BuildTextureUvMetadataEntry(
                material,
                profile,
                mainTextureProperty,
                "main",
                diagnostics);
            var alpha = BuildTextureUvMetadataEntry(
                material,
                profile,
                alphaTextureProperty,
                "alpha",
                diagnostics);
            if (main == null && alpha == null) return null;
            var metadata = Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.material_texture_uv.v1"));
            if (main != null) metadata.Add("main", main);
            if (alpha != null) metadata.Add("alpha", alpha);
            return metadata;
        }

        private static JsonObject BuildTextureUvMetadataEntry(
            Material material,
            ShaderProfile profile,
            string textureProperty,
            string role,
            ConversionDiagnostics diagnostics)
        {
            if (string.IsNullOrEmpty(textureProperty) ||
                !material.HasProperty(textureProperty) ||
                material.GetTexture(textureProperty) == null)
                return null;
            var scale = material.GetTextureScale(textureProperty);
            var offset = material.GetTextureOffset(textureProperty);
            profile.TryResolveTexturePanning(material, textureProperty, out var panning, out var panningLabel);
            if (scale == Vector2.one && offset == Vector2.zero && panning == Vector2.zero) return null;
            diagnostics.mapped.Add("material.textureUv." + role + ".metadata.v1");
            if (panning != Vector2.zero)
            {
                diagnostics.mapped.Add("material.textureUv." + role + ".panning." +
                                       (string.IsNullOrEmpty(panningLabel) ? "custom" : panningLabel));
            }
            return Json.Object()
                .Add("property", Json.String(textureProperty))
                .Add("scale", Json.Array().Add(Json.Number(scale.x)).Add(Json.Number(scale.y)))
                .Add("offset", Json.Array().Add(Json.Number(offset.x)).Add(Json.Number(offset.y)))
                .Add("panning", Json.Array().Add(Json.Number(panning.x)).Add(Json.Number(panning.y)));
        }

        private static string ResolveAlphaFactorTextureProperty(
            Material material,
            ShaderProfile profile,
            string mainTextureProperty,
            Texture mainTexture)
        {
            if (material == null) return string.Empty;
            var candidates = profile.AlphaFactorTextureProperties;
            foreach (var property in candidates)
            {
                if (string.Equals(property, mainTextureProperty, StringComparison.Ordinal) ||
                    !material.HasProperty(property) || material.GetTexture(property) == null) continue;
                var candidateTexture = material.GetTexture(property);
                // Unity materials commonly expose the same texture through
                // MainTex/MainTexture and AlphaOverride. Active Piloto alpha
                // overrides are folded into the base alpha channel metadata;
                // sampling the same image again as Three's alphaMap would
                // multiply opacity twice.
                if (TexturesReferToSameAsset(mainTexture, candidateTexture)) continue;
                if (!IsAlphaTextureFactorActive(material, profile, property)) continue;
                return property;
            }
            return string.Empty;
        }

        private static bool IsAlphaTextureFactorActive(
            Material material,
            ShaderProfile profile,
            string property)
        {
            return material != null && profile.IsAlphaTextureFactorActive(material, property);
        }

        private static Color UnlitParticleColor(
            Color color,
            Color emission,
            bool premultiplied)
        {
            var alpha = Mathf.Clamp01(color.a);
            var baseMultiplier = premultiplied ? alpha : 1;
            return new Color(
                color.r * baseMultiplier + emission.r,
                color.g * baseMultiplier + emission.g,
                color.b * baseMultiplier + emission.b,
                alpha);
        }

        private static int ColorInt(Color color)
        {
            return (Mathf.RoundToInt(Mathf.Clamp01(color.r) * 255) << 16) |
                   (Mathf.RoundToInt(Mathf.Clamp01(color.g) * 255) << 8) |
                   Mathf.RoundToInt(Mathf.Clamp01(color.b) * 255);
        }

        private static JsonObject BuildMaterialAlphaMetadata(
            Material material,
            ShaderProfile profile,
            float alphaTest,
            ConversionDiagnostics diagnostics)
        {
            if (material == null) return null;
            var metadata = Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.material.alpha.v1"))
                .Add("profile", Json.String(profile.DiagnosticId))
                .Add("materialColorAlpha", Json.Boolean(true))
                .Add("particleColorAlpha", Json.Boolean(ProfileConsumesParticleColor(profile)));

            var textureProperty = ResolveMainTextureProperty(material, profile);
            var mainTexture = string.IsNullOrEmpty(textureProperty) ? null : material.GetTexture(textureProperty);
            if (!string.IsNullOrEmpty(textureProperty) && mainTexture != null)
            {
                var baseMetadata = Json.Object()
                    .Add("property", Json.String(textureProperty))
                    .Add("channel", Json.String(profile.ResolveAlphaChannel(material, mainTexture)));
                var alphaWeights = profile.ResolveAlphaChannelWeights(material, mainTexture);
                if (alphaWeights != null)
                    baseMetadata.Add("weights", ColorJson(alphaWeights.Value));
                var colorScale = profile.ResolveMainTextureColorScale(material);
                if (colorScale != null)
                    baseMetadata.Add("colorScale", ColorJson(colorScale.Value));
                metadata.Add("base", baseMetadata);
                diagnostics.mapped.Add("material.alpha.baseTexture");
                if (profile.UsesSameTextureAlphaOverride(material, mainTexture))
                    diagnostics.mapped.Add("material.alpha.baseTexture.alphaOverrideChannel");
            }
            else
            {
                metadata.Add("base", Json.Object().Add("source", Json.String("constant")));
                if (!string.IsNullOrEmpty(textureProperty))
                    diagnostics.approximated.Add("material.alpha.baseTexture.nullFallback");
            }

            var factors = Json.Array();
            foreach (var property in new[] { "_AlphaOverride", "_Opacity", "_Alpha", "_AlphaIntensity" })
            {
                if (!material.HasProperty(property) ||
                    (MaterialHasTextureProperty(material, property) && material.GetTexture(property) != null)) continue;
                if (!profile.IsAlphaTextureFactorActive(material, property) &&
                    string.Equals(property, "_AlphaOverride", StringComparison.Ordinal)) continue;
                var scalar = material.GetFloat(property);
                factors.Add(Json.Object()
                    .Add("source", Json.String("property"))
                    .Add("property", Json.String(property))
                    .Add("value", Json.Number(scalar)));
                diagnostics.mapped.Add("material.alpha." + property.TrimStart('_'));
            }
            var factorProperty = ResolveAlphaFactorTextureProperty(material, profile, textureProperty, mainTexture);
            if (!string.IsNullOrEmpty(factorProperty))
            {
                metadata.Add("factorChannel", Json.String(profile.ResolveAlphaTextureChannel(material, factorProperty)));
                var factorWeights = profile.ResolveAlphaTextureChannelWeights(material, factorProperty);
                if (factorWeights != null)
                    metadata.Add("factorWeights", ColorJson(factorWeights.Value));
            }
            foreach (var property in profile.AlphaFactorTextureProperties ?? Array.Empty<string>())
            {
                if (!material.HasProperty(property) || material.GetTexture(property) == null) continue;
                if (mainTexture != null && material.GetTexture(property) == mainTexture) continue;
                if (!IsAlphaTextureFactorActive(material, profile, property)) continue;
                factors.Add(Json.Object()
                    .Add("source", Json.String("texture"))
                    .Add("property", Json.String(property))
                    .Add("channel", Json.String(profile.ResolveAlphaTextureChannel(material, property))));
                diagnostics.mapped.Add("material.alpha.texture." + property.TrimStart('_'));
            }
            metadata.Add("multiply", factors);
            metadata.Add("clip", Json.Object()
                .Add("enabled", Json.Boolean(alphaTest > 0))
                .Add("threshold", Json.Number(Mathf.Max(0, alphaTest))));
            return metadata;
        }

        private static bool MaterialHasTextureProperty(Material material, string property)
        {
            return material != null &&
                   !string.IsNullOrEmpty(property) &&
                   material.GetTexturePropertyNames().Contains(property, StringComparer.Ordinal);
        }

        private static JsonObject BuildMaterialBlendMetadata(
            Material material,
            ShaderProfile profile,
            MaterialBlendState blend,
            float alphaTest,
            ConversionDiagnostics diagnostics)
        {
            if (material == null || blend == null) return null;
            var zWriteProperty = FirstMaterialProperty(material, "_ZWrite", "_Zwrite", "_ZWriteControl");
            var zWrite = zWriteProperty == null
                ? !((blend.blending == 2) || blend.custom)
                : material.GetFloat(zWriteProperty) > 0.5f;
            var metadata = Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.material.blend.v1"))
                .Add("profile", Json.String(profile.DiagnosticId))
                .Add("mode", Json.String(BlendModeName(blend)))
                .Add("src", Json.Number(blend.blendSrc))
                .Add("dst", Json.Number(blend.blendDst))
                .Add("equation", Json.Number(blend.blendEquation))
                .Add("srcAlpha", Json.Number(blend.blendSrcAlpha))
                .Add("dstAlpha", Json.Number(blend.blendDstAlpha))
                .Add("equationAlpha", Json.Number(blend.blendEquationAlpha))
                .Add("customAlpha", Json.Boolean(blend.customAlpha))
                .Add("premultiplied", Json.Boolean(blend.sourcePremultipliedAlpha))
                .Add("zWrite", Json.Boolean(zWrite))
                .Add("alphaTest", Json.Number(Mathf.Max(0, alphaTest)));
            diagnostics.mapped.Add("material.blend.metadata");
            if (zWriteProperty != null) diagnostics.mapped.Add("material.zWrite." + zWriteProperty.TrimStart('_'));
            return metadata;
        }

        private static bool TexturesReferToSameAsset(Texture first, Texture second)
        {
            if (first == null || second == null) return false;
            if (first == second) return true;
            var firstPath = AssetDatabase.GetAssetPath(first);
            var secondPath = AssetDatabase.GetAssetPath(second);
            return !string.IsNullOrEmpty(firstPath) &&
                   string.Equals(firstPath, secondPath, StringComparison.Ordinal);
        }

        private static string FirstMaterialProperty(Material material, params string[] names)
        {
            if (material == null) return null;
            foreach (var name in names ?? Array.Empty<string>())
                if (material.HasProperty(name)) return name;
            return null;
        }

        private static string BlendModeName(MaterialBlendState blend)
        {
            if (blend == null) return "normal";
            if (blend.blending == 2) return "additive";
            if (blend.fragmentColorMode == "legacyMultiply" || blend.fragmentColorMode == "legacyMultiplyDouble") return "multiply";
            if (blend.sourcePremultipliedAlpha) return "premultiplied";
            return blend.custom ? "custom" : "normal";
        }

        private static bool RequiresPbrAlphaAtlasUnlitFallback(
            ParticleSystemRenderer renderer,
            float alphaTest)
        {
            if (renderer == null || alphaTest <= 0) return false;
            var particleSystem = renderer.GetComponent<ParticleSystem>();
            if (particleSystem == null) return false;
            var sheet = particleSystem.textureSheetAnimation;
            return sheet.enabled &&
                   sheet.mode == ParticleSystemAnimationMode.Grid &&
                   (sheet.numTilesX > 1 || sheet.numTilesY > 1);
        }

        private static ShaderProfile ClassifyMaterialShader(Material material)
        {
            return ShaderProfileRegistry.Resolve(material);
        }

        private static ShaderProfile ClassifyMaterialShaderName(string shaderName)
        {
            return ShaderProfileRegistry.ResolveShaderName(shaderName);
        }

        private bool IsDoubleSidedMaterial(Material material, ShaderProfile profile)
        {
            if (material == null) return true;
            var cullProperty = ResolveCullPropertyName(
                profile,
                sourceBuiltInPipeline,
                material.HasProperty("_BUILTIN_CullMode"),
                material.HasProperty("_Cull"),
                material.HasProperty("_CullMode"));
            if (!string.IsNullOrEmpty(cullProperty))
                return material.GetInt(cullProperty) == (int)CullMode.Off;

            return profile.DoubleSidedByDefault;
        }

        private static string ResolveCullPropertyName(
            ShaderProfile profile,
            bool builtInPipeline,
            bool hasBuiltInCullMode,
            bool hasCull,
            bool hasCullMode)
        {
            return profile.ResolveCullPropertyName(
                builtInPipeline,
                hasBuiltInCullMode,
                hasCull,
                hasCullMode);
        }

        private static void DiagnoseMaterialShader(
            Material material,
            string materialSlot,
            ShaderProfile profile,
            bool litMaterial,
            bool pbrAlphaAtlasFallback,
            bool sourceBuiltInPipeline,
            ConversionDiagnostics diagnostics)
        {
            if (material == null) return;
            var shaderName = material.shader == null ? string.Empty : material.shader.name;
            if (string.IsNullOrEmpty(shaderName) || EqualsShader(shaderName, "Hidden/InternalErrorShader"))
            {
                var resolvedShaderName = string.IsNullOrEmpty(shaderName) ? "<missing>" : shaderName;
                var materialPath = AssetDatabase.GetAssetPath(material) ?? string.Empty;
                diagnostics.unsupported.Add("material.shaderBehavior");
                diagnostics.fatalUnsupported.Add("material.shaderResolution");
                diagnostics.shaderResolutionFailures.Add(new UnityParticleQuarksShaderResolutionFailure
                {
                    materialName = material.name ?? string.Empty,
                    materialAssetPath = materialPath,
                    materialSlot = materialSlot,
                    resolvedShaderName = resolvedShaderName,
                    failureKind = string.IsNullOrEmpty(shaderName) ? "missing_shader" : "internal_error_shader"
                });
                diagnostics.warnings.Add("Shader '" + resolvedShaderName + "' on material '" +
                    (string.IsNullOrEmpty(materialPath) ? material.name : materialPath) +
                    "' is not a resolved source shader. The import project must restore the source render-pipeline dependency before VFX conversion; no fallback JSON is published.");
                return;
            }
            if (pbrAlphaAtlasFallback)
            {
                diagnostics.approximated.Add("material.shader.pbrAlphaAtlasUnlitFallback");
                diagnostics.warnings.Add("Quarks 0.17.1 Mesh PBR batches do not preserve alpha-clipped Grid Texture Sheet sampling reliably. The exporter uses MeshBasicMaterial for this narrow combination while preserving the authored texture, tint, ParticleSystem color, alpha clip, culling, and Texture Sheet Animation; source lighting is an explicit approximation.");
                return;
            }
            if (profile.DiagnoseShaderConversion(litMaterial, diagnostics)) return;

            diagnostics.unsupported.Add("material.shaderBehavior");
            diagnostics.approximated.Add("material.shaderBehavior.meshBasicFallback");
            diagnostics.shaderProfileGaps.Add(BuildShaderProfileGap(
                material,
                materialSlot,
                sourceBuiltInPipeline ? "default" : "urp"));
            diagnostics.warnings.Add("Shader '" + (string.IsNullOrEmpty(shaderName) ? "<missing>" : shaderName) + "' is outside the validated basic particle-shader set. Best-effort explicitly keeps only known texture/tint aliases, alpha-test, culling, and serialized blend factors when exposed; otherwise it uses normal alpha blending. Strict export fails.");
        }

        private static UnityParticleQuarksShaderProfileGap BuildShaderProfileGap(
            Material material,
            string materialSlot,
            string sourcePipeline)
        {
            var shader = material == null ? null : material.shader;
            var shaderName = shader == null ? string.Empty : shader.name ?? string.Empty;
            var properties = new List<string>();
            if (shader != null)
            {
                try
                {
                    var count = ShaderUtil.GetPropertyCount(shader);
                    for (var index = 0; index < count; index++)
                    {
                        var name = ShaderUtil.GetPropertyName(shader, index);
                        if (!string.IsNullOrEmpty(name)) properties.Add(name);
                    }
                }
                catch (Exception)
                {
                    properties.Add("<property-introspection-failed>");
                }
            }
            properties.Sort(StringComparer.Ordinal);
            var keywords = (material == null ? Array.Empty<string>() : material.shaderKeywords ?? Array.Empty<string>())
                .Where(item => !string.IsNullOrEmpty(item))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            var fingerprintInput = shaderName + "\n" + sourcePipeline + "\n" +
                                   string.Join("\n", properties) + "\n" + string.Join("\n", keywords);
            var materialPath = material == null ? string.Empty : AssetDatabase.GetAssetPath(material) ?? string.Empty;
            return new UnityParticleQuarksShaderProfileGap
            {
                shaderName = shaderName,
                shaderFingerprint = UnityParticleQuarksStableId.Hash(fingerprintInput),
                sourcePipeline = sourcePipeline,
                materialName = material == null ? string.Empty : material.name ?? string.Empty,
                materialAssetPath = materialPath,
                materialSlot = materialSlot ?? string.Empty,
                properties = properties.ToArray(),
                keywords = keywords
            };
        }

        private static bool EqualsShader(string actual, string expected) =>
            string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

        private static UnityParticleQuarksMaterialProfileReport BuildMaterialProfileReport(
            Material material,
            string materialSlot,
            string materialAssetPath,
            ShaderProfile profile,
            string profileId,
            string runtimeTier,
            string fidelity)
        {
            var aliases = profile.GetPropertyAliases(material);
            var resolved = new List<string>();
            var missing = new List<string>();
            foreach (var alias in aliases)
            {
                if (material != null && material.HasProperty(alias)) resolved.Add(alias);
                else missing.Add(alias);
            }
            var conflicts = new List<string>();
            if (material != null && material.HasProperty("_BaseMap") && material.HasProperty("_MainTex"))
                conflicts.Add("baseTexture:_BaseMap+_MainTex");
            if (material != null && material.HasProperty("_BaseColor") && material.HasProperty("_Color"))
                conflicts.Add("baseTint:_BaseColor+_Color");
            var unmapped = new List<string>();
            if (material != null)
            {
                foreach (var property in new[] { "_DetailAlbedoMap", "_DistortionMap", "_DistortionStrength", "_FlipbookBlending", "_VertexStreams" })
                {
                    if (material.HasProperty(property)) unmapped.Add(property);
                }
            }
            var shaderName = material == null || material.shader == null ? "" : material.shader.name;
            return new UnityParticleQuarksMaterialProfileReport
            {
                materialSlot = materialSlot,
                materialName = material == null ? "" : material.name,
                materialAssetPath = materialAssetPath ?? "",
                sourceShader = shaderName,
                profileId = profileId,
                profileVersion = profile.GetProfileVersion(material),
                runtimeTier = runtimeTier,
                fidelity = fidelity,
                consumesParticleColor = profile.ConsumesParticleColor,
                meshPbr = profile.UsesLitMaterial,
                resolvedProperties = resolved.ToArray(),
                missingProperties = missing.ToArray(),
                unmappedProperties = unmapped.ToArray(),
                conflicts = conflicts.ToArray()
            };
        }

        private static bool ProfileUsesLitMaterial(ShaderProfile profile)
        {
            return profile.UsesLitMaterial;
        }

        private static bool ProfileConsumesParticleColor(ShaderProfile profile)
        {
            return profile.ConsumesParticleColor;
        }

        private static bool IsFixedTransparentProfile(ShaderProfile profile)
        {
            return profile.FixedTransparent;
        }

        private static MaterialBlendState ResolveMaterialBlendState(
            Material material,
            ConversionDiagnostics diagnostics)
        {
            var result = new MaterialBlendState();
            if (material == null) return result;
            var sourceProperty = FirstMaterialProperty(material, "_SrcBlend", "_AlphaSrcBlend");
            var destinationProperty = FirstMaterialProperty(material, "_DstBlend", "_AlphaDstBlend");
            if (sourceProperty != null && destinationProperty != null)
            {
                if (TryMapBlendFactor(material.GetInt(sourceProperty), out var source) &&
                    TryMapBlendFactor(material.GetInt(destinationProperty), out var destination) &&
                    TryMapBlendEquation(
                        material.HasProperty("_BlendOp") ? material.GetInt("_BlendOp") :
                        material.HasProperty("_BlendOpAlpha") ? material.GetInt("_BlendOpAlpha") : 0,
                        out var equation))
                {
                    if (material.renderQueue >= (int)RenderQueue.Transparent ||
                        material.GetInt(sourceProperty) != (int)BlendMode.One ||
                        material.GetInt(destinationProperty) != (int)BlendMode.Zero)
                    {
                        SetCustomBlend(result, source, destination, equation, "stock");
                        var alphaSourceProperty = FirstMaterialProperty(material, "_AlphaSrcBlend");
                        var alphaDestinationProperty = FirstMaterialProperty(material, "_AlphaDstBlend");
                        if (alphaSourceProperty != null && alphaDestinationProperty != null &&
                            TryMapBlendFactor(material.GetInt(alphaSourceProperty), out var alphaSource) &&
                            TryMapBlendFactor(material.GetInt(alphaDestinationProperty), out var alphaDestination) &&
                            TryMapBlendEquation(
                                material.HasProperty("_BlendOpAlpha") ? material.GetInt("_BlendOpAlpha") : 0,
                                out var alphaEquation))
                        {
                            result.blendSrcAlpha = alphaSource;
                            result.blendDstAlpha = alphaDestination;
                            result.blendEquationAlpha = alphaEquation;
                            result.customAlpha = true;
                            diagnostics.mapped.Add("material.alphaBlendFactors");
                        }
                        diagnostics.mapped.Add("material.blendFactors");
                    }
                }
                else
                {
                    diagnostics.unsupported.Add("material.blendState");
                    diagnostics.approximated.Add("material.blendState.normalAlphaFallback");
                    diagnostics.warnings.Add("The source material uses a blend factor or equation outside the Three material blend-state set. Best-effort uses normal alpha blending; strict export fails.");
                }
            }
            else if (IsAdditive(material))
            {
                result.blending = 2;
            }
            result.sourcePremultipliedAlpha = IsPremultipliedAlpha(material);
            return result;
        }


        private static void SetCustomBlend(
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
            result.custom = true;
            result.fragmentColorMode = fragmentColorMode;
        }

        private static bool TryMapBlendFactor(int unity, out int three)
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

        private static bool TryMapBlendEquation(int unity, out int three)
        {
            switch ((BlendOp)unity)
            {
                case BlendOp.Add: three = 100; return true;
                case BlendOp.Subtract: three = 101; return true;
                case BlendOp.ReverseSubtract: three = 102; return true;
                case BlendOp.Min: three = 103; return true;
                case BlendOp.Max: three = 104; return true;
                default: three = 0; return false;
            }
        }

        private static (bool enabled, float near, float far) ResolveSoftParticleSettings(
            Material material,
            ShaderProfile profile,
            ConversionDiagnostics diagnostics)
        {
            if (!profile.TryResolveSoftParticleSettings(material, diagnostics, out var profileSettings))
                return (false, 0, 1);
            return (true, profileSettings.near, profileSettings.far);
        }

        private static (bool enabled, float near, float far, float smoothness) ResolveCameraFadeSettings(
            Material material,
            ShaderProfile profile,
            ConversionDiagnostics diagnostics)
        {
            if (!profile.TryResolveCameraFadeSettings(material, diagnostics, out var profileSettings))
            {
                return (false, 0, 1, 1);
            }
            return (true, profileSettings.near, profileSettings.far, profileSettings.smoothness);
        }

        private static bool UsesSpecularWorkflow(Material material, ShaderProfile profile)
        {
            if (profile.SpecularWorkflow) return true;
            return material != null && material.HasProperty("_WorkflowMode") &&
                   Mathf.RoundToInt(material.GetFloat("_WorkflowMode")) == 0;
        }

        private static float ResolveMetalness(Material material, bool specularWorkflow)
        {
            if (specularWorkflow || material == null || !material.HasProperty("_Metallic")) return 0;
            return Mathf.Clamp01(material.GetFloat("_Metallic"));
        }

        private static float ResolveSmoothness(Material material)
        {
            if (material == null) return 0;
            if (material.HasProperty("_Glossiness")) return Mathf.Clamp01(material.GetFloat("_Glossiness"));
            if (material.HasProperty("_Smoothness")) return Mathf.Clamp01(material.GetFloat("_Smoothness"));
            return 0;
        }

        private static string ResolveMainTextureProperty(Material material, ShaderProfile profile)
        {
            return profile.ResolveMainTextureProperty(material);
        }

        private void RegisterLitMaterialMaps(
            Material material,
            ShaderProfile profile,
            JsonObject json,
            ConversionDiagnostics diagnostics)
        {
            if (material == null) return;
            var mapSettings = profile.GetLitMapSettings(material);
            var normalProperty = mapSettings.normalMapProperty;
            var normalScaleProperty = mapSettings.normalScaleProperty;
            if (material.IsKeywordEnabled("_NORMALMAP") && material.HasProperty(normalProperty) &&
                !mapSettings.objectSpaceNormal)
            {
                RegisterOptionalMaterialMap(material, normalProperty, "normalMap", diagnostics);
                var texture = material.GetTexture(normalProperty) as Texture2D;
                if (texture != null)
                {
                    var textureId = RegisterTexture(texture, diagnostics);
                    if (!string.IsNullOrEmpty(textureId))
                    {
                        var scale = material.HasProperty(normalScaleProperty)
                            ? material.GetFloat(normalScaleProperty)
                            : 1;
                        json.Add("normalMap", Json.String(textureId))
                            .Add("normalScale", Json.Array().Add(Json.Number(scale)).Add(Json.Number(scale)));
                        diagnostics.approximated.Add("material.normalMap.tangentBasis");
                        diagnostics.warnings.Add("The normal-map texels and scalar strength are mapped. Unity and Three tangent-basis reconstruction under reflected particle geometry remains a documented approximation.");
                    }
                }
            }
            if (mapSettings.objectSpaceNormal && material.GetTexture(normalProperty) != null)
            {
                diagnostics.unsupported.Add("material.normalMap.objectSpace");
                diagnostics.approximated.Add("material.normalMap.objectSpace.omittedFallback");
                diagnostics.warnings.Add("HDRP object-space normal maps cannot be represented by Three's tangent-space normalMap. Best-effort omits the map; strict export fails.");
            }

            var emissionProperty = mapSettings.emissionMapProperty;
            var emissionIsActive = mapSettings.emissionMapActive;
            if (emissionIsActive)
            {
                RegisterOptionalMaterialMap(material, emissionProperty, "emissiveMap", diagnostics);
                var texture = material.GetTexture(emissionProperty) as Texture2D;
                if (texture != null)
                {
                    var textureId = RegisterTexture(texture, diagnostics);
                    if (!string.IsNullOrEmpty(textureId))
                    {
                        json.Add("emissiveMap", Json.String(textureId));
                        diagnostics.mapped.Add("material.emissiveMap");
                    }
                }
            }

            if (CanMapPackedMetallicGloss(material, profile))
            {
                RegisterPackedMetallicGlossMaps(material, profile, json, diagnostics);
            }
        }

        private static bool SupportsPackedMetallicGloss(ShaderProfile profile)
        {
            return profile.SupportsPackedMetallicGloss;
        }

        private static bool HasActivePackedMetallicGloss(Material material, ShaderProfile profile)
        {
            if (material == null || !SupportsPackedMetallicGloss(profile) ||
                !material.HasProperty("_MetallicGlossMap") ||
                material.GetTexture("_MetallicGlossMap") == null)
            {
                return false;
            }
            return material.IsKeywordEnabled("_METALLICGLOSSMAP") ||
                   material.IsKeywordEnabled("_METALLICSPECGLOSSMAP");
        }

        private static bool CanMapPackedMetallicGloss(Material material, ShaderProfile profile)
        {
            return HasActivePackedMetallicGloss(material, profile) &&
                   !UsesSpecularWorkflow(material, profile) &&
                   !material.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A") &&
                   material.GetTexture("_MetallicGlossMap") is Texture2D;
        }

        private void RegisterPackedMetallicGlossMaps(
            Material material,
            ShaderProfile profile,
            JsonObject json,
            ConversionDiagnostics diagnostics)
        {
            var source = material.GetTexture("_MetallicGlossMap") as Texture2D;
            if (source == null) return;
            var assetPath = AssetDatabase.GetAssetPath(source);
            Texture2D image = null;
            try
            {
                image = LoadTextureForOfflineConversion(source, assetPath, diagnostics, "material.metallicGlossMap");
                var width = image.width;
                var height = image.height;
                var scale = Mathf.Min(1f, maxTextureSize / (float)Mathf.Max(width, height));
                var outputWidth = Mathf.Max(1, Mathf.RoundToInt(width * scale));
                var outputHeight = Mathf.Max(1, Mathf.RoundToInt(height * scale));
                var pixels = ResizePixelsBilinear(
                    image.GetPixels32(),
                    width,
                    height,
                    outputWidth,
                    outputHeight);
                var smoothnessScale = profile.ResolvePackedSmoothness(material);
                var metalnessPixels = new Color32[pixels.Length];
                var roughnessPixels = new Color32[pixels.Length];
                for (var index = 0; index < pixels.Length; index++)
                {
                    var sourcePixel = pixels[index];
                    metalnessPixels[index] = new Color32(0, 0, sourcePixel.r, byte.MaxValue);
                    var roughness = 1 - sourcePixel.a / 255f * smoothnessScale;
                    roughnessPixels[index] = new Color32(
                        0,
                        (byte)Mathf.RoundToInt(Mathf.Clamp01(roughness) * 255),
                        0,
                        byte.MaxValue);
                }

                var metalnessId = RegisterDerivedDataTexture(
                    source,
                    assetPath,
                    "metalness-blue-from-red",
                    outputWidth,
                    outputHeight,
                    metalnessPixels,
                    diagnostics);
                var roughnessId = RegisterDerivedDataTexture(
                    source,
                    assetPath,
                    "roughness-green-from-one-minus-alpha",
                    outputWidth,
                    outputHeight,
                    roughnessPixels,
                    diagnostics);
                json.Add("metalnessMap", Json.String(metalnessId))
                    .Add("roughnessMap", Json.String(roughnessId));
                diagnostics.mapped.Add("material.metallicGlossMap.channelRepack");
                diagnostics.warnings.Add("Unity metallic-gloss packing (R=metallic, A=smoothness times scalar) is deterministically repacked for Three (B=metalness, G=roughness=1-smoothness). The material scalars are set to one so the derived channels are not multiplied twice.");
            }
            catch (Exception exception)
            {
                json.Set("metalness", Json.Number(ResolveMetalness(material, false)))
                    .Set("roughness", Json.Number(1 - ResolveSmoothness(material)));
                diagnostics.unsupported.Add("material.metallicGlossMap.channelRepack");
                diagnostics.approximated.Add("material.metallicGlossMap.scalarFallback");
                diagnostics.warnings.Add("The active metallic-gloss map could not be repacked offline. Best-effort keeps scalar metalness/roughness; strict export fails: " + exception.Message);
            }
            finally
            {
                if (image != null) UnityEngine.Object.DestroyImmediate(image);
            }
        }

        private string RegisterDerivedDataTexture(
            Texture2D source,
            string assetPath,
            string semanticSlot,
            int width,
            int height,
            Color32[] pixels,
            ConversionDiagnostics diagnostics)
        {
            var sourceKey = assetPath + "#" + semanticSlot;
            var textureId = UnityParticleQuarksStableId.Create(sourcePath, sourceKey, "texture");
            if (textures.ContainsKey(textureId)) return textureId;
            var imageId = UnityParticleQuarksStableId.Create(sourcePath, sourceKey, "image");
            var shortHash = UnityParticleQuarksStableId.Hash(sourceKey).Substring(0, 12);
            var fileName = "textures/" + SanitizeFileName(Path.GetFileNameWithoutExtension(assetPath)) +
                           "-" + semanticSlot + "-" + shortHash + ".png";
            var absolutePath = Path.Combine(outputDirectory, fileName.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));
            var output = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            try
            {
                output.SetPixels32(pixels);
                output.Apply(false, false);
                File.WriteAllBytes(absolutePath, output.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(output);
            }

            images[imageId] = Json.Object().Add("uuid", Json.String(imageId)).Add("url", Json.String(fileName));
            var wrapU = ThreeWrapMode(source.wrapModeU, "material.metallicGlossMap.wrapU", diagnostics);
            var wrapV = ThreeWrapMode(source.wrapModeV, "material.metallicGlossMap.wrapV", diagnostics);
            var magFilter = source.filterMode == FilterMode.Point ? 1003 : 1006;
            var minFilter = source.mipmapCount <= 1
                ? magFilter
                : source.filterMode == FilterMode.Point ? 1004
                : source.filterMode == FilterMode.Trilinear ? 1008
                : 1007;
            textures[textureId] = Json.Object()
                .Add("uuid", Json.String(textureId))
                .Add("name", Json.String(source.name + " " + semanticSlot))
                .Add("image", Json.String(imageId))
                .Add("mapping", Json.Number(300))
                .Add("channel", Json.Number(0))
                .Add("repeat", Json.Array().Add(Json.Number(1)).Add(Json.Number(1)))
                .Add("offset", Json.Array().Add(Json.Number(0)).Add(Json.Number(0)))
                .Add("center", Json.Array().Add(Json.Number(0)).Add(Json.Number(0)))
                .Add("rotation", Json.Number(0))
                .Add("wrap", Json.Array().Add(Json.Number(wrapU)).Add(Json.Number(wrapV)))
                .Add("magFilter", Json.Number(magFilter))
                .Add("minFilter", Json.Number(minFilter))
                .Add("anisotropy", Json.Number(Mathf.Max(1, source.anisoLevel)))
                .Add("format", Json.Number(1023))
                .Add("type", Json.Number(1009))
                .Add("colorSpace", Json.String(string.Empty));
            textureFiles.Add(fileName);
            return textureId;
        }

        private static void RegisterOptionalMaterialMap(
            Material material,
            string property,
            string target,
            ConversionDiagnostics diagnostics)
        {
            var texture = material.GetTexture(property);
            if (texture == null || texture is Texture2D) return;
            diagnostics.unsupported.Add("material." + target + ".dimension");
            diagnostics.approximated.Add("material." + target + ".omittedFallback");
            diagnostics.warnings.Add(property + " uses " + texture.dimension + ". Best-effort omits the map; strict export fails because Three particle materials require Texture2D maps.");
        }

        private static string TextureColorSpace(Texture2D texture, string assetPath)
        {
            if (QualitySettings.activeColorSpace == ColorSpace.Gamma) return string.Empty;
            if (string.Equals(assetPath, "Resources/unity_builtin_extra", StringComparison.Ordinal)) return string.Empty;
            if (texture != null && IsUnityDefaultParticleTexture(texture, assetPath)) return string.Empty;
            var importer = string.IsNullOrEmpty(assetPath)
                ? null
                : AssetImporter.GetAtPath(assetPath) as TextureImporter;
            // Authored maps are sRGB unless their importer explicitly marks
            // them as data. Built-in/generated particle textures stay raw.
            return importer == null || importer.sRGBTexture ? "srgb" : string.Empty;
        }

        private string RegisterUnityDefaultParticleFallback(ConversionDiagnostics diagnostics)
        {
            const string sourceKey = "Resources/unity_builtin_extra:Default-Particle";
            diagnostics.unsupported.Add("material.mainTextureExport");
            diagnostics.approximated.Add("material.unityDefaultParticle.radialFallback");
            diagnostics.warnings.Add("Unity's built-in Default-Particle material has an implicit non-readable texture. Best-effort uses a deterministic 64x64 radial-alpha PNG while strict mode remains unsupported.");
            var textureId = UnityParticleQuarksStableId.Create(sourcePath, sourceKey, "texture");
            if (textures.ContainsKey(textureId)) return textureId;
            var imageId = UnityParticleQuarksStableId.Create(sourcePath, sourceKey, "image");
            var shortHash = UnityParticleQuarksStableId.Hash(sourceKey).Substring(0, 12);
            var fileName = "textures/unity-default-particle-" + shortHash + ".png";
            var absolutePath = Path.Combine(outputDirectory, fileName.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));
            File.WriteAllBytes(absolutePath, EncodeUnityDefaultParticleFallback());

            images[imageId] = Json.Object().Add("uuid", Json.String(imageId)).Add("url", Json.String(fileName));
            textures[textureId] = Json.Object()
                .Add("uuid", Json.String(textureId))
                .Add("name", Json.String("Default-Particle"))
                .Add("image", Json.String(imageId))
                .Add("mapping", Json.Number(300))
                .Add("channel", Json.Number(0))
                .Add("repeat", Json.Array().Add(Json.Number(1)).Add(Json.Number(1)))
                .Add("offset", Json.Array().Add(Json.Number(0)).Add(Json.Number(0)))
                .Add("center", Json.Array().Add(Json.Number(0)).Add(Json.Number(0)))
                .Add("rotation", Json.Number(0))
                .Add("wrap", Json.Array().Add(Json.Number(1001)).Add(Json.Number(1001)))
                .Add("format", Json.Number(1023))
                .Add("type", Json.Number(1009))
                .Add("colorSpace", Json.String(TextureColorSpace(null, "Resources/unity_builtin_extra")));
            textureFiles.Add(fileName);
            return textureId;
        }

        private static Texture2D FindUnityDefaultParticleTexture()
        {
            return AssetDatabase.LoadAllAssetsAtPath("Resources/unity_builtin_extra")
                .OfType<Texture2D>()
                .FirstOrDefault(texture =>
                    (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(texture, out _, out long localId) &&
                     localId == 10300) ||
                    string.Equals(texture.name, "Default-Particle", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsUnityDefaultParticleMaterial(Material material)
        {
            if (material == null ||
                !string.Equals(AssetDatabase.GetAssetPath(material), "Resources/unity_builtin_extra", StringComparison.Ordinal))
            {
                return false;
            }
            var defaultParticle = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Particle.mat");
            return (defaultParticle != null && material == defaultParticle) ||
                   string.Equals(material.name, "Default-Particle", StringComparison.OrdinalIgnoreCase);
        }

        private string RegisterTexture(Texture2D texture, ConversionDiagnostics diagnostics)
        {
            var assetPath = AssetDatabase.GetAssetPath(texture);
            var textureId = UnityParticleQuarksStableId.Create(sourcePath, assetPath, "texture");
            if (textures.ContainsKey(textureId)) return textureId;
            var imageId = UnityParticleQuarksStableId.Create(sourcePath, assetPath, "image");
            var shortHash = UnityParticleQuarksStableId.Hash(assetPath).Substring(0, 12);
            var fileName = "textures/" + SanitizeFileName(Path.GetFileNameWithoutExtension(assetPath)) + "-" + shortHash + ".png";
            var absolutePath = Path.Combine(outputDirectory, fileName.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));
            Texture2D sourceImage = null;
            Texture2D outputImage = null;
            var usedBuiltinFallback = false;
            try
            {
                string sourceFile = null;
                byte[] sourceBytes = null;
                var importer = string.IsNullOrEmpty(assetPath)
                    ? null
                    : AssetImporter.GetAtPath(assetPath) as TextureImporter;
                if (importer != null && importer.alphaSource == TextureImporterAlphaSource.FromGrayScale)
                {
                    sourceImage = CopyImportedTextureWithTemporaryReadability(texture, assetPath);
                    diagnostics.mapped.Add("material.mainTextureImporterAlphaBake");
                }
                else
                {
                    try
                    {
                        sourceFile = ResolveAssetSourceFile(assetPath);
                        sourceBytes = File.ReadAllBytes(sourceFile);
                        sourceImage = DecodeImage(sourceBytes, assetPath);
                    }
                    catch (Exception sourceException)
                    {
                        sourceImage = CopyImportedTextureWithTemporaryReadability(texture, assetPath);
                        diagnostics.approximated.Add("material.mainTextureImporterCpuReadback");
                        diagnostics.warnings.Add("Texture source bytes were unavailable or unsupported; Unity importer texels were copied on the CPU and the original readability setting was restored before export continued. No graphics device is used: " + sourceException.Message);
                    }
                }
                var sourcePixels = sourceImage.GetPixels32();
                var sourceProfile = ProfilePixels(sourcePixels);
                var width = sourceImage.width;
                var height = sourceImage.height;
                var scale = Mathf.Min(1f, maxTextureSize / (float)Mathf.Max(width, height));
                width = Mathf.Max(1, Mathf.RoundToInt(width * scale));
                height = Mathf.Max(1, Mathf.RoundToInt(height * scale));
                byte[] outputBytes;
                if (sourceBytes != null && width == sourceImage.width && height == sourceImage.height &&
                    string.Equals(Path.GetExtension(sourceFile), ".png", StringComparison.OrdinalIgnoreCase))
                {
                    outputBytes = sourceBytes;
                }
                else
                {
                    outputImage = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
                    outputImage.SetPixels32(ResizePixelsBilinear(
                        sourcePixels,
                        sourceImage.width,
                        sourceImage.height,
                        width,
                        height));
                    outputImage.Apply(false, false);
                    outputBytes = outputImage.EncodeToPNG();
                }

                var validationImage = DecodeImage(outputBytes, fileName);
                try
                {
                    if (validationImage.width != width || validationImage.height != height)
                    {
                        throw new InvalidDataException("CPU texture export dimensions do not match the requested dimensions.");
                    }
                    ValidatePixelProfile(sourceProfile, ProfilePixels(validationImage.GetPixels32()));
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(validationImage);
                }
                File.WriteAllBytes(absolutePath, outputBytes);
            }
            catch (Exception exception)
            {
                if (File.Exists(absolutePath)) File.Delete(absolutePath);
                if (IsUnityDefaultParticleTexture(texture, assetPath))
                {
                    try
                    {
                        var outputBytes = EncodeTextureGpuReadback(
                            texture,
                            maxTextureSize,
                            out var expandedAlphaOnlyRgb);
                        File.WriteAllBytes(absolutePath, outputBytes);
                        diagnostics.approximated.Add("material.unityDefaultParticle.gpuReadback");
                        diagnostics.warnings.Add("Unity's non-readable built-in Default-Particle texture is exported through deterministic GPU sampling/readback. This preserves the actual texture alpha profile but is reported because compressed/source texels are not available byte-for-byte.");
                        if (expandedAlphaOnlyRgb)
                        {
                            diagnostics.mapped.Add("material.unityDefaultParticle.alphaOnlyRgbExpansion");
                            diagnostics.warnings.Add("The GPU readback exposed an alpha-only texture as alpha-swizzled grayscale. PNG RGB is expanded to white while preserving sampled alpha so browser blending does not apply alpha twice.");
                        }
                    }
                    catch (Exception gpuException)
                    {
                        diagnostics.unsupported.Add("material.mainTextureExport");
                        var outputBytes = EncodeUnityDefaultParticleFallback();
                        File.WriteAllBytes(absolutePath, outputBytes);
                        usedBuiltinFallback = true;
                        diagnostics.approximated.Add("material.unityDefaultParticle.radialFallback");
                        diagnostics.warnings.Add("Unity's non-readable built-in Default-Particle texture GPU readback failed. Best-effort uses the documented deterministic 64x64 radial-alpha fallback and strict mode remains unsupported: " + gpuException.Message);
                    }
                }
                else
                {
                    diagnostics.unsupported.Add("material.mainTextureExport");
                    diagnostics.approximated.Add("material.mainTextureExport.untexturedFallback");
                    diagnostics.warnings.Add("Texture export failed for " + assetPath + ". Best-effort explicitly emits the material without a texture; strict export fails: " + exception.Message);
                    return string.Empty;
                }
            }
            finally
            {
                if (sourceImage != null) UnityEngine.Object.DestroyImmediate(sourceImage);
                if (outputImage != null) UnityEngine.Object.DestroyImmediate(outputImage);
            }

            images[imageId] = Json.Object().Add("uuid", Json.String(imageId)).Add("url", Json.String(fileName));
            var wrapU = ThreeWrapMode(texture.wrapModeU, "material.mainTexture.wrapU", diagnostics);
            var wrapV = ThreeWrapMode(texture.wrapModeV, "material.mainTexture.wrapV", diagnostics);
            var magFilter = texture.filterMode == FilterMode.Point ? 1003 : 1006;
            var minFilter = texture.mipmapCount <= 1
                ? magFilter
                : texture.filterMode == FilterMode.Point ? 1004
                : texture.filterMode == FilterMode.Trilinear ? 1008
                : 1007;
            textures[textureId] = Json.Object()
                .Add("uuid", Json.String(textureId))
                .Add("name", Json.String(texture.name))
                .Add("image", Json.String(imageId))
                .Add("mapping", Json.Number(300))
                .Add("channel", Json.Number(0))
                .Add("repeat", Json.Array().Add(Json.Number(1)).Add(Json.Number(1)))
                .Add("offset", Json.Array().Add(Json.Number(0)).Add(Json.Number(0)))
                .Add("center", Json.Array().Add(Json.Number(0)).Add(Json.Number(0)))
                .Add("rotation", Json.Number(0))
                .Add("wrap", Json.Array().Add(Json.Number(wrapU)).Add(Json.Number(wrapV)))
                .Add("magFilter", Json.Number(magFilter))
                .Add("minFilter", Json.Number(minFilter))
                .Add("anisotropy", Json.Number(Mathf.Max(1, texture.anisoLevel)))
                .Add("format", Json.Number(1023))
                .Add("type", Json.Number(1009))
                .Add("colorSpace", Json.String(TextureColorSpace(texture, assetPath)));
            textureFiles.Add(fileName);
            if (!usedBuiltinFallback) diagnostics.mapped.Add("material.mainTexture");
            diagnostics.mapped.Add("material.mainTexture.sampler");
            return textureId;
        }

        private static byte[] EncodeTextureGpuReadback(
            Texture texture,
            int textureLimit,
            out bool expandedAlphaOnlyRgb)
        {
            expandedAlphaOnlyRgb = false;
            if (texture == null) throw new ArgumentNullException(nameof(texture));
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new InvalidOperationException("Unity is running without a graphics device.");
            var scale = Mathf.Min(1f, textureLimit / (float)Mathf.Max(texture.width, texture.height));
            var width = Mathf.Max(1, Mathf.RoundToInt(texture.width * scale));
            var height = Mathf.Max(1, Mathf.RoundToInt(texture.height * scale));
            var previous = RenderTexture.active;
            var target = RenderTexture.GetTemporary(
                width,
                height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Default);
            Texture2D image = null;
            try
            {
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                image = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                var pixels = image.GetPixels32();
                var alphaSwizzled = pixels.Any(pixel => pixel.a > 0 && pixel.a < 255) &&
                                    pixels.All(pixel =>
                                        Mathf.Abs(pixel.r - pixel.a) <= 4 &&
                                        Mathf.Abs(pixel.g - pixel.a) <= 4 &&
                                        Mathf.Abs(pixel.b - pixel.a) <= 4);
                if ((texture is Texture2D texture2D && texture2D.format == TextureFormat.Alpha8) || alphaSwizzled)
                {
                    expandedAlphaOnlyRgb = true;
                    for (var index = 0; index < pixels.Length; index++)
                    {
                        pixels[index].r = 255;
                        pixels[index].g = 255;
                        pixels[index].b = 255;
                    }
                }
                image.SetPixels32(pixels);
                image.Apply(false, false);
                return image.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                if (image != null) UnityEngine.Object.DestroyImmediate(image);
            }
        }

        private static int ThreeWrapMode(
            TextureWrapMode mode,
            string field,
            ConversionDiagnostics diagnostics)
        {
            switch (mode)
            {
                case TextureWrapMode.Repeat: return 1000;
                case TextureWrapMode.Clamp: return 1001;
                case TextureWrapMode.Mirror: return 1002;
                case TextureWrapMode.MirrorOnce:
                    diagnostics.unsupported.Add(field + ".mirrorOnce");
                    diagnostics.approximated.Add(field + ".mirrorOnce.mirroredRepeatFallback");
                    diagnostics.warnings.Add(field + " uses Unity Mirror Once, which Three does not expose. Best-effort explicitly uses MirroredRepeatWrapping; strict export fails.");
                    return 1002;
                default:
                    diagnostics.unsupported.Add(field + ".unknown");
                    diagnostics.approximated.Add(field + ".unknown.clampFallback");
                    diagnostics.warnings.Add(field + " uses an unknown texture wrap mode. Best-effort explicitly clamps; strict export fails.");
                    return 1001;
            }
        }

        private static bool IsUnityDefaultParticleTexture(Texture2D texture, string assetPath)
        {
            if (texture == null || !string.Equals(assetPath, "Resources/unity_builtin_extra", StringComparison.Ordinal))
                return false;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(texture, out _, out long localId) && localId == 10300)
                return true;
            return string.Equals(texture.name, "Default-Particle", StringComparison.OrdinalIgnoreCase);
        }

        internal static byte[] EncodeUnityDefaultParticleFallback()
        {
            const int size = 64;
            var image = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
            try
            {
                var pixels = new Color32[size * size];
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        var dx = ((x + 0.5f) / size) * 2 - 1;
                        var dy = ((y + 0.5f) / size) * 2 - 1;
                        var radius = Mathf.Sqrt(dx * dx + dy * dy);
                        var alpha = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.05f, 1, radius));
                        pixels[y * size + x] = new Color32(255, 255, 255,
                            (byte)Mathf.RoundToInt(Mathf.Clamp01(alpha) * 255));
                    }
                }
                image.SetPixels32(pixels);
                image.Apply(false, false);
                return image.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(image);
            }
        }

        private static string ResolveAssetSourceFile(string assetPath)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
            var projectPath = Path.GetFullPath(Path.Combine(
                projectRoot,
                assetPath.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(projectPath)) return projectPath;

            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
            if (package != null)
            {
                var packagePrefix = "Packages/" + package.name;
                var relative = assetPath.StartsWith(packagePrefix, StringComparison.Ordinal)
                    ? assetPath.Substring(packagePrefix.Length).TrimStart('/')
                    : string.Empty;
                var resolvedPath = Path.GetFullPath(Path.Combine(
                    package.resolvedPath,
                    relative.Replace('/', Path.DirectorySeparatorChar)));
                if (File.Exists(resolvedPath)) return resolvedPath;
            }
            throw new FileNotFoundException("Texture source file is not available for CPU export.", assetPath);
        }

        private static Texture2D DecodeImage(byte[] bytes, string label)
        {
            var image = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            if (bytes == null || bytes.Length == 0 || !ImageConversion.LoadImage(image, bytes, false))
            {
                UnityEngine.Object.DestroyImmediate(image);
                throw new InvalidDataException("Texture source " + label + " is not a CPU-decodable PNG or JPEG image.");
            }
            return image;
        }

        private static Texture2D LoadTextureForOfflineConversion(
            Texture2D texture,
            string assetPath,
            ConversionDiagnostics diagnostics,
            string field)
        {
            try
            {
                var sourceFile = ResolveAssetSourceFile(assetPath);
                return DecodeImage(File.ReadAllBytes(sourceFile), assetPath);
            }
            catch (Exception sourceException)
            {
                var image = CopyImportedTextureWithTemporaryReadability(texture, assetPath);
                diagnostics.mapped.Add(field + ".unityImporterCpuDecode");
                diagnostics.warnings.Add(
                    field + " source bytes are not PNG/JPEG-decodable, so the exporter temporarily enabled Unity importer CPU readability, copied the imported texels, and restored the original importer setting. This remains fully offline and does not use a graphics device: " +
                    sourceException.Message);
                return image;
            }
        }

        private static Texture2D CopyImportedTextureWithTemporaryReadability(Texture2D texture, string assetPath)
        {
            if (texture == null) throw new ArgumentNullException(nameof(texture));
            if (texture.isReadable) return CopyReadableTexture(texture, assetPath);

            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
                throw new InvalidDataException("Texture has no mutable TextureImporter for offline CPU decoding: " + assetPath);
            var wasReadable = importer.isReadable;
            try
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
                var readable = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                if (readable == null || !readable.isReadable)
                    throw new InvalidDataException("TextureImporter did not produce a CPU-readable texture: " + assetPath);
                return CopyReadableTexture(readable, assetPath);
            }
            finally
            {
                if (!wasReadable)
                {
                    importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                    if (importer != null)
                    {
                        importer.isReadable = false;
                        importer.SaveAndReimport();
                    }
                }
            }
        }

        private static Texture2D CopyReadableTexture(Texture2D texture, string label)
        {
            Texture2D image = null;
            try
            {
                image = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, false);
                image.SetPixels32(texture.GetPixels32());
                image.Apply(false, false);
                return image;
            }
            catch (Exception exception)
            {
                if (image != null) UnityEngine.Object.DestroyImmediate(image);
                throw new InvalidDataException("Imported texture " + label + " could not be read on the CPU.", exception);
            }
        }

        private static Color32[] ResizePixelsBilinear(
            Color32[] source,
            int sourceWidth,
            int sourceHeight,
            int targetWidth,
            int targetHeight)
        {
            var result = new Color32[targetWidth * targetHeight];
            for (var y = 0; y < targetHeight; y++)
            {
                var sourceY = (y + 0.5f) * sourceHeight / targetHeight - 0.5f;
                var y0 = Mathf.Clamp(Mathf.FloorToInt(sourceY), 0, sourceHeight - 1);
                var y1 = Mathf.Min(y0 + 1, sourceHeight - 1);
                var ty = Mathf.Clamp01(sourceY - Mathf.Floor(sourceY));
                for (var x = 0; x < targetWidth; x++)
                {
                    var sourceX = (x + 0.5f) * sourceWidth / targetWidth - 0.5f;
                    var x0 = Mathf.Clamp(Mathf.FloorToInt(sourceX), 0, sourceWidth - 1);
                    var x1 = Mathf.Min(x0 + 1, sourceWidth - 1);
                    var tx = Mathf.Clamp01(sourceX - Mathf.Floor(sourceX));
                    var bottomLeft = source[y0 * sourceWidth + x0];
                    var bottomRight = source[y0 * sourceWidth + x1];
                    var topLeft = source[y1 * sourceWidth + x0];
                    var topRight = source[y1 * sourceWidth + x1];
                    result[y * targetWidth + x] = new Color32(
                        BilinearByte(bottomLeft.r, bottomRight.r, topLeft.r, topRight.r, tx, ty),
                        BilinearByte(bottomLeft.g, bottomRight.g, topLeft.g, topRight.g, tx, ty),
                        BilinearByte(bottomLeft.b, bottomRight.b, topLeft.b, topRight.b, tx, ty),
                        BilinearByte(bottomLeft.a, bottomRight.a, topLeft.a, topRight.a, tx, ty));
                }
            }
            return result;
        }

        private static byte BilinearByte(byte bottomLeft, byte bottomRight, byte topLeft, byte topRight, float tx, float ty)
        {
            var bottom = Mathf.Lerp(bottomLeft, bottomRight, tx);
            var top = Mathf.Lerp(topLeft, topRight, tx);
            return (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(bottom, top, ty)), 0, 255);
        }

        private static PixelProfile ProfilePixels(Color32[] pixels)
        {
            if (pixels == null || pixels.Length == 0) throw new InvalidDataException("Decoded texture has no pixels.");
            var profile = new PixelProfile
            {
                minRed = byte.MaxValue,
                minGreen = byte.MaxValue,
                minBlue = byte.MaxValue,
                minAlpha = byte.MaxValue
            };
            foreach (var pixel in pixels)
            {
                profile.minRed = Math.Min(profile.minRed, pixel.r);
                profile.maxRed = Math.Max(profile.maxRed, pixel.r);
                profile.minGreen = Math.Min(profile.minGreen, pixel.g);
                profile.maxGreen = Math.Max(profile.maxGreen, pixel.g);
                profile.minBlue = Math.Min(profile.minBlue, pixel.b);
                profile.maxBlue = Math.Max(profile.maxBlue, pixel.b);
                profile.minAlpha = Math.Min(profile.minAlpha, pixel.a);
                profile.maxAlpha = Math.Max(profile.maxAlpha, pixel.a);
            }
            return profile;
        }

        private static void ValidatePixelProfile(PixelProfile source, PixelProfile output)
        {
            if (source.HasColorVariation && !output.HasColorVariation)
            {
                throw new InvalidDataException("CPU texture export collapsed non-uniform source color pixels to a uniform image.");
            }
            if (source.HasAlphaVariation && !output.HasAlphaVariation)
            {
                throw new InvalidDataException("CPU texture export collapsed non-uniform source alpha pixels to a uniform image.");
            }
            if (source.minAlpha < byte.MaxValue && output.minAlpha == byte.MaxValue)
            {
                throw new InvalidDataException("CPU texture export lost source transparency.");
            }
        }


        private static bool IsAdditive(Material material)
        {
            if (material == null) return false;
            var shaderName = material.shader == null ? string.Empty : material.shader.name;
            return material.IsKeywordEnabled("_ADDITIVE_ON") ||
                   material.shaderKeywords.Any(keyword => keyword.IndexOf("ADDITIVE", StringComparison.OrdinalIgnoreCase) >= 0) ||
                   shaderName.IndexOf("Add_CenterGlow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   shaderName.IndexOf("Add_Blend", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (FirstMaterialProperty(material, "_DstBlend", "_AlphaDstBlend") is string destination &&
                    material.GetInt(destination) == (int)BlendMode.One);
        }

        private static bool IsPremultipliedAlpha(Material material)
        {
            if (material == null || IsAdditive(material)) return false;
            var source = FirstMaterialProperty(material, "_SrcBlend", "_AlphaSrcBlend");
            var destination = FirstMaterialProperty(material, "_DstBlend", "_AlphaDstBlend");
            return source != null && destination != null &&
                   material.GetInt(source) == (int)BlendMode.One &&
                   material.GetInt(destination) == (int)BlendMode.OneMinusSrcAlpha;
        }

        private static JsonObject ColorJson(Color color)
        {
            return Json.Object()
                .Add("r", Json.Number(color.r))
                .Add("g", Json.Number(color.g))
                .Add("b", Json.Number(color.b))
                .Add("a", Json.Number(color.a));
        }

        private static string SanitizeFileName(string value)
        {
            var characters = (value ?? "texture").ToLowerInvariant().Select(character =>
                char.IsLetterOrDigit(character) || character == '-' ? character : '-').ToArray();
            return new string(characters).Trim('-');
        }
    }
}
