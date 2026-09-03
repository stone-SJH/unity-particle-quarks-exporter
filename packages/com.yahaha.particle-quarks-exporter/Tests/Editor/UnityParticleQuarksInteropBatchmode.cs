using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityParticleQuarksExporter.Editor.Tests
{
    public static class UnityParticleQuarksInteropBatchmode
    {
        private const string FixtureRoot = "Assets/__UnityParticleQuarksInterop";

        public static void ConfigureProjectForTests()
        {
            try
            {
                var pipeline = (GetArgument("-unityParticleQuarksTestPipeline") ?? "built-in").ToLowerInvariant();
                if (pipeline == "built-in")
                {
                    GraphicsSettings.defaultRenderPipeline = null;
                    QualitySettings.renderPipeline = null;
                }
                else if (pipeline == "urp")
                {
                    var assetType = Type.GetType(
                        "UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset, Unity.RenderPipelines.Universal.Runtime");
                    if (assetType == null)
                        throw new InvalidOperationException("The URP package is required for the URP contract tuple.");
                    var assetPath = "Assets/__UnityParticleQuarksTestPipeline.asset";
                    AssetDatabase.DeleteAsset(assetPath);
                    AssetDatabase.DeleteAsset("Assets/UniversalRenderer.asset");
                    var create = assetType.GetMethods().Single(method =>
                        method.Name == "Create" && method.IsStatic && method.GetParameters().Length == 1);
                    var asset = create.Invoke(null, new object[] { null }) as RenderPipelineAsset;
                    if (asset == null) throw new InvalidOperationException("Could not create the URP pipeline asset.");
                    AssetDatabase.CreateAsset(asset, assetPath);
                    var loadRenderer = assetType.GetMethods().Single(method =>
                        method.Name == "LoadBuiltinRendererData" && method.GetParameters().Length == 1);
                    var rendererType = loadRenderer.GetParameters()[0].ParameterType;
                    loadRenderer.Invoke(asset, new[] { Enum.ToObject(rendererType, 0) });
                    GraphicsSettings.defaultRenderPipeline = asset;
                    QualitySettings.renderPipeline = asset;
                }
                else
                {
                    throw new InvalidOperationException("Test pipeline must be built-in or urp.");
                }

                AssetDatabase.SaveAssets();
                Debug.Log("Configured Unity particle contract tests for " + pipeline + ".");
                if (Application.isBatchMode) EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                else throw;
            }
        }

        public static void Run()
        {
            try
            {
                var output = GetArgument("-unityParticleQuarksInteropOutput");
                if (string.IsNullOrWhiteSpace(output)) throw new InvalidOperationException("-unityParticleQuarksInteropOutput is required.");
                var prefabPath = FixtureRoot + "/InteropEffect.prefab";
                var reuseFixture = HasArgument("-unityParticleQuarksInteropReuseFixture");
                if (!reuseFixture || AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
                {
                    AssetDatabase.DeleteAsset(FixtureRoot);
                    AssetDatabase.CreateFolder("Assets", "__UnityParticleQuarksInterop");
                    var gameObject = new GameObject("InteropEffect");
                    var system = gameObject.AddComponent<ParticleSystem>();
                    var shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended") ??
                                 Shader.Find("Particles/Standard Unlit");
                    if (shader == null) throw new InvalidOperationException("Interop fixture could not resolve a built-in unlit shader.");
                    var material = new Material(shader) { name = "InteropMaterial" };
                    var materialPath = FixtureRoot + "/InteropMaterial.mat";
                    AssetDatabase.CreateAsset(material, materialPath);
                    var sourceTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                    sourceTexture.SetPixels(new[] { Color.cyan, Color.white, Color.blue, Color.clear });
                    sourceTexture.Apply();
                    var texturePath = FixtureRoot + "/InteropTexture.png";
                    File.WriteAllBytes(texturePath, sourceTexture.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(sourceTexture);
                    AssetDatabase.ImportAsset(texturePath, ImportAssetOptions.ForceSynchronousImport);
                    var importedTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
                    if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", importedTexture);
                    else if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", importedTexture);
                    else throw new InvalidOperationException("Interop fixture shader has no supported base texture property.");
                    gameObject.GetComponent<ParticleSystemRenderer>().sharedMaterial = material;
                    var main = system.main;
                    main.loop = false;
                    main.duration = 0.25f;
                    main.startLifetime = new ParticleSystem.MinMaxCurve(1.5f, AnimationCurve.EaseInOut(0, 0.2f, 1, 1));
                    var gradient = new Gradient();
                    gradient.SetKeys(
                        new[] { new GradientColorKey(Color.cyan, 0), new GradientColorKey(Color.white, 1) },
                        new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0, 1) });
                    main.startColor = new ParticleSystem.MinMaxGradient(gradient);
                    var emission = system.emission;
                    emission.rateOverTime = 0;
                    emission.SetBursts(new[] { new ParticleSystem.Burst(0, (short)8) });
                    PrefabUtility.SaveAsPrefabAsset(gameObject, prefabPath);
                    UnityEngine.Object.DestroyImmediate(gameObject);
                }

                var configPath = Path.GetFullPath(output) + ".config.json";
                var config = new UnityParticleQuarksPipelineConfig
                {
                    schemaVersion = "unity_particle_quarks_pipeline.config.v1",
                    outputRoot = Path.GetFullPath(output),
                    mode = "strict",
                    maxTextureSize = 256,
                    effects = new[]
                    {
                        new UnityParticleQuarksEffectRequest
                        {
                            id = "interop-effect",
                            prefabPath = prefabPath,
                            includeParticleSystemPaths = Array.Empty<string>(),
                            excludeParticleSystemPaths = Array.Empty<string>()
                        }
                    }
                };
                File.WriteAllText(configPath, JsonUtility.ToJson(config, true), new UTF8Encoding(false));
                UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
                File.Delete(configPath);
                if (!reuseFixture) AssetDatabase.DeleteAsset(FixtureRoot);
                Debug.Log("Unity Particle Unity/Quarks interop fixture exported to " + output);
                if (Application.isBatchMode) EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                AssetDatabase.DeleteAsset(FixtureRoot);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                else throw;
            }
        }

        private static string GetArgument(string name)
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
                if (string.Equals(arguments[index], name, StringComparison.Ordinal)) return arguments[index + 1];
            return null;
        }

        private static bool HasArgument(string name)
        {
            return Environment.GetCommandLineArgs().Any(argument =>
                string.Equals(argument, name, StringComparison.Ordinal));
        }
    }
}
