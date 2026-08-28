using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityParticleQuarksExporter.Editor
{
    public static class UnityParticleQuarksExportBatchmode
    {
        internal const string ConfigSchema = "unity_particle_quarks_pipeline.config.v1";
        private const string ManifestSchema = "unity_particle_quarks_pipeline.manifest.v1";
        private const string ReportSchema = "unity_particle_quarks_conversion.report.v1";
        private const string UnityPairedSemanticsExtensionId = "unity_particle_paired_semantics";
        private const string UnityPairedSemanticsExtensionVersion = "1";
        private static readonly Regex EffectIdPattern = new Regex("^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant);
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static void RunBatch()
        {
            RunBatchWithArgument("-unityParticleQuarksConfig", "Unity Particle");
        }

        internal static void RunBatchWithArgument(string argumentName, string displayName)
        {
            try
            {
                var configPath = GetArgument(argumentName);
                if (string.IsNullOrWhiteSpace(configPath))
                    throw new InvalidOperationException(argumentName + " <config.json> is required.");
                ExportConfig(configPath, true);
                Debug.Log(displayName + " export completed successfully.");
                if (Application.isBatchMode) EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                else throw;
            }
        }

        public static UnityParticleQuarksPipelineManifest ExportConfig(string configPath, bool throwOnStrictFailure)
        {
            var absoluteConfigPath = Path.GetFullPath(configPath);
            if (!File.Exists(absoluteConfigPath)) throw new FileNotFoundException("Unity VFX config was not found.", absoluteConfigPath);
            var rawConfig = File.ReadAllText(absoluteConfigPath, Encoding.UTF8);
            RejectSelectionMetadata(rawConfig);
            var config = JsonUtility.FromJson<UnityParticleQuarksPipelineConfig>(rawConfig);
            ValidateConfig(config);
            var runtimeProfile = NormalizeRuntimeProfile(config.runtimeProfile);
            var unknownCustomShaderPolicy = NormalizeUnknownCustomShaderPolicy(config.unknownCustomShaderPolicy);

            var outputRoot = ResolveOutputRoot(config.outputRoot, Path.GetDirectoryName(absoluteConfigPath));
            var stagingRoot = outputRoot + ".staging";
            var backupRoot = outputRoot + ".backup";
            DeleteDirectory(stagingRoot);
            Directory.CreateDirectory(stagingRoot);

            var manifest = new UnityParticleQuarksPipelineManifest
            {
                schemaVersion = ManifestSchema,
                unityVersion = Application.unityVersion,
                target = NormalizeTarget(config.target),
                sourceRenderPipeline = ResolveSourceRenderPipeline(config.sourceRenderPipeline),
                runtimeProfile = runtimeProfile,
                unknownCustomShaderPolicy = unknownCustomShaderPolicy,
                extensionsUsed = Array.Empty<UnityParticleQuarksExtensionDescriptor>(),
                extensionsRequired = Array.Empty<UnityParticleQuarksExtensionDescriptor>(),
                effects = Array.Empty<UnityParticleQuarksEffectManifest>()
            };
            var target = manifest.target;
            var sourceRenderPipeline = manifest.sourceRenderPipeline;
            var effectResults = new List<UnityParticleQuarksEffectManifest>();
            var strictFailure = false;
            try
            {
                foreach (var effect in config.effects.OrderBy(item => item.id, StringComparer.Ordinal))
                {
                    var mode = NormalizeMode(string.IsNullOrWhiteSpace(effect.mode) ? config.mode : effect.mode);
                    var textureSize = effect.maxTextureSize > 0 ? effect.maxTextureSize : config.maxTextureSize;
                    var result = ExportEffect(
                        effect,
                        mode,
                        target,
                        sourceRenderPipeline,
                        runtimeProfile,
                        unknownCustomShaderPolicy,
                        textureSize,
                        stagingRoot);
                    effectResults.Add(result);
                    strictFailure |= mode == "strict" &&
                                     (result.status == "failed" || result.status == "profile_required");
                }

                manifest.effects = effectResults.ToArray();
                manifest.publicationBlocked = effectResults.Any(item => item.publicationBlocked);
                manifest.extensionsUsed = effectResults.Any(item => item.extensionsUsed != null && item.extensionsUsed.Length > 0)
                    ? UnityPairedSemanticsExtensions()
                    : Array.Empty<UnityParticleQuarksExtensionDescriptor>();
                manifest.extensionsRequired = effectResults.Any(item => item.extensionsRequired != null && item.extensionsRequired.Length > 0)
                    ? UnityPairedSemanticsExtensions()
                    : Array.Empty<UnityParticleQuarksExtensionDescriptor>();
                WriteJson(Path.Combine(stagingRoot, "manifest.json"), manifest);
                ValidateStaging(stagingRoot, manifest);
                Publish(stagingRoot, outputRoot, backupRoot);
            }
            catch
            {
                DeleteDirectory(stagingRoot);
                throw;
            }

            if (strictFailure && throwOnStrictFailure)
            {
                throw new InvalidOperationException("One or more strict VFX effects failed conversion. Inspect conversion-report.json.");
            }
            return manifest;
        }

        private static UnityParticleQuarksEffectManifest ExportEffect(
            UnityParticleQuarksEffectRequest request,
            string mode,
            string target,
            string sourceRenderPipeline,
            string runtimeProfile,
            string unknownCustomShaderPolicy,
            int maxTextureSize,
            string stagingRoot)
        {
            var effectDirectory = Path.Combine(stagingRoot, request.id);
            Directory.CreateDirectory(effectDirectory);
            var sourceFingerprint = SourceFingerprint(request.prefabPath);
            var presentationTarget = target == "presentation";
            var report = new UnityParticleQuarksConversionReport
            {
                schemaVersion = ReportSchema,
                unityVersion = Application.unityVersion,
                effectId = request.id,
                sourcePrefabPath = NormalizeAssetPath(request.prefabPath),
                sourceFingerprint = sourceFingerprint,
                mode = mode,
                target = target,
                sourceRenderPipeline = sourceRenderPipeline,
                status = "failed",
                runtimeProfile = runtimeProfile,
                unknownCustomShaderPolicy = unknownCustomShaderPolicy,
                extensionsUsed = Array.Empty<UnityParticleQuarksExtensionDescriptor>(),
                extensionsRequired = Array.Empty<UnityParticleQuarksExtensionDescriptor>(),
                particleSystems = Array.Empty<UnityParticleQuarksParticleSystemReport>(),
                errors = Array.Empty<string>()
            };
            var manifest = new UnityParticleQuarksEffectManifest
            {
                id = request.id,
                sourcePrefabPath = NormalizeAssetPath(request.prefabPath),
                status = "failed",
                effectJson = string.Empty,
                conversionReport = request.id + "/conversion-report.json",
                textures = Array.Empty<string>(),
                particleSystemCount = 0,
                sourceFingerprint = sourceFingerprint,
                target = target,
                runtimeProfile = runtimeProfile,
                unknownCustomShaderPolicy = unknownCustomShaderPolicy,
                extensionsUsed = Array.Empty<UnityParticleQuarksExtensionDescriptor>(),
                extensionsRequired = Array.Empty<UnityParticleQuarksExtensionDescriptor>(),
                errors = Array.Empty<string>()
            };

            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(request.prefabPath);
                var systems = SelectParticleSystems(root, request);
                manifest.particleSystemCount = systems.Count;
                var exporter = new QuarksJsonExporter(
                    root,
                    request.prefabPath,
                    effectDirectory,
                    maxTextureSize,
                    presentationTarget,
                    sourceRenderPipeline == "default");
                var conversion = exporter.Export(systems);
                report.particleSystems = conversion.reports;
                report.shaderProfileGaps = conversion.reports
                    .Where(item => item.shaderProfileGaps != null)
                    .SelectMany(item => item.shaderProfileGaps)
                    .GroupBy(item => item.shaderFingerprint + "\n" + item.materialAssetPath + "\n" + item.materialSlot, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .OrderBy(item => item.shaderFingerprint, StringComparer.Ordinal)
                    .ThenBy(item => item.materialAssetPath, StringComparer.Ordinal)
                    .ThenBy(item => item.materialSlot, StringComparer.Ordinal)
                    .ToArray();
                report.publicationBlocked = report.shaderProfileGaps.Length > 0;
                var usesUnityPairedSemanticsExtension = conversion.runtimeTier == "paired";
                var requiresUnityPairedSemanticsExtension = runtimeProfile == "extended" && usesUnityPairedSemanticsExtension;
                var resolvedRuntimeTier = requiresUnityPairedSemanticsExtension ? "paired" : "stock";
                foreach (var particleSystem in report.particleSystems)
                {
                    particleSystem.runtimeProfile = runtimeProfile;
                    if (runtimeProfile == "stock") particleSystem.runtimeTier = "stock";
                }
                report.runtimeTier = resolvedRuntimeTier;
                manifest.runtimeTier = resolvedRuntimeTier;
                report.extensionsUsed = manifest.extensionsUsed = usesUnityPairedSemanticsExtension
                    ? UnityPairedSemanticsExtensions()
                    : Array.Empty<UnityParticleQuarksExtensionDescriptor>();
                report.extensionsRequired = manifest.extensionsRequired = requiresUnityPairedSemanticsExtension
                    ? UnityPairedSemanticsExtensions()
                    : Array.Empty<UnityParticleQuarksExtensionDescriptor>();
                manifest.textures = conversion.textures.Select(path => request.id + "/" + path).ToArray();
                manifest.shaderProfileGaps = report.shaderProfileGaps;
                manifest.publicationBlocked = report.publicationBlocked;

                if (conversion.hasFatalUnsupported)
                {
                    report.status = "failed";
                    manifest.status = "failed";
                    manifest.errors = report.errors = new[]
                    {
                        "Automatic Unity VFX conversion abandoned because the effect contains playback-blocking unsupported features that cannot publish fallback JSON: " +
                        string.Join(", ", conversion.fatalUnsupported ?? Array.Empty<string>())
                    };
                    DeleteDirectory(Path.Combine(effectDirectory, "textures"));
                    manifest.textures = Array.Empty<string>();
                }
                else if (conversion.emitterCount == 0)
                {
                    report.status = "failed";
                    manifest.status = "failed";
                    manifest.errors = report.errors = new[] { "No exportable ParticleSystem remains after applying explicit renderer/material diagnostics." };
                    DeleteDirectory(Path.Combine(effectDirectory, "textures"));
                    manifest.textures = Array.Empty<string>();
                }
                else if (report.shaderProfileGaps.Length > 0 && unknownCustomShaderPolicy == "require-profile")
                {
                    report.status = "profile_required";
                    manifest.status = "profile_required";
                    manifest.effectJson = request.id + "/effect.quarks.json";
                    manifest.errors = report.errors = new[]
                    {
                        "Conversion produced a review artifact, but publication is blocked until every unknown custom shader receives a validated profile: " +
                        string.Join(", ", report.shaderProfileGaps.Select(item => item.shaderName).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal))
                    };
                    File.WriteAllText(Path.Combine(effectDirectory, "effect.quarks.json"), conversion.json, Utf8NoBom);
                }
                else if (report.shaderProfileGaps.Length > 0 && unknownCustomShaderPolicy == "review-fallback")
                {
                    report.status = "review_only";
                    manifest.status = "review_only";
                    manifest.effectJson = request.id + "/effect.quarks.json";
                    manifest.errors = report.errors = new[]
                    {
                        "Review-only fallback was used for unknown custom shaders. This artifact is not delivery-ready or publishable."
                    };
                    File.WriteAllText(Path.Combine(effectDirectory, "effect.quarks.json"), conversion.json, Utf8NoBom);
                }
                else if (conversion.hasUnsupported && mode == "strict" && !presentationTarget)
                {
                    report.status = "failed";
                    manifest.status = "failed";
                    manifest.errors = report.errors = new[] { "Strict conversion rejected one or more active unsupported modules." };
                    DeleteDirectory(Path.Combine(effectDirectory, "textures"));
                    manifest.textures = Array.Empty<string>();
                }
                else
                {
                    var status = conversion.hasUnsupported ? "partial" : "ready";
                    report.status = status;
                    manifest.status = status;
                    manifest.effectJson = request.id + "/effect.quarks.json";
                    File.WriteAllText(Path.Combine(effectDirectory, "effect.quarks.json"), conversion.json, Utf8NoBom);
                }
            }
            catch (Exception exception)
            {
                manifest.status = report.status = "failed";
                manifest.errors = report.errors = new[] { exception.GetType().Name + ": " + exception.Message };
                var jsonPath = Path.Combine(effectDirectory, "effect.quarks.json");
                if (File.Exists(jsonPath)) File.Delete(jsonPath);
            }
            finally
            {
                if (root != null) PrefabUtility.UnloadPrefabContents(root);
            }

            WriteJson(Path.Combine(effectDirectory, "conversion-report.json"), report);
            return manifest;
        }

        private static List<ParticleSystem> SelectParticleSystems(GameObject root, UnityParticleQuarksEffectRequest request)
        {
            var include = new HashSet<string>((request.includeParticleSystemPaths ?? Array.Empty<string>()).Select(NormalizeHierarchyPath), StringComparer.Ordinal);
            var exclude = new HashSet<string>((request.excludeParticleSystemPaths ?? Array.Empty<string>()).Select(NormalizeHierarchyPath), StringComparer.Ordinal);
            var all = root.GetComponentsInChildren<ParticleSystem>(true)
                .OrderBy(system => QuarksJsonExporter.GetPath(root.transform, system.transform), StringComparer.Ordinal)
                .ToArray();
            var known = new HashSet<string>(all.Select(system => QuarksJsonExporter.GetPath(root.transform, system.transform)), StringComparer.Ordinal);
            var missing = include.Where(path => !known.Contains(path)).ToArray();
            if (missing.Length > 0) throw new InvalidOperationException("Included ParticleSystem paths were not found: " + string.Join(", ", missing));
            var selected = all.Where(system =>
            {
                var path = QuarksJsonExporter.GetPath(root.transform, system.transform);
                return (include.Count == 0 || include.Contains(path)) && !exclude.Contains(path);
            }).ToList();
            if (selected.Count == 0) throw new InvalidOperationException("Effect selects no ParticleSystem components.");
            return selected;
        }

        private static void ValidateConfig(UnityParticleQuarksPipelineConfig config)
        {
            if (config == null) throw new InvalidOperationException("Config JSON could not be parsed.");
            if (!string.Equals(config.schemaVersion, ConfigSchema, StringComparison.Ordinal))
                throw new InvalidOperationException("Config schemaVersion must be " + ConfigSchema + ".");
            if (string.IsNullOrWhiteSpace(config.outputRoot)) throw new InvalidOperationException("Config outputRoot is required.");
            NormalizeMode(config.mode);
            NormalizeRuntimeProfile(config.runtimeProfile);
            NormalizeUnknownCustomShaderPolicy(config.unknownCustomShaderPolicy);
            NormalizeTarget(config.target);
            NormalizeSourceRenderPipeline(config.sourceRenderPipeline);
            if (config.maxTextureSize <= 0 || config.maxTextureSize > 4096) throw new InvalidOperationException("maxTextureSize must be in [1, 4096].");
            if (config.effects == null || config.effects.Length == 0) throw new InvalidOperationException("Config effects must not be empty.");

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var effect in config.effects)
            {
                if (effect == null || !EffectIdPattern.IsMatch(effect.id ?? string.Empty))
                    throw new InvalidOperationException("Effect id must be lowercase kebab-case.");
                if (!ids.Add(effect.id)) throw new InvalidOperationException("Duplicate effect id: " + effect.id);
                if (!IsPrefabPath(effect.prefabPath)) throw new InvalidOperationException("Effect prefabPath must be an Assets/ or Packages/ .prefab path: " + effect.prefabPath);
                if (AssetDatabase.LoadAssetAtPath<GameObject>(effect.prefabPath) == null)
                    throw new FileNotFoundException("Effect prefab could not be loaded: " + effect.prefabPath);
                if (!string.IsNullOrWhiteSpace(effect.mode)) NormalizeMode(effect.mode);
                if (effect.maxTextureSize < 0 || effect.maxTextureSize > 4096)
                    throw new InvalidOperationException("Effect maxTextureSize must be 0 or in [1, 4096].");
                ValidateFilters(effect.includeParticleSystemPaths, "includeParticleSystemPaths");
                ValidateFilters(effect.excludeParticleSystemPaths, "excludeParticleSystemPaths");
            }
        }

        private static void ValidateFilters(string[] filters, string field)
        {
            foreach (var filter in filters ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(filter) || filter.Contains("..") || filter.StartsWith("/", StringComparison.Ordinal) || filter.Contains("\\"))
                    throw new InvalidOperationException(field + " contains an invalid hierarchy path: " + filter);
            }
        }

        private static void RejectSelectionMetadata(string rawConfig)
        {
            var forbiddenKeys = new[] { "catalogAssetId", "assetRole", "selectionScore", "gameplayCategory", "licenseProvenance" };
            foreach (var key in forbiddenKeys)
            {
                if (Regex.IsMatch(rawConfig, "\\\"" + Regex.Escape(key) + "\\\"\\s*:", RegexOptions.CultureInvariant))
                    throw new InvalidOperationException("Exporter config must not contain Catalog, role, license, or gameplay metadata: " + key);
            }
        }

        private static string ResolveOutputRoot(string value, string configDirectory)
        {
            var output = Path.IsPathRooted(value) ? Path.GetFullPath(value) : Path.GetFullPath(Path.Combine(configDirectory, value));
            var packages = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Packages"));
            if (IsWithin(packages, output)) throw new InvalidOperationException("Unity VFX outputRoot must not be inside the Unity Packages directory.");
            return output;
        }

        private static bool IsWithin(string root, string candidate)
        {
            var relative = candidate.Substring(0, Math.Min(candidate.Length, root.Length));
            return string.Equals(relative, root, StringComparison.OrdinalIgnoreCase) &&
                   (candidate.Length == root.Length || candidate[root.Length] == Path.DirectorySeparatorChar || candidate[root.Length] == Path.AltDirectorySeparatorChar);
        }

        private static string SourceFingerprint(string prefabPath)
        {
            var builder = new StringBuilder();
            foreach (var dependency in AssetDatabase.GetDependencies(prefabPath, true).Select(NormalizeAssetPath).OrderBy(path => path, StringComparer.Ordinal))
            {
                builder.Append(dependency).Append('\n');
                builder.Append(AssetDatabase.GetAssetDependencyHash(dependency)).Append('\n');
            }
            return UnityParticleQuarksStableId.Hash(builder.ToString());
        }

        private static void ValidateStaging(string stagingRoot, UnityParticleQuarksPipelineManifest manifest)
        {
            var manifestPath = Path.Combine(stagingRoot, "manifest.json");
            if (!File.Exists(manifestPath)) throw new InvalidOperationException("Staged manifest is missing.");
            foreach (var effect in manifest.effects)
            {
                var reportPath = Path.Combine(stagingRoot, effect.conversionReport.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(reportPath)) throw new InvalidOperationException("Staged conversion report is missing for " + effect.id);
                if (effect.status == "ready" || effect.status == "partial" || effect.status == "profile_required" || effect.status == "review_only")
                {
                    var jsonPath = Path.Combine(stagingRoot, effect.effectJson.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(jsonPath) || !File.ReadAllText(jsonPath).Contains("\"ParticleEmitter\""))
                        throw new InvalidOperationException("Staged Quarks JSON is invalid for " + effect.id);
                }
                else if (!string.IsNullOrEmpty(effect.effectJson))
                {
                    throw new InvalidOperationException("Failed effect published effectJson: " + effect.id);
                }
            }
        }

        private static void Publish(string stagingRoot, string outputRoot, string backupRoot)
        {
            DeleteDirectory(backupRoot);
            if (Directory.Exists(outputRoot)) Directory.Move(outputRoot, backupRoot);
            try
            {
                Directory.Move(stagingRoot, outputRoot);
                DeleteDirectory(backupRoot);
            }
            catch
            {
                if (!Directory.Exists(outputRoot) && Directory.Exists(backupRoot)) Directory.Move(backupRoot, outputRoot);
                throw;
            }
        }

        private static void WriteJson(string path, object value)
        {
            var json = JsonUtility.ToJson(value, true).Replace("\r\n", "\n") + "\n";
            File.WriteAllText(path, json, Utf8NoBom);
        }

        private static void DeleteDirectory(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }

        private static string GetArgument(string name)
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
                if (string.Equals(arguments[index], name, StringComparison.Ordinal)) return arguments[index + 1];
            return null;
        }

        private static string NormalizeMode(string value)
        {
            var mode = (value ?? "strict").Trim().ToLowerInvariant();
            if (mode != "strict" && mode != "best-effort") throw new InvalidOperationException("Conversion mode must be strict or best-effort.");
            return mode;
        }

        private static string NormalizeRuntimeProfile(string value)
        {
            var profile = (string.IsNullOrWhiteSpace(value) ? "extended" : value).Trim().ToLowerInvariant();
            if (profile != "stock" && profile != "extended")
                throw new InvalidOperationException("runtimeProfile must be stock or extended.");
            return profile;
        }

        private static string NormalizeUnknownCustomShaderPolicy(string value)
        {
            var policy = (string.IsNullOrWhiteSpace(value) ? "require-profile" : value).Trim().ToLowerInvariant();
            if (policy != "require-profile" && policy != "review-fallback")
                throw new InvalidOperationException("unknownCustomShaderPolicy must be require-profile or review-fallback.");
            return policy;
        }

        private static UnityParticleQuarksExtensionDescriptor[] UnityPairedSemanticsExtensions()
        {
            return new[]
            {
                new UnityParticleQuarksExtensionDescriptor
                {
                    id = UnityPairedSemanticsExtensionId,
                    version = UnityPairedSemanticsExtensionVersion
                }
            };
        }

        private static string NormalizeTarget(string value)
        {
            var target = (string.IsNullOrWhiteSpace(value) ? "default" : value).Trim().ToLowerInvariant();
            if (target != "default" && target != "presentation")
                throw new InvalidOperationException("Conversion target must be default or presentation.");
            return target;
        }

        private static string NormalizeSourceRenderPipeline(string value)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized == string.Empty || normalized == "current") return string.Empty;
            if (normalized == "default" || normalized == "urp" || normalized == "hdrp") return normalized;
            throw new InvalidOperationException("sourceRenderPipeline must be current, default, urp, or hdrp.");
        }

        private static string ResolveSourceRenderPipeline(string value)
        {
            var normalized = NormalizeSourceRenderPipeline(value);
            if (normalized != string.Empty) return normalized;
            if (GraphicsSettings.currentRenderPipeline == null) return "default";

            var typeName = GraphicsSettings.currentRenderPipeline.GetType().FullName ?? string.Empty;
            if (typeName.IndexOf("UniversalRenderPipeline", StringComparison.OrdinalIgnoreCase) >= 0) return "urp";
            if (typeName.IndexOf("HDRenderPipeline", StringComparison.OrdinalIgnoreCase) >= 0) return "hdrp";
            return "custom";
        }

        private static bool IsPrefabPath(string path)
        {
            var normalized = NormalizeAssetPath(path);
            return (normalized.StartsWith("Assets/", StringComparison.Ordinal) || normalized.StartsWith("Packages/", StringComparison.Ordinal)) &&
                   normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) && !normalized.Contains("..");
        }

        private static string NormalizeAssetPath(string path) => (path ?? string.Empty).Replace('\\', '/').Trim();
        private static string NormalizeHierarchyPath(string path) => (path ?? string.Empty).Trim('/').Trim();
    }
}

namespace UnityParticleQuarksExporter.Editor
{
    public static class ParticleQuarksExportBatchmode
    {
        // Keep the batch entry point in the package assembly for headless review exports.
        public static void RunBatch()
        {
            UnityParticleQuarksExporter.Editor.UnityParticleQuarksExportBatchmode.RunBatchWithArgument(
                "-particleQuarksConfig",
                "Unity ParticleSystem to Quarks");
        }
    }
}
