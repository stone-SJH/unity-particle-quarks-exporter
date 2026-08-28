using System;

namespace UnityParticleQuarksExporter.Editor
{
    [Serializable]
    public sealed class UnityParticleQuarksPipelineConfig
    {
        public string schemaVersion;
        public string outputRoot;
        public string mode = "strict";
        // stock publishes the same loadable JSON without requiring the adapter.
        public string runtimeProfile = "extended";
        // The default target preserves the normal strict/best-effort contract.
        public string target = "default";
        // Unknown custom shaders must either receive a profile or remain a
        // review-only artifact. This is intentionally independent of mode.
        public string unknownCustomShaderPolicy = "require-profile";
        // Empty uses the active project's render pipeline.
        public string sourceRenderPipeline = "";
        public int maxTextureSize = 1024;
        public UnityParticleQuarksEffectRequest[] effects;
    }

    [Serializable]
    public sealed class UnityParticleQuarksEffectRequest
    {
        public string id;
        public string prefabPath;
        public string[] includeParticleSystemPaths;
        public string[] excludeParticleSystemPaths;
        public string mode;
        public int maxTextureSize;
    }

    [Serializable]
    public sealed class UnityParticleQuarksPipelineManifest
    {
        public string schemaVersion = "unity_particle_quarks_pipeline.manifest.v1";
        public string exporterVersion = "0.3.2";
        public string unityVersion;
        public string target;
        public string sourceRenderPipeline;
        public string runtimeProfile = "extended";
        public string unknownCustomShaderPolicy = "require-profile";
        public bool publicationBlocked;
        public UnityParticleQuarksExtensionDescriptor[] extensionsUsed;
        public UnityParticleQuarksExtensionDescriptor[] extensionsRequired;
        public UnityParticleQuarksEffectManifest[] effects;
    }

    [Serializable]
    public sealed class UnityParticleQuarksEffectManifest
    {
        public string id;
        public string sourcePrefabPath;
        public string status;
        public string effectJson;
        public string conversionReport;
        public string[] textures;
        public int particleSystemCount;
        public string sourceFingerprint;
        public string target;
        public string runtimeProfile = "extended";
        public string unknownCustomShaderPolicy = "require-profile";
        public bool publicationBlocked;
        public UnityParticleQuarksShaderProfileGap[] shaderProfileGaps;
        public string runtimeTier = "stock";
        public UnityParticleQuarksExtensionDescriptor[] extensionsUsed;
        public UnityParticleQuarksExtensionDescriptor[] extensionsRequired;
        public string[] errors;
    }

    [Serializable]
    public sealed class UnityParticleQuarksConversionReport
    {
        public string schemaVersion = "unity_particle_quarks_conversion.report.v1";
        public string exporterVersion = "0.3.2";
        public string unityVersion;
        public string effectId;
        public string sourcePrefabPath;
        public string sourceFingerprint;
        public string mode;
        public string target;
        public string sourceRenderPipeline;
        public string status;
        public string runtimeProfile = "extended";
        public string unknownCustomShaderPolicy = "require-profile";
        public bool publicationBlocked;
        public string runtimeTier = "stock";
        public UnityParticleQuarksExtensionDescriptor[] extensionsUsed;
        public UnityParticleQuarksExtensionDescriptor[] extensionsRequired;
        public UnityParticleQuarksParticleSystemReport[] particleSystems;
        public UnityParticleQuarksShaderProfileGap[] shaderProfileGaps;
        public string[] errors;
    }

    [Serializable]
    public sealed class UnityParticleQuarksParticleSystemReport
    {
        public string path;
        public string status;
        public string runtimeProfile;
        public string runtimeTier;
        public string[] mapped;
        public string[] approximated;
        public string[] unsupported;
        public string[] fatalUnsupported;
        public string[] nonBlockingUnsupported;
        public string[] inactive;
        public UnityParticleQuarksShaderResolutionFailure[] shaderResolutionFailures;
        public UnityParticleQuarksShaderProfileGap[] shaderProfileGaps;
        public UnityParticleQuarksMaterialProfileReport[] materialProfiles;
        public string[] warnings;
    }

    [Serializable]
    public sealed class UnityParticleQuarksExtensionDescriptor
    {
        public string id;
        public string version;
    }

    [Serializable]
    public sealed class UnityParticleQuarksMaterialProfileReport
    {
        public string materialSlot;
        public string materialName;
        public string materialAssetPath;
        public string sourceShader;
        public string profileId;
        public string profileVersion;
        public string runtimeTier;
        public string fidelity;
        public string blendMode;
        public bool alphaClip;
        public bool softParticles;
        public bool doubleSided;
        public bool consumesParticleColor;
        public bool meshPbr;
        public string[] resolvedProperties;
        public string[] missingProperties;
        public string[] unmappedProperties;
        public string[] conflicts;
    }

    [Serializable]
    public sealed class UnityParticleQuarksShaderResolutionFailure
    {
        public string materialName;
        public string materialAssetPath;
        public string materialSlot;
        public string resolvedShaderName;
        public string failureKind;
    }

    [Serializable]
    public sealed class UnityParticleQuarksShaderProfileGap
    {
        public string schemaVersion = "unity_particle_quarks_shader_profile_gap.v1";
        public string shaderName;
        public string shaderFingerprint;
        public string sourcePipeline;
        public string materialName;
        public string materialAssetPath;
        public string materialSlot;
        public string[] properties;
        public string[] keywords;
        public string requiredAction = "add-profile";
    }
}
