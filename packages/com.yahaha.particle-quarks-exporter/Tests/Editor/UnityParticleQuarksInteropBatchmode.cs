using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityParticleQuarksExporter.Editor.Tests
{
    public static class UnityParticleQuarksInteropBatchmode
    {
        private const string FixtureRoot = "Assets/__UnityParticleQuarksInterop";

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
                    var shader = Shader.Find("Unlit/Color") ?? Shader.Find("Particles/Standard Unlit");
                    if (shader == null) throw new InvalidOperationException("Interop fixture could not resolve a built-in unlit shader.");
                    var material = new Material(shader) { name = "InteropMaterial" };
                    var materialPath = FixtureRoot + "/InteropMaterial.mat";
                    AssetDatabase.CreateAsset(material, materialPath);
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
