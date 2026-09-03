using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UnityParticleQuarksExporter.Editor.Tests
{
    public sealed class UnityParticleQuarksExporterTests
    {
        private const string FixtureRoot = "Assets/__UnityParticleQuarksExporterTests";
        private string outputRoot;
        private string configPath;
        private Material fixtureMaterial;
        private Mesh fixtureRendererMesh;

        [SetUp]
        public void SetUp()
        {
            AssetDatabase.DeleteAsset(FixtureRoot);
            AssetDatabase.CreateFolder("Assets", "__UnityParticleQuarksExporterTests");
            var shader = Shader.Find("Legacy Shaders/Particles/Additive");
            Assert.That(shader, Is.Not.Null, "Unity's built-in particle shader is required by exporter fixtures.");
            fixtureMaterial = new Material(shader);
            AssetDatabase.CreateAsset(fixtureMaterial, FixtureRoot + "/FixtureParticle.mat");
            fixtureRendererMesh = CreateShapeMesh("FixtureRendererMesh", false);
            outputRoot = Path.Combine(Path.GetTempPath(), "unity-particle-quarks-tests", Guid.NewGuid().ToString("N"));
            configPath = Path.Combine(outputRoot + "-config", "config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath));
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(FixtureRoot);
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            var configDirectory = Path.GetDirectoryName(configPath);
            if (Directory.Exists(configDirectory)) Directory.Delete(configDirectory, true);
        }

        [Test]
        public void StableId_IsDeterministicAndSlotSensitive()
        {
            var first = UnityParticleQuarksStableId.Create("Assets/VFX/Water.prefab", "Root/Splash", "emitter");
            var second = UnityParticleQuarksStableId.Create("Assets\\VFX\\Water.prefab", "Root/Splash", "emitter");
            var other = UnityParticleQuarksStableId.Create("Assets/VFX/Water.prefab", "Root/Splash", "material");
            Assert.That(first, Is.EqualTo(second));
            Assert.That(Guid.TryParse(first, out _), Is.True);
            Assert.That(other, Is.Not.EqualTo(first));
        }

        [Test]
        public void UnknownShapeArcMode_UsesNamedRandomFallback()
        {
            var diagnostics = new ConversionDiagnostics();
            var value = QuarksParticleEmissionShapeConverter.EmitterMode(
                (ParticleSystemShapeMultiModeValue)int.MaxValue,
                "shape.fixture.arcMode",
                diagnostics);

            Assert.That(value, Is.EqualTo(0));
            CollectionAssert.Contains(diagnostics.unsupported, "shape.fixture.arcMode.unknown");
            CollectionAssert.Contains(
                diagnostics.approximated,
                "shape.fixture.arcMode.unknown.randomFallback");
        }

        [Test]
        public void GammaProjectParticleColors_ArePreservedForStockQuarksShader()
        {
            Assert.That(QualitySettings.activeColorSpace, Is.EqualTo(ColorSpace.Gamma));
            var prefabPath = CreatePrefab("GammaColor", system =>
            {
                var main = system.main;
                main.startColor = new Color(0.5f, 0.25f, 0.75f, 0.4f);
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            Assert.That(manifest.exporterVersion, Is.EqualTo("0.3.3"));
            Assert.That(manifest.runtimeProfile, Is.EqualTo("extended"));
            Assert.That(manifest.effects.Single().runtimeTier, Is.EqualTo("paired"));
            Assert.That(manifest.effects.Single().extensionsRequired.Single().id,
                Is.EqualTo("unity_particle_paired_semantics"));
            StringAssert.Contains("\"r\":0.5", json);
            StringAssert.Contains("\"g\":0.25", json);
            StringAssert.Contains("\"b\":0.75", json);
            StringAssert.Contains("\"a\":0.4", json);
            StringAssert.Contains("colorSpace.gammaPassThrough", report);
            StringAssert.Contains("\"runtimeTier\": \"paired\"", report);
            StringAssert.Contains("main.maxParticles.runtimeCapacity", report);
            StringAssert.Contains("main.maxParticles.stockUnboundedFallback", report);
            StringAssert.Contains("no colorspace_fragment", report);
        }

        [Test]
        public void ReadyExport_WritesRuntimeManifestThatMatchesThePipelineManifest()
        {
            var prefabPath = CreatePrefab("RuntimeManifestReady", _ => { });
            WriteConfig(prefabPath, "strict", runtimeProfile: "stock");

            var pipelineManifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var runtimeManifestPath = Path.Combine(outputRoot, "runtime-manifest.json");
            Assert.That(File.Exists(runtimeManifestPath), Is.True);

            var runtimeJson = File.ReadAllText(runtimeManifestPath);
            var runtimeManifest = JsonUtility.FromJson<UnityParticleQuarksRuntimeManifest>(runtimeJson);
            var pipelineEffect = pipelineManifest.effects.Single();
            var runtimeEffect = runtimeManifest.effects.Single();

            Assert.That(runtimeManifest.schemaVersion, Is.EqualTo("unity_particle_quarks_runtime.manifest.v1"));
            Assert.That(runtimeEffect.id, Is.EqualTo(pipelineEffect.id));
            Assert.That(runtimeEffect.url, Is.EqualTo(pipelineEffect.effectJson));
            Assert.That(runtimeEffect.status, Is.EqualTo("ready"));
            Assert.That(runtimeEffect.runtimeProfile, Is.EqualTo("stock"));
            Assert.That(runtimeEffect.runtimeTier, Is.EqualTo("stock"));
            Assert.That(runtimeEffect.extensionsUsed.Single().id, Is.EqualTo("unity_particle_paired_semantics"));
            Assert.That(runtimeEffect.extensionsRequired, Is.Empty);
            Assert.That(runtimeEffect.conversionReport, Is.EqualTo(pipelineEffect.conversionReport));
            StringAssert.DoesNotContain("\"effectJson\"", runtimeJson);
        }

        [Test]
        public void RuntimeManifestProjection_MatchesTheSharedContractFixture()
        {
            var pipelineFixture = AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Packages/com.yahaha.particle-quarks-exporter/Tests/Fixtures/pipeline-manifest.json");
            var runtimeFixture = AssetDatabase.LoadAssetAtPath<TextAsset>(
                "Packages/com.yahaha.particle-quarks-exporter/Tests/Fixtures/runtime-manifest.json");
            Assert.That(pipelineFixture, Is.Not.Null);
            Assert.That(runtimeFixture, Is.Not.Null);

            var pipelineManifest = JsonUtility.FromJson<UnityParticleQuarksPipelineManifest>(pipelineFixture.text);
            var runtimeManifest = UnityParticleQuarksExportBatchmode.CreateRuntimeManifest(pipelineManifest);

            Assert.That(runtimeManifest, Is.Not.Null);
            Assert.That(
                JsonUtility.ToJson(runtimeManifest, true).Replace("\r\n", "\n").TrimEnd(),
                Is.EqualTo(runtimeFixture.text.Replace("\r\n", "\n").TrimEnd()));
        }

        [Test]
        public void BlockedMixedExport_RemovesPreviouslyPublishedRuntimeManifest()
        {
            var readyPrefab = CreatePrefab("RuntimeManifestReadyFirst", _ => { });
            WriteConfig(readyPrefab, "strict", runtimeProfile: "stock");
            UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            Assert.That(File.Exists(Path.Combine(outputRoot, "runtime-manifest.json")), Is.True);

            var blockedPrefab = CreatePrefab("RuntimeManifestBlocked", system =>
            {
                var collision = system.collision;
                collision.enabled = true;
            });
            var config = new UnityParticleQuarksPipelineConfig
            {
                schemaVersion = UnityParticleQuarksExportBatchmode.ConfigSchema,
                outputRoot = outputRoot,
                mode = "strict",
                runtimeProfile = "stock",
                target = "default",
                maxTextureSize = 256,
                effects = new[]
                {
                    EffectRequest("ready-effect", readyPrefab),
                    EffectRequest("blocked-effect", blockedPrefab)
                }
            };
            File.WriteAllText(configPath, JsonUtility.ToJson(config, true));

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);

            Assert.That(manifest.effects.Single(effect => effect.id == "ready-effect").status, Is.EqualTo("ready"));
            Assert.That(manifest.effects.Single(effect => effect.id == "blocked-effect").status, Is.EqualTo("failed"));
            Assert.That(File.Exists(Path.Combine(outputRoot, "manifest.json")), Is.True);
            Assert.That(File.Exists(Path.Combine(outputRoot, "runtime-manifest.json")), Is.False);
        }

        [Test]
        public void ConstantStartDelay_IsExportedForPairedRuntimeEvaluation()
        {
            var prefabPath = CreatePrefab("ConstantStartDelay", system =>
            {
                var main = system.main;
                main.startDelay = new ParticleSystem.MinMaxCurve(1.5f);
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"schemaVersion\":\"unity_particle_quarks_exporter.start_delay.v1\"", json);
            StringAssert.Contains("\"mode\":\"constant\",\"value\":{\"type\":\"ConstantValue\",\"value\":1.5}", json);
            StringAssert.Contains("main.startDelay.runtime", report);
        }

        [Test]
        public void LinearGradientKeysUseDirectConversionWithoutFallback()
        {
            var diagnostics = new ConversionDiagnostics();
            var gradient = new Gradient
            {
                colorSpace = ColorSpace.Linear
            };
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.black, 0f),
                    new GradientColorKey(Color.white, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 1f)
                });
            var value = QuarksParticleSemanticsUtility.GradientJson(
                gradient,
                diagnostics,
                "gradient.fixture",
                null);
            Assert.That(value, Is.Not.Null);

            CollectionAssert.DoesNotContain(
                diagnostics.unsupported,
                "gradient.fixture.linearGradientColorSpace");
            CollectionAssert.Contains(
                diagnostics.mapped,
                "gradient.fixture.linearGradientColorSpace.directKeys");
            Assert.That(
                diagnostics.warnings.Any(item =>
                    item.IndexOf("Linear", StringComparison.OrdinalIgnoreCase) >= 0),
                Is.False);
        }

        [Test]
        public void StockProfile_UsesGenericSchemaWithoutRequiringTheAdapter()
        {
            var prefabPath = CreatePrefab("StockProfile", _ => { });
            WriteConfig(
                prefabPath,
                "strict",
                runtimeProfile: "stock",
                schemaVersion: "unity_particle_quarks_pipeline.config.v1");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.schemaVersion, Is.EqualTo("unity_particle_quarks_pipeline.manifest.v1"));
            Assert.That(manifest.runtimeProfile, Is.EqualTo("stock"));
            Assert.That(manifest.effects.Single().runtimeProfile, Is.EqualTo("stock"));
            Assert.That(manifest.effects.Single().runtimeTier, Is.EqualTo("stock"));
            Assert.That(manifest.effects.Single().extensionsUsed.Single().id,
                Is.EqualTo("unity_particle_paired_semantics"));
            Assert.That(manifest.effects.Single().extensionsRequired, Is.Empty);
            StringAssert.Contains("\"schemaVersion\": \"unity_particle_quarks_conversion.report.v1\"", report);
            StringAssert.Contains("\"runtimeProfile\": \"stock\"", report);
            StringAssert.Contains("\"runtimeTier\": \"stock\"", report);
            StringAssert.Contains("\"generator\":\"UnityParticleQuarksExporter\"", json);
            StringAssert.Contains("\"unityParticleQuarks\"", json);
            StringAssert.Contains("\"unityParticleQuarks\"", json);
        }

        [Test]
        public void LegacyConfigWithoutRuntimeProfile_DefaultsToExtended()
        {
            var prefabPath = CreatePrefab("LegacyProfileDefault", _ => { });
            WriteConfig(prefabPath, "strict", runtimeProfile: null);

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);

            Assert.That(manifest.schemaVersion, Is.EqualTo("unity_particle_quarks_pipeline.manifest.v1"));
            Assert.That(manifest.runtimeProfile, Is.EqualTo("extended"));
            Assert.That(manifest.effects.Single().runtimeTier, Is.EqualTo("paired"));
            Assert.That(manifest.effects.Single().extensionsRequired, Has.Length.EqualTo(1));
            Assert.That(typeof(UnityParticleQuarksExportBatchmode).Assembly.GetType(
                "UnityParticleQuarksExporter.Editor.ParticleQuarksExportBatchmode"), Is.Not.Null);
        }

        [Test]
        public void LegacyHdrTint_UsesDocumentedDoubleInParticleColor()
        {
            var material = new Material(Shader.Find("Legacy Shaders/Particles/Additive"));
            material.SetColor("_TintColor", new Color(0.75f, 0.25f, 0.1f, 0.8f));
            AssetDatabase.CreateAsset(material, FixtureRoot + "/LegacyHdrTint.mat");
            var prefabPath = CreatePrefab("LegacyHdrTint", system =>
                system.GetComponent<ParticleSystemRenderer>().sharedMaterial = material);
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"startColor\":{\"type\":\"ConstantColor\",\"color\":{\"r\":1.5,\"g\":0.5,\"b\":0.2,\"a\":1}}", json);
            StringAssert.DoesNotContain("\"materialColor\"", json);
            StringAssert.Contains("material.tintColor.legacyDouble", report);
            StringAssert.DoesNotContain("material.color.stockClampedFallback", report);
            StringAssert.DoesNotContain("material.shaderBehavior", report);
        }

        [Test]
        public void LegacyAnimAlphaBlended_IsAcceptedAsDocumentedParticleProfile()
        {
            var previousSoftParticles = QualitySettings.softParticles;
            QualitySettings.softParticles = true;
            try
            {
                var shader = Shader.Find("Legacy Shaders/Particles/Anim Alpha Blended");
                Assert.That(shader, Is.Not.Null);
                var material = new Material(shader);
                material.SetColor("_TintColor", new Color(0.5f, 0.25f, 0.1f, 0.4f));
                material.SetFloat("_InvFade", 3);
                material.SetTexture("_MainTex", null);
                AssetDatabase.CreateAsset(material, FixtureRoot + "/LegacyAnimAlphaBlended.mat");
                var prefabPath = CreatePrefab("LegacyAnimAlphaBlended", system =>
                    system.GetComponent<ParticleSystemRenderer>().sharedMaterial = material);
                WriteConfig(prefabPath, "strict");

                var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
                var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
                var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

                Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
                StringAssert.Contains("\"startColor\":{\"type\":\"ConstantColor\",\"color\":{\"r\":1,\"g\":0.5,\"b\":0.2,\"a\":0.8}}", json);
                StringAssert.Contains("material.shaderProfile.builtin.particleAnimAlphaBlended", report);
                StringAssert.Contains("material.shader.meshBasicSubset", report);
                StringAssert.Contains("material.tintColor.legacyDouble", report);
                StringAssert.Contains("material.softParticles.legacyInvFade", report);
                StringAssert.DoesNotContain("material.shaderBehavior", report);
            }
            finally
            {
                QualitySettings.softParticles = previousSoftParticles;
            }
        }

        [Test]
        public void LegacyVertexLitMesh_ExportsStandardMaterialAndNormals()
        {
            var shader = Shader.Find("Legacy Shaders/Particles/VertexLit Blended");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            material.SetColor("_Color", new Color(0.5f, 0.4f, 0.2f, 1));
            material.SetColor("_EmisColor", new Color(0.2f, 0.1f, 0.05f, 1));
            AssetDatabase.CreateAsset(material, FixtureRoot + "/VertexLit.mat");
            var prefabPath = CreatePrefab("VertexLit", system =>
            {
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Mesh;
                renderer.sharedMaterial = material;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"type\":\"MeshStandardMaterial\"", json);
            StringAssert.Contains("\"alphaTest\":0", json);
            StringAssert.Contains("\"normal\":{\"itemSize\":3", json);
            StringAssert.Contains("material.shader.vertexLitToStandard", report);
            StringAssert.Contains("material.emissive.legacyVertexLit", report);
        }

        [Test]
        public void LegacyVertexLitBillboard_ReportsUnlitEmissionCompositionFallback()
        {
            var shader = Shader.Find("Legacy Shaders/Particles/VertexLit Blended");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            material.SetColor("_Color", new Color(0.5f, 0.4f, 0.2f, 1));
            material.SetColor("_EmisColor", new Color(0.2f, 0.1f, 0.05f, 1));
            AssetDatabase.CreateAsset(material, FixtureRoot + "/VertexLitBillboard.mat");
            var prefabPath = CreatePrefab("VertexLitBillboard", system =>
                system.GetComponent<ParticleSystemRenderer>().sharedMaterial = material);

            WriteConfig(prefabPath, "best-effort");
            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));
            Assert.That(manifest.effects.Single().status, Is.EqualTo("partial"));
            StringAssert.Contains("material.emission.unlitComposition.foldedParticleColorFallback", report);

            WriteConfig(prefabPath, "strict", "presentation");
            manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));
            Assert.That(manifest.effects.Single().status, Is.EqualTo("partial"));
            Assert.That(File.Exists(Path.Combine(outputRoot, "fixture", "effect.quarks.json")), Is.True);
            StringAssert.Contains("material.emission.unlitComposition", report);
            StringAssert.Contains("folds emissive color into particle color", report);

            WriteConfig(prefabPath, "strict");
            manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);
            Assert.That(manifest.effects.Single().status, Is.EqualTo("failed"));
        }

        [Test]
        public void StandardMesh_NeutralizesUnconsumedParticleColorAndMapsPbrMaterial()
        {
            var shader = Shader.Find("Standard");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            material.SetColor("_Color", new Color(0.35f, 0.35f, 0.35f, 1));
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", new Color(0.45f, 0.45f, 0.45f, 1));
            material.SetFloat("_Metallic", 0);
            material.SetFloat("_Glossiness", 0);
            AssetDatabase.CreateAsset(material, FixtureRoot + "/Standard.mat");
            var prefabPath = CreatePrefab("Standard", system =>
            {
                var main = system.main;
                main.startColor = Color.red;
                var color = system.colorOverLifetime;
                color.enabled = true;
                color.color = Color.green;
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Mesh;
                renderer.sharedMaterial = material;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"type\":\"MeshStandardMaterial\"", json);
            StringAssert.Contains("\"startColor\":{\"type\":\"ConstantColor\",\"color\":{\"r\":1,\"g\":1,\"b\":1,\"a\":1}}", json);
            StringAssert.DoesNotContain("\"type\":\"ColorOverLife\"", json);
            StringAssert.Contains("colorOverLifetime.notConsumedBySourceShader", report);
            StringAssert.Contains("material.shader.litProfileToThreePbr", report);
        }

        [Test]
        public void StandardCutoutMeshGridAtlas_UsesExplicitUnlitQuarksFallback()
        {
            var shader = Shader.Find("Standard");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            material.SetFloat("_Mode", 1);
            material.SetFloat("_Cutoff", 0.5f);
            material.EnableKeyword("_ALPHATEST_ON");
            AssetDatabase.CreateAsset(material, FixtureRoot + "/StandardCutoutGrid.mat");
            var prefabPath = CreatePrefab("StandardCutoutGrid", system =>
            {
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Mesh;
                renderer.sharedMaterial = material;
                var sheet = system.textureSheetAnimation;
                sheet.enabled = true;
                sheet.mode = ParticleSystemAnimationMode.Grid;
                sheet.numTilesX = 1;
                sheet.numTilesY = 8;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"), report);
            StringAssert.Contains("\"type\":\"MeshBasicMaterial\"", json);
            StringAssert.Contains("\"alphaTest\":0.5", json);
            StringAssert.Contains("material.shader.pbrAlphaAtlasUnlitFallback", report);
            StringAssert.DoesNotContain("material.shader.litProfileToThreePbr", report);
        }

        [Test]
        public void StandardTransparentBillboard_IgnoresInactiveEmissionKeyword()
        {
            var shader = Shader.Find("Standard");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            material.SetFloat("_Mode", 3);
            material.SetFloat("_Cutoff", 0.5f);
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0);
            material.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            material.EnableKeyword("_EMISSION");
            material.SetColor("_Color", new Color(1, 1, 1, 0));
            material.SetColor("_EmissionColor", new Color(0.1f, 0.1f, 0.1f, 0.1f));
            material.renderQueue = 3000;
            AssetDatabase.CreateAsset(material, FixtureRoot + "/StandardTransparent.mat");
            material.shaderKeywords = new[] { "_ALPHAPREMULTIPLY_ON", "_EMISSION" };
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            material = AssetDatabase.LoadAssetAtPath<Material>(FixtureRoot + "/StandardTransparent.mat");
            Assert.That(material.IsKeywordEnabled("_EMISSION"), Is.True);
            var prefabPath = CreatePrefab("StandardTransparent", system =>
            {
                var color = system.colorOverLifetime;
                color.enabled = true;
                color.color = Color.red;
                system.GetComponent<ParticleSystemRenderer>().sharedMaterial = material;
            });
            WriteConfig(prefabPath, "best-effort");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"), report);
            StringAssert.Contains("\"alphaTest\":0", json);
            StringAssert.Contains("\"blending\":5", json);
            StringAssert.Contains("\"blendSrc\":201", json);
            StringAssert.Contains("\"startColor\":{\"type\":\"ConstantColor\",\"color\":{\"r\":0,\"g\":0,\"b\":0,\"a\":0}}", json);
            StringAssert.DoesNotContain("\"type\":\"ColorOverLife\"", json);
            StringAssert.DoesNotContain("material.emissive.standard", report);
        }

        [Test]
        public void SpritesDefault_IsAcceptedAsVertexColorAlphaSubset()
        {
            var material = AssetDatabase.GetBuiltinExtraResource<Material>("Sprites-Default.mat");
            Assert.That(material, Is.Not.Null);
            var prefabPath = CreatePrefab("SpritesDefault", system =>
            {
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Mesh;
                renderer.sharedMaterial = material;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            Assert.That(manifest.effects.Single().textures, Is.Empty);
            StringAssert.Contains("\"side\":2", json);
            StringAssert.DoesNotContain("\"map\"", json);
            StringAssert.Contains("material.doubleSide", report);
            StringAssert.Contains("material.shader.meshBasicSubset", report);
            StringAssert.DoesNotContain("material.unityDefaultParticle", report);
            StringAssert.DoesNotContain("material.shaderBehavior", report);
        }

        [Test]
        public void WholeSheetLifetime_PreservesCyclesAndStartFrameForPairedRuntime()
        {
            var prefabPath = CreatePrefab("TextureSheetCycles", system =>
            {
                var sheet = system.textureSheetAnimation;
                sheet.enabled = true;
                sheet.mode = ParticleSystemAnimationMode.Grid;
                sheet.animation = ParticleSystemAnimationType.WholeSheet;
                sheet.timeMode = ParticleSystemAnimationTimeMode.Lifetime;
                sheet.numTilesX = 5;
                sheet.numTilesY = 4;
                sheet.cycleCount = 30;
                sheet.startFrame = 0.1f;
                sheet.frameOverTime = new ParticleSystem.MinMaxCurve(
                    0.9999f,
                    AnimationCurve.Linear(0, 0, 1, 1));
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("unity_particle_quarks_exporter.texture_sheet_animation.v2", json);
            StringAssert.Contains("\"frameCount\":20", json);
            StringAssert.Contains("\"tileCountX\":5", json);
            StringAssert.Contains("\"tileCountY\":4", json);
            StringAssert.Contains("\"cycleCount\":30", json);
            StringAssert.Contains("\"startFrame\":{\"mode\":\"constant\",\"value\":{\"type\":\"ConstantValue\",\"value\":0.1}}", json);
            StringAssert.Contains("\"blendTiles\":false", json);
            StringAssert.Contains("\"p3\":19.998", json);
            StringAssert.Contains("textureSheetAnimation.timeMode.lifetime.runtime", report);
            StringAssert.Contains("textureSheetAnimation.stockSingleCycleFallback", report);
        }

        [Test]
        public void WholeSheetFpsTimeMode_UsesPairedRuntimeMetadata()
        {
            var prefabPath = CreatePrefab("TextureSheetFps", system =>
            {
                var sheet = system.textureSheetAnimation;
                sheet.enabled = true;
                sheet.mode = ParticleSystemAnimationMode.Grid;
                sheet.animation = ParticleSystemAnimationType.WholeSheet;
                sheet.timeMode = ParticleSystemAnimationTimeMode.FPS;
                sheet.numTilesX = 2;
                sheet.numTilesY = 2;
                sheet.fps = 12;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("textureSheetAnimation.timeMode.fps.runtime", report);
            StringAssert.DoesNotContain("textureSheetAnimation.timeMode.FPS", report);
        }

        [Test]
        public void SingleRowTextureSheet_ExportsSelectedRowAndFrameCount()
        {
            var prefabPath = CreatePrefab("TextureSheetSingleRow", system =>
            {
                var sheet = system.textureSheetAnimation;
                sheet.enabled = true;
                sheet.mode = ParticleSystemAnimationMode.Grid;
                sheet.animation = ParticleSystemAnimationType.SingleRow;
                sheet.timeMode = ParticleSystemAnimationTimeMode.Lifetime;
                sheet.numTilesX = 4;
                sheet.numTilesY = 3;
                sheet.rowMode = ParticleSystemAnimationRowMode.Custom;
                sheet.rowIndex = 2;
                sheet.frameOverTime = new ParticleSystem.MinMaxCurve(1);
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"animation\":\"singleRow\"", json);
            StringAssert.Contains("\"frameCount\":4", json);
            StringAssert.Contains("\"rowMode\":\"custom\"", json);
            StringAssert.Contains("\"rowIndex\":2", json);
            StringAssert.Contains("textureSheetAnimation.singleRow", report);
            StringAssert.DoesNotContain("textureSheetAnimation.singleRowOrSprites", report);
        }

        [Test]
        public void SpriteListTextureSheet_ExportsRectsPivotsAndTextureOverride()
        {
            var texture = new Texture2D(4, 2, TextureFormat.RGBA32, false);
            texture.SetPixels(new[]
            {
                Color.red, Color.red, Color.green, Color.green,
                Color.blue, Color.blue, Color.white, Color.white
            });
            texture.Apply();
            var texturePath = FixtureRoot + "/SpriteSheet.png.asset";
            AssetDatabase.CreateAsset(texture, texturePath);
            var first = Sprite.Create(texture, new Rect(0, 0, 2, 2), new Vector2(0.25f, 0.5f), 1);
            first.name = "SpriteSheet_First";
            var second = Sprite.Create(texture, new Rect(2, 0, 2, 2), new Vector2(0.75f, 0.5f), 1);
            second.name = "SpriteSheet_Second";
            AssetDatabase.AddObjectToAsset(first, texture);
            AssetDatabase.AddObjectToAsset(second, texture);
            AssetDatabase.SaveAssets();

            var shader = Shader.Find("Particles/Standard Unlit");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader) { name = "SpriteSheetMaterial" };
            material.SetTexture("_MainTex", texture);
            AssetDatabase.CreateAsset(material, FixtureRoot + "/SpriteSheetMaterial.mat");

            var prefabPath = CreatePrefab("TextureSheetSprites", system =>
            {
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                renderer.sharedMaterial = material;
                var sheet = system.textureSheetAnimation;
                sheet.enabled = true;
                sheet.mode = ParticleSystemAnimationMode.Sprites;
                sheet.timeMode = ParticleSystemAnimationTimeMode.FPS;
                sheet.fps = 12;
                sheet.AddSprite(first);
                sheet.AddSprite(second);
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"mode\":\"sprites\"", json);
            StringAssert.Contains("\"animation\":\"sprites\"", json);
            StringAssert.Contains("\"frameCount\":2", json);
            StringAssert.Contains("\"sprites\":[", json);
            StringAssert.Contains("textureSheetAnimation.sprites", report);
            StringAssert.DoesNotContain("textureSheetAnimation.sprites.multipleTextures", report);
        }

        [Test]
        public void StartColorGradient_RecordsNormalizedEmitterTimeRuntimeContract()
        {
            var prefabPath = CreatePrefab("NormalizedStartGradient", system =>
            {
                var gradient = new Gradient();
                gradient.SetKeys(
                    new[] { new GradientColorKey(Color.yellow, 0), new GradientColorKey(Color.red, 1) },
                    new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0.2f, 1) });
                var main = system.main;
                main.duration = 7;
                main.startColor = new ParticleSystem.MinMaxGradient(gradient);
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("unity_particle_quarks_exporter.start_color.v1", json);
            StringAssert.Contains("\"mode\":\"gradient\"", json);
            StringAssert.Contains("normalizedEmitterTimeRuntime", report);
            StringAssert.Contains("stockAbsoluteTimeFallback", report);
        }

        [Test]
        public void StartColorRandomColor_RecordsFullGradientSamplingRuntimeContract()
        {
            var prefabPath = CreatePrefab("RandomStartGradient", system =>
            {
                var gradient = new Gradient();
                gradient.SetKeys(
                    new[]
                    {
                        new GradientColorKey(Color.blue, 0),
                        new GradientColorKey(Color.green, 0.4f),
                        new GradientColorKey(Color.red, 1)
                    },
                    new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0.5f, 1) });
                var randomColor = new ParticleSystem.MinMaxGradient(gradient)
                {
                    mode = ParticleSystemGradientMode.RandomColor
                };
                var main = system.main;
                main.startColor = randomColor;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"mode\":\"randomColor\"", json);
            StringAssert.Contains("gradientSampleRuntime", report);
            StringAssert.Contains("stockEmissionTimeGradientFallback", report);
        }

        [TestCase(ParticleSystemScalingMode.Hierarchy, -2f, 3f, 4f)]
        [TestCase(ParticleSystemScalingMode.Hierarchy, 2f, -3f, 4f)]
        [TestCase(ParticleSystemScalingMode.Hierarchy, 2f, 3f, -4f)]
        [TestCase(ParticleSystemScalingMode.Local, -2f, 3f, 4f)]
        [TestCase(ParticleSystemScalingMode.Shape, 2f, 3f, -4f)]
        public void NegativeScale_UsesPositiveEmitterAndExplicitSignedBirthBasis(
            ParticleSystemScalingMode scalingMode,
            float x,
            float y,
            float z)
        {
            var prefabPath = CreatePrefab("Negative" + scalingMode + x + y + z, system =>
            {
                system.transform.localScale = new Vector3(x, y, z);
                var main = system.main;
                main.scalingMode = scalingMode;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.startRotation3D = true;
                main.startRotationX = 0.2f;
                var shape = system.shape;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.position = new Vector3(0.25f, 0.5f, 0.75f);
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            Assert.That(EmitterLinearDeterminant(json), Is.GreaterThan(0));
            StringAssert.Contains("negativeAxisRuntime", report);
            StringAssert.Contains("negativeAxis.stockMagnitudeFallback", report);
            StringAssert.Contains("\"birthPositionTransform\"", json);
            StringAssert.Contains("\"birthDirectionTransform\"", json);
        }

        [Test]
        public void NestedHierarchy_PreservesParentTransformAndModuleBases()
        {
            var root = new GameObject("NestedHierarchy");
            root.transform.SetPositionAndRotation(new Vector3(3, 5, 7), Quaternion.Euler(11, 23, 37));
            root.transform.localScale = new Vector3(-2, 2, 2);
            var child = new GameObject("Emitter");
            child.transform.SetParent(root.transform, false);
            child.transform.localPosition = new Vector3(1, -2, 0.5f);
            child.transform.localRotation = Quaternion.Euler(-19, 31, 7);
            child.transform.localScale = new Vector3(0.5f, 0.75f, 1.25f);
            var system = AddParticleSystem(child);
            var main = system.main;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 1.5f;
            var velocity = system.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            velocity.x = 1;
            var force = system.forceOverLifetime;
            force.enabled = true;
            force.space = ParticleSystemSimulationSpace.Local;
            force.y = 2;
            var path = FixtureRoot + "/NestedHierarchy.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);
            WriteConfig(path, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            Assert.That(EmitterLinearDeterminant(json), Is.GreaterThan(0));
            StringAssert.Contains("unity_particle_quarks_exporter.velocity_over_lifetime.v2", json);
            StringAssert.Contains("unity_particle_quarks_exporter.force_over_lifetime.v1", json);
            StringAssert.Contains("unity_particle_quarks_exporter.gravity.v1", json);
            StringAssert.Contains("main.scalingMode.hierarchy.negativeAxisRuntime", report);
            StringAssert.DoesNotContain("main.scalingMode.hierarchy.shear", report);
        }

        [Test]
        public void HierarchyShear_IsExplicitStrictFailureWithNamedFallback()
        {
            var root = new GameObject("ShearedHierarchy");
            root.transform.localScale = new Vector3(2, 3, 4);
            var child = new GameObject("Emitter");
            child.transform.SetParent(root.transform, false);
            child.transform.localRotation = Quaternion.Euler(17, 29, 0);
            var system = AddParticleSystem(child);
            var main = system.main;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            var path = FixtureRoot + "/ShearedHierarchy.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);
            WriteConfig(path, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("failed"));
            StringAssert.Contains("main.scalingMode.hierarchy.shear", report);
            StringAssert.Contains("orthogonalizedTrsFallback", report);
        }

        [Test]
        public void StrictExport_IsDeterministicAndMapsCurvesGradientsAndBursts()
        {
            var prefabPath = CreatePrefab("CurveGradient", system =>
            {
                var main = system.main;
                main.startLifetime = new ParticleSystem.MinMaxCurve(2, AnimationCurve.EaseInOut(0, 0.2f, 1, 1));
                var gradient = new Gradient();
                gradient.SetKeys(
                    new[] { new GradientColorKey(Color.cyan, 0), new GradientColorKey(Color.white, 1) },
                    new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0, 1) });
                main.startColor = new ParticleSystem.MinMaxGradient(gradient);
                var emission = system.emission;
                emission.SetBursts(new[] { new ParticleSystem.Burst(0.1f, (short)3, (short)7) });
            });
            WriteConfig(prefabPath, "strict");

            var first = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var firstJson = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var second = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var secondJson = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));

            Assert.That(first.effects.Single().status, Is.EqualTo("ready"));
            Assert.That(second.effects.Single().status, Is.EqualTo("ready"));
            Assert.That(secondJson, Is.EqualTo(firstJson));
            StringAssert.Contains("PiecewiseBezier", firstJson);
            StringAssert.Contains("Gradient", firstJson);
            StringAssert.Contains("emissionBursts", firstJson);
            StringAssert.Contains("ParticleEmitter", firstJson);
        }

        [TestCase(ParticleSystemShapeType.Cone, "\"type\":\"cone\"")]
        [TestCase(ParticleSystemShapeType.Sphere, "\"type\":\"sphere\"")]
        [TestCase(ParticleSystemShapeType.Hemisphere, "\"type\":\"hemisphere\"")]
        [TestCase(ParticleSystemShapeType.Circle, "\"type\":\"circle\"")]
        [TestCase(ParticleSystemShapeType.Donut, "\"type\":\"donut\"")]
        [TestCase(ParticleSystemShapeType.Rectangle, "\"type\":\"rectangle\"")]
        public void SupportedShapes_ProduceReadyStockShape(ParticleSystemShapeType shapeType, string marker)
        {
            var prefabPath = CreatePrefab("Shape" + shapeType, system =>
            {
                var shape = system.shape;
                shape.shapeType = shapeType;
                if (shapeType == ParticleSystemShapeType.Sphere ||
                    shapeType == ParticleSystemShapeType.Hemisphere)
                {
                    shape.radiusThickness = 0;
                }
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains(marker, json);
        }

        [Test]
        public void MeshVertexShape_UsesEqualAreaVertexProxyTriangles()
        {
            var mesh = CreateShapeMesh("MeshVertexShape", false);
            var prefabPath = CreatePrefab("MeshVertexShape", system =>
            {
                var shape = system.shape;
                shape.shapeType = ParticleSystemShapeType.Mesh;
                shape.mesh = mesh;
                shape.meshShapeType = ParticleSystemMeshShapeType.Vertex;
                shape.useMeshColors = false;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"type\":\"mesh_surface\"", json);
            StringAssert.Contains("\"array\":[0,1,2,3,4,5,6,7,8]", json);
            StringAssert.Contains("shape.meshVertex", report);
            StringAssert.Contains("equal-area microscopic triangles", report);
            StringAssert.DoesNotContain("shape.meshSurface", report);
        }

        [Test]
        public void MeshTriangleShape_RemainsDirectSurfaceMapping()
        {
            var mesh = CreateShapeMesh("MeshTriangleShape", false);
            var prefabPath = CreatePrefab("MeshTriangleShape", system =>
            {
                var shape = system.shape;
                shape.shapeType = ParticleSystemShapeType.Mesh;
                shape.mesh = mesh;
                shape.meshShapeType = ParticleSystemMeshShapeType.Triangle;
                shape.useMeshColors = false;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("shape.meshSurface", report);
            StringAssert.DoesNotContain("shape.meshVertex", report);
        }

        [Test]
        public void MeshEdgeShape_IsExplicitStrictFailure()
        {
            var mesh = CreateShapeMesh("MeshEdgeShape", false);
            var prefabPath = CreatePrefab("MeshEdgeShape", system =>
            {
                var shape = system.shape;
                shape.shapeType = ParticleSystemShapeType.Mesh;
                shape.mesh = mesh;
                shape.meshShapeType = ParticleSystemMeshShapeType.Edge;
                shape.useMeshColors = false;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("failed"));
            Assert.That(File.Exists(Path.Combine(outputRoot, "fixture", "effect.quarks.json")), Is.False);
            StringAssert.Contains("shape.meshEdge", report);
            StringAssert.Contains("best-effort output uses mesh-surface sampling", report);
        }

        [Test]
        public void ActiveMeshShapeColors_AreExplicitStrictFailure()
        {
            var mesh = CreateShapeMesh("MeshColorShape", true);
            var prefabPath = CreatePrefab("MeshColorShape", system =>
            {
                var shape = system.shape;
                shape.shapeType = ParticleSystemShapeType.Mesh;
                shape.mesh = mesh;
                shape.meshShapeType = ParticleSystemMeshShapeType.Vertex;
                shape.useMeshColors = true;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("failed"));
            Assert.That(File.Exists(Path.Combine(outputRoot, "fixture", "effect.quarks.json")), Is.False);
            StringAssert.Contains("shape.meshColors", report);
            StringAssert.Contains("sampled vertex color", report);
        }

        [Test]
        public void ActiveUnsupportedModule_FailsStrictAndPublishesNoJson()
        {
            var prefabPath = CreatePrefab("Collision", system =>
            {
                var collision = system.collision;
                collision.enabled = true;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);
            var effect = manifest.effects.Single();
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(effect.status, Is.EqualTo("failed"));
            Assert.That(effect.effectJson, Is.Empty);
            Assert.That(File.Exists(Path.Combine(outputRoot, "fixture", "effect.quarks.json")), Is.False);
            StringAssert.Contains("collision", report);
        }

        [Test]
        public void PhysicsCollisionModule_IsAbandonedInBestEffort()
        {
            var prefabPath = CreatePrefab("BestEffortCollisionAbandoned", system =>
            {
                var collision = system.collision;
                collision.enabled = true;
            });
            WriteConfig(prefabPath, "best-effort");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("failed"));
            Assert.That(manifest.effects.Single().effectJson, Is.Empty);
            Assert.That(File.Exists(Path.Combine(outputRoot, "fixture", "effect.quarks.json")), Is.False);
            StringAssert.Contains("Automatic Unity VFX conversion abandoned", report);
            StringAssert.Contains("\"fatalUnsupported\"", report);
            StringAssert.Contains("collision", report);
            StringAssert.Contains("target-runtime simulation", report);
        }

        [Test]
        public void PhysicsTriggerModule_IsAbandonedInBestEffort()
        {
            var prefabPath = CreatePrefab("BestEffortTriggerAbandoned", system =>
            {
                var trigger = system.trigger;
                trigger.enabled = true;
            });
            WriteConfig(prefabPath, "best-effort");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("failed"));
            Assert.That(File.Exists(Path.Combine(outputRoot, "fixture", "effect.quarks.json")), Is.False);
            StringAssert.Contains("\"fatalUnsupported\"", report);
            StringAssert.Contains("trigger", report);
            StringAssert.Contains("ParticleSystem physics collision or trigger", report);
        }

        [Test]
        public void CollisionSubEmitter_IsAbandonedInBestEffort()
        {
            var root = new GameObject("CollisionSubEmitter");
            var parent = AddParticleSystem(root);
            var childObject = new GameObject("Child");
            childObject.transform.SetParent(root.transform, false);
            var child = AddParticleSystem(childObject);
            var subEmitters = parent.subEmitters;
            subEmitters.enabled = true;
            subEmitters.AddSubEmitter(child, ParticleSystemSubEmitterType.Collision, ParticleSystemSubEmitterProperties.InheritNothing);
            var prefabPath = FixtureRoot + "/CollisionSubEmitter.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            WriteConfig(prefabPath, "best-effort");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("failed"));
            Assert.That(File.Exists(Path.Combine(outputRoot, "fixture", "effect.quarks.json")), Is.False);
            StringAssert.Contains("\"fatalUnsupported\"", report);
            StringAssert.Contains("subEmitters.Collision", report);
            StringAssert.Contains("collision-free VFX variant", report);
        }

        [Test]
        public void PresentationCollision_IsOmittedAndPublishedAsPartial()
        {
            var prefabPath = CreatePrefab("PresentationCollision", system =>
            {
                var collision = system.collision;
                collision.enabled = true;
            });
            WriteConfig(prefabPath, "strict", "presentation");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var effect = manifest.effects.Single();
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(effect.status, Is.EqualTo("partial"));
            Assert.That(effect.target, Is.EqualTo("presentation"));
            Assert.That(File.Exists(Path.Combine(outputRoot, "fixture", "effect.quarks.json")), Is.True);
            StringAssert.Contains("\"target\": \"presentation\"", report);
            StringAssert.Contains("collision", report);
            StringAssert.Contains("omitted for the presentation target", report);
            StringAssert.Contains("\"fatalUnsupported\": []", report);
            StringAssert.Contains("\"ParticleEmitter\"", json);
        }

        [Test]
        public void PresentationTrigger_IsOmittedAndPublishedAsPartial()
        {
            var prefabPath = CreatePrefab("PresentationTrigger", system =>
            {
                var trigger = system.trigger;
                trigger.enabled = true;
            });
            WriteConfig(prefabPath, "strict", "presentation");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("partial"));
            Assert.That(File.Exists(Path.Combine(outputRoot, "fixture", "effect.quarks.json")), Is.True);
            StringAssert.Contains("trigger", report);
            StringAssert.Contains("omitted for the presentation target", report);
            StringAssert.Contains("\"fatalUnsupported\": []", report);
        }

        [Test]
        public void PresentationCollisionSubEmitter_IsRemovedAndPublishedAsPartial()
        {
            var root = new GameObject("PresentationCollisionSubEmitter");
            var parent = AddParticleSystem(root);
            var childObject = new GameObject("Child");
            childObject.transform.SetParent(root.transform, false);
            var child = AddParticleSystem(childObject);
            var subEmitters = parent.subEmitters;
            subEmitters.enabled = true;
            subEmitters.AddSubEmitter(child, ParticleSystemSubEmitterType.Collision, ParticleSystemSubEmitterProperties.InheritNothing);
            var prefabPath = FixtureRoot + "/PresentationCollisionSubEmitter.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            WriteConfig(prefabPath, "strict", "presentation");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("partial"));
            Assert.That(File.Exists(Path.Combine(outputRoot, "fixture", "effect.quarks.json")), Is.True);
            StringAssert.Contains("subEmitters.Collision", report);
            StringAssert.Contains("omitted for the presentation target", report);
            StringAssert.Contains("\"fatalUnsupported\": []", report);
            StringAssert.DoesNotContain("\"type\":\"EmitSubParticleSystem\"", json);
        }

        [Test]
        public void EnabledZeroNoise_DoesNotFailStrict()
        {
            var prefabPath = CreatePrefab("ZeroNoise", system =>
            {
                var noise = system.noise;
                noise.enabled = true;
                noise.strength = 0;
                noise.separateAxes = false;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("noise", report);
            StringAssert.Contains("inactive", report);
        }

        [Test]
        public void EnabledZeroVelocityOverLifetime_IsInactiveInStrictMode()
        {
            var prefabPath = CreatePrefab("ZeroVelocity", system =>
            {
                var velocity = system.velocityOverLifetime;
                velocity.enabled = true;
                velocity.x = 0;
                velocity.y = 0;
                velocity.z = 0;
                velocity.orbitalX = 0;
                velocity.orbitalY = 0;
                velocity.orbitalZ = 0;
                velocity.orbitalOffsetX = 0;
                velocity.orbitalOffsetY = 0;
                velocity.orbitalOffsetZ = 0;
                velocity.radial = 0;
                velocity.speedModifier = 1;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("velocityOverLifetime", report);
            StringAssert.Contains("inactive", report);
        }

        [Test]
        public void LinearVelocityOverLifetime_PreservesShortCurveDomainInRuntimeMetadata()
        {
            var prefabPath = CreatePrefab("ActiveVelocity", system =>
            {
                var velocity = system.velocityOverLifetime;
                velocity.enabled = true;
                velocity.space = ParticleSystemSimulationSpace.Local;
                velocity.x = new ParticleSystem.MinMaxCurve(
                    5,
                    AnimationCurve.Linear(0, 1, 0.02f, 0));
                velocity.y = new ParticleSystem.MinMaxCurve(-2, 2);
                velocity.z = new ParticleSystem.MinMaxCurve(
                    3,
                    AnimationCurve.Linear(0, -1, 0.1f, 0),
                    AnimationCurve.Linear(0, 1, 0.1f, 0));
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("unity_particle_quarks_exporter.velocity_over_lifetime.v2", json);
            StringAssert.Contains("\"mode\":\"twoConstants\"", json);
            StringAssert.Contains("\"mode\":\"twoCurves\"", json);
            StringAssert.Contains("\"start\":0.02", json);
            StringAssert.Contains("\"p0\":0,\"p1\":0,\"p2\":0,\"p3\":0},\"start\":0.02", json);
            StringAssert.Contains("velocityOverLifetime.linear", report);
            StringAssert.Contains("paired SDK runtime", report);
        }

        [Test]
        public void OrbitalVelocityOverLifetime_UsesUnityRuntimeMetadata()
        {
            var prefabPath = CreatePrefab("OrbitalVelocity", system =>
            {
                var velocity = system.velocityOverLifetime;
                velocity.enabled = true;
                velocity.orbitalY = 1;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("velocityOverLifetime.orbital.runtime", report);
            StringAssert.Contains("\"orbitalY\":", File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json")));
        }

        [Test]
        public void ScalarLimitVelocity_UsesUnityDragRuntimeMetadata()
        {
            var prefabPath = CreatePrefab("LimitWithDrag", system =>
            {
                var limit = system.limitVelocityOverLifetime;
                limit.enabled = true;
                limit.separateAxes = false;
                limit.limit = new ParticleSystem.MinMaxCurve(1);
                limit.dampen = 0.1f;
                limit.drag = new ParticleSystem.MinMaxCurve(0.5f);
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("LimitSpeedOverLife", json);
            StringAssert.Contains("limitVelocityOverLifetime.scalar", report);
            StringAssert.Contains("limitVelocityOverLifetime.drag.runtime", report);
            StringAssert.Contains("Unity's area and velocity-dependent drag formula", report);
        }

        [Test]
        public void ZeroDragWithMultipliers_IsInactive()
        {
            var prefabPath = CreatePrefab("ZeroDrag", system =>
            {
                var limit = system.limitVelocityOverLifetime;
                limit.enabled = true;
                limit.limit = 10;
                limit.drag = 0;
                limit.multiplyDragByParticleSize = true;
                limit.multiplyDragByParticleVelocity = true;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("limitVelocityOverLifetime.drag", report);
            StringAssert.Contains("\"inactive\"", report);
            StringAssert.DoesNotContain("Size and velocity drag multipliers", report);
        }

        [Test]
        public void LimitVelocityTwoCurves_UsesRuntimeMetadataAndStrictStockFallback()
        {
            var prefabPath = CreatePrefab("LimitTwoCurves", system =>
            {
                var limit = system.limitVelocityOverLifetime;
                limit.enabled = true;
                limit.separateAxes = false;
                limit.limit = new ParticleSystem.MinMaxCurve(
                    2,
                    AnimationCurve.Linear(0, 1, 1, 2),
                    AnimationCurve.Linear(0, 3, 1, 4));
                limit.dampen = 0.5f;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"type\":\"LimitSpeedOverLife\"", json);
            StringAssert.Contains("unity_particle_quarks_exporter.limit_velocity_over_lifetime.v3", json);
            StringAssert.Contains("\"mode\":\"twoCurves\"", json);
            StringAssert.Contains("limitVelocityOverLifetime.limit.runtime", report);
            StringAssert.Contains("limitVelocityOverLifetime.limit.twoCurves.stockMeanFallback", report);
        }

        [Test]
        public void LimitVelocitySeparateAxes_UsesPairedRuntimeMetadata()
        {
            var prefabPath = CreatePrefab("LimitSeparateAxes", system =>
            {
                var limit = system.limitVelocityOverLifetime;
                limit.enabled = true;
                limit.separateAxes = true;
                limit.limitX = new ParticleSystem.MinMaxCurve(2);
                limit.limitY = new ParticleSystem.MinMaxCurve(3);
                limit.limitZ = new ParticleSystem.MinMaxCurve(4);
                limit.dampen = 1f;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("unity_particle_quarks_exporter.limit_velocity_over_lifetime.v3", json);
            StringAssert.Contains("\"separateAxes\":true", json);
            StringAssert.Contains("\"limitX\":", json);
            StringAssert.Contains("limitVelocityOverLifetime.separateAxes.runtime", report);
            StringAssert.Contains("limitVelocityOverLifetime.separateAxes.stockOmittedFallback", report);
            StringAssert.DoesNotContain("limitVelocityOverLifetime.separateAxes\"", report);
        }

        [Test]
        public void InheritVelocityInitial_UsesRuntimeMetadataInStrictMode()
        {
            var prefabPath = CreatePrefab("InheritInitial", system =>
            {
                var inherit = system.inheritVelocity;
                inherit.enabled = true;
                inherit.mode = ParticleSystemInheritVelocityMode.Initial;
                inherit.curve = new ParticleSystem.MinMaxCurve(2);
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("unity_particle_quarks_exporter.inherit_velocity.v2", json);
            StringAssert.Contains("inheritVelocity.initial.runtime", report);
            StringAssert.Contains("inheritVelocity.initial.stockOmittedFallback", report);
        }

        [Test]
        public void InheritVelocityCurrent_UsesPairedRuntimeMetadataInStrictMode()
        {
            var prefabPath = CreatePrefab("InheritCurrent", system =>
            {
                var inherit = system.inheritVelocity;
                inherit.enabled = true;
                inherit.mode = ParticleSystemInheritVelocityMode.Current;
                inherit.curve = new ParticleSystem.MinMaxCurve(1);
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("unity_particle_quarks_exporter.inherit_velocity.v2", json);
            StringAssert.Contains("\"mode\":\"current\"", json);
            StringAssert.Contains("inheritVelocity.current.runtime", report);
            StringAssert.Contains("inheritVelocity.current.stockOmittedFallback", report);
        }

        [Test]
        public void LifetimeByEmitterSpeed_UsesParticleSystemModuleMetadataInStrictMode()
        {
            var prefabPath = CreatePrefab("LifetimeByEmitterSpeed", system =>
            {
                var lifetime = system.lifetimeByEmitterSpeed;
                lifetime.enabled = true;
                lifetime.range = new Vector2(0, 8);
                lifetime.curve = new ParticleSystem.MinMaxCurve(
                    1,
                    AnimationCurve.Linear(0, 0.5f, 1, 2));
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("unity_particle_quarks_exporter.lifetime_by_emitter_speed.v1", json);
            StringAssert.Contains("\"range\":[0,8]", json);
            StringAssert.Contains("main.lifetimeByEmitterSpeed.runtime", report);
            StringAssert.Contains("main.lifetimeByEmitterSpeed.stockUnscaledFallback", report);
        }

        [Test]
        public void InheritVelocityWithZeroCurve_IsInactiveRegardlessOfMode()
        {
            var prefabPath = CreatePrefab("InheritZero", system =>
            {
                var inherit = system.inheritVelocity;
                inherit.enabled = true;
                inherit.mode = ParticleSystemInheritVelocityMode.Current;
                inherit.curve = new ParticleSystem.MinMaxCurve(0);
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"inactive\"", report);
            StringAssert.Contains("\"inheritVelocity\"", report);
            StringAssert.DoesNotContain("inheritVelocity.Current", report);
        }

        [Test]
        public void WorldSpaceBillboardShapeRotation_IsMappedIntoBirthTransform()
        {
            var prefabPath = CreatePrefab("WorldShapeRotation", system =>
            {
                var main = system.main;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                var shape = system.shape;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.rotation = new Vector3(-90, 0, 0);
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("shape.transform.rotation", report);
            StringAssert.DoesNotContain("\"matrix\":[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1],\"ps\"", json);
        }

        [Test]
        public void WorldSpaceSphere_UsesPositiveDeterminantEmitterBasisForRadialVelocity()
        {
            var prefabPath = CreatePrefab("WorldSphere", system =>
            {
                var main = system.main;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                var shape = system.shape;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 2.2f;
                shape.radiusThickness = 0;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"matrix\":[-1,0,0,0,0,1,0,0,0,0,-1,0,0,0,0,1]", json);
            StringAssert.Contains("\"radius\":2.2", json);
            StringAssert.Contains("\"thickness\":0", json);
            StringAssert.DoesNotContain("worldSpaceHandednessBake", report);
        }

        [Test]
        public void ShapeScalingMode_SeparatesBirthPositionScaleFromDirectionAndSpeed()
        {
            var prefabPath = CreatePrefab("ShapeScaling", system =>
            {
                system.transform.localScale = Vector3.one * 0.1f;
                var main = system.main;
                main.scalingMode = ParticleSystemScalingMode.Shape;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.startSpeed = 3;
                main.startSize = 4;
                var shape = system.shape;
                shape.shapeType = ParticleSystemShapeType.Hemisphere;
                shape.radius = 2;
                shape.radiusThickness = 0;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"radius\":2", json);
            StringAssert.Contains("\"startSpeed\":{\"type\":\"ConstantValue\",\"value\":3}", json);
            StringAssert.Contains("\"startSize\":{\"type\":\"ConstantValue\",\"value\":4}", json);
            StringAssert.Contains("\"matrix\":[-1,0,0,0,0,1,0,0,0,0,-1,0,0,0,0,1]", json);
            StringAssert.Contains("\"birthPositionTransform\":[0.1,0,0,0,0,0.1,0,0,0,0,0.1", json);
            StringAssert.Contains("\"birthDirectionTransform\":[1,0,0,0,0,1,0,0,0,0,1", json);
            StringAssert.Contains("main.scalingMode.shape", report);
            StringAssert.Contains("main.scalingMode.shape.positionRuntime", report);
            StringAssert.Contains("stockUnitShapeFallback", report);
        }

        [Test]
        public void ShapeModuleScale_UsesRuntimeBirthPositionAndNormalBasis()
        {
            var prefabPath = CreatePrefab("ShapeModuleScale", system =>
            {
                var main = system.main;
                main.startSpeed = 3;
                var shape = system.shape;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.radius = 2;
                shape.angle = 0;
                shape.scale = new Vector3(2, 3, 4);
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"radius\":2", json);
            StringAssert.Contains("\"unsupported\": []", report);
            StringAssert.Contains("shape.transform.scale.runtime", report);
            StringAssert.Contains("shape.transform.scale.stockUnitShapeFallback", report);
            var position = JsonMatrix(json, "birthPositionTransform");
            var direction = JsonMatrix(json, "birthDirectionTransform");
            Assert.That(position[0], Is.EqualTo(2).Within(0.000001f));
            Assert.That(position[5], Is.EqualTo(3).Within(0.000001f));
            Assert.That(position[10], Is.EqualTo(4).Within(0.000001f));
            Assert.That(direction[0], Is.EqualTo(0.5f).Within(0.000001f));
            Assert.That(direction[5], Is.EqualTo(1f / 3f).Within(0.000001f));
            Assert.That(direction[10], Is.EqualTo(0.25f).Within(0.000001f));
        }

        [Test]
        public void PrefabRootPlacement_IsNormalizedWhileRootScaleIsPreserved()
        {
            var prefabPath = CreatePrefab("RootRotation", system =>
            {
                system.transform.localPosition = new Vector3(120, 5, -23);
                system.transform.localRotation = Quaternion.Euler(-90, 0, 0);
                system.transform.localScale = new Vector3(2, 3, 4);
                var shape = system.shape;
                shape.shapeType = ParticleSystemShapeType.Cone;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            var matrix = EmitterMatrix(json);
            Assert.That(matrix[0], Is.EqualTo(-2).Within(0.00001f));
            Assert.That(matrix[5], Is.EqualTo(3).Within(0.00001f));
            Assert.That(matrix[10], Is.EqualTo(-4).Within(0.00001f));
            Assert.That(matrix[12], Is.EqualTo(0).Within(0.00001f));
            Assert.That(matrix[13], Is.EqualTo(0).Within(0.00001f));
            Assert.That(matrix[14], Is.EqualTo(0).Within(0.00001f));
            StringAssert.Contains("\"matrix\":[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1],\"children\"", json);
            StringAssert.Contains("prefabRoot.poseNormalized", report);
        }

        [Test]
        public void MaxParticles_IsExportedAsPairedRuntimeCapacity()
        {
            var prefabPath = CreatePrefab("MaxParticles", system =>
            {
                var main = system.main;
                main.maxParticles = 37;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("unity_particle_quarks_exporter.particle_capacity.v1", json);
            StringAssert.Contains("\"maxParticles\":37", json);
            StringAssert.Contains("main.maxParticles.runtimeCapacity", report);
            StringAssert.Contains("main.maxParticles.stockUnboundedFallback", report);
        }

        [Test]
        public void RadialVolumeDistribution_UsesRuntimeUniformVolumeMetadata()
        {
            var prefabPath = CreatePrefab("SphereVolume", system =>
            {
                var shape = system.shape;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radiusThickness = 1;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("unity_particle_quarks_exporter.shape_semantics.v1", json);
            StringAssert.Contains("\"type\":\"sphereVolume\"", json);
            StringAssert.Contains("shape.sphere.uniformVolumeRuntime", report);
            StringAssert.Contains("shape.sphere.linearRadiusStockFallback", report);
            StringAssert.Contains("corrected to a uniform-volume radius", report);
        }

        [Test]
        public void MissingMaterial_BestEffortOmitsVisibleSystemInsteadOfWhiteFallback()
        {
            var root = new GameObject("MissingMaterialPair");
            AddParticleSystem(root);
            var child = new GameObject("MissingMaterialChild");
            child.transform.SetParent(root.transform, false);
            AddParticleSystem(child);
            child.GetComponent<ParticleSystemRenderer>().sharedMaterial = null;
            var prefabPath = FixtureRoot + "/MissingMaterialPair.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            WriteConfig(prefabPath, "best-effort");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("partial"));
            Assert.That(Occurrences(json, "\"type\":\"ParticleEmitter\""), Is.EqualTo(1));
            StringAssert.Contains("renderer.material", report);
            StringAssert.Contains("omits this visible ParticleSystem", report);
        }

        [Test]
        public void MissingMaterialOnlyEffect_FailsCleanlyWithoutPublishingJson()
        {
            var prefabPath = CreatePrefab("MissingMaterialOnly", system =>
                system.GetComponent<ParticleSystemRenderer>().sharedMaterial = null);
            WriteConfig(prefabPath, "best-effort");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("failed"));
            Assert.That(File.Exists(Path.Combine(outputRoot, "fixture", "effect.quarks.json")), Is.False);
            StringAssert.Contains("renderer.material", report);
            StringAssert.Contains("No exportable ParticleSystem remains", report);
        }

        [Test]
        public void LocalSpaceShapeRotation_MapsBirthDirectionAndKeepsWorldGravity()
        {
            var prefabPath = CreatePrefab("LocalShapeRotation", system =>
            {
                var main = system.main;
                main.gravityModifier = 3;
                var shape = system.shape;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.rotation = new Vector3(-90, 0, 0);
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("shape.transform.rotation", report);
            StringAssert.Contains("unity_particle_quarks_exporter.gravity.v1", json);
            StringAssert.Contains("\"acceleration\":[0,-9.81,0]", json);
            StringAssert.Contains("\"birthPositionTransform\"", json);
            StringAssert.Contains("\"birthDirectionTransform\"", json);
            StringAssert.DoesNotContain("\"type\":\"ForceOverLife\"", json);
        }

        [Test]
        public void DampedScalarNoise_MapsFrequencyAdjustedPower()
        {
            var prefabPath = CreatePrefab("ActiveNoise", system =>
            {
                var noise = system.noise;
                noise.enabled = true;
                noise.strength = new ParticleSystem.MinMaxCurve(5, 20);
                noise.frequency = 5;
                noise.damping = true;
                noise.scrollSpeed = 0;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"type\":\"Noise\"", json);
            StringAssert.Contains("\"power\":{\"type\":\"IntervalValue\",\"a\":1,\"b\":4}", json);
            StringAssert.Contains("unity_particle_quarks_exporter.noise.v1", json);
            StringAssert.Contains("\"qualityDimensions\":2", json);
            StringAssert.Contains("\"remapEnabled\":false", json);
            StringAssert.DoesNotContain("\"remapX\"", json);
            StringAssert.Contains("noise.dampedPower", report);
            StringAssert.Contains("noise.spatialCurl.runtime", report);
        }

        [Test]
        public void NoiseScrollSpeed_IsExplicitButDoesNotBlockStrict()
        {
            var prefabPath = CreatePrefab("ScrollingNoise", system =>
            {
                var noise = system.noise;
                noise.enabled = true;
                noise.strength = 4;
                noise.frequency = 2;
                noise.damping = true;
                noise.scrollSpeed = 3;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"type\":\"Noise\"", json);
            StringAssert.Contains("\"power\":{\"type\":\"ConstantValue\",\"value\":2}", json);
            StringAssert.Contains("unity_particle_quarks_exporter.noise.v1", json);
            StringAssert.Contains("noise.scrollSpeed.runtime", report);
            StringAssert.Contains("noise.scrollSpeed.omittedFallback", report);
            StringAssert.DoesNotContain("\"nonBlockingUnsupported\":[\"noise.scrollSpeed\"]", report);
            Assert.That(File.Exists(Path.Combine(outputRoot, "fixture", "effect.quarks.json")), Is.True);
        }

        [Test]
        public void SeparateAxisNoise_UsesPairedSpatialCurlRuntime()
        {
            var prefabPath = CreatePrefab("SeparateAxisNoise", system =>
            {
                var noise = system.noise;
                noise.enabled = true;
                noise.separateAxes = true;
                noise.strengthX = 0.4f;
                noise.strengthY = 0;
                noise.strengthZ = 0.4f;
                noise.frequency = 0.5f;
                noise.damping = true;
                noise.quality = ParticleSystemNoiseQuality.High;
                noise.octaveCount = 1;
                noise.scrollSpeed = 0.1f;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("unity_particle_quarks_exporter.noise.v1", json);
            StringAssert.Contains("\"separateAxes\":true", json);
            StringAssert.Contains("\"strengthY\":{\"mode\":\"constant\",\"value\":{\"type\":\"ConstantValue\",\"value\":0}}", json);
            StringAssert.Contains("noise.separateAxes.runtime", report);
            StringAssert.Contains("noise.scrollSpeed.runtime", report);
            StringAssert.Contains("\"unsupported\": [],", report);
        }

        [Test]
        public void SimulationSpeed_UsesPairedRuntimeMetadata()
        {
            var prefabPath = CreatePrefab("SimulationSpeed", system =>
            {
                var main = system.main;
                main.simulationSpeed = 3;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("unity_particle_quarks_exporter.simulation_speed.v1", json);
            StringAssert.Contains("\"value\":3", json);
            StringAssert.Contains("main.simulationSpeed.runtime", report);
            StringAssert.Contains("main.simulationSpeed.stockUnitSpeedFallback", report);
        }

        [Test]
        public void MeshVelocityAlignment_UsesPairedRuntimeMetadataInLocalSpace()
        {
            var prefabPath = CreatePrefab("MeshVelocityAlignment", system =>
            {
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Mesh;
                renderer.alignment = ParticleSystemRenderSpace.Velocity;
                var main = system.main;
                main.simulationSpace = ParticleSystemSimulationSpace.Local;
                main.startRotation3D = true;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("unity_particle_quarks_exporter.mesh_velocity_alignment.v1", json);
            StringAssert.Contains("\"forwardAxis\":[0,0,1]", json);
            StringAssert.Contains("renderer.mesh.alignment.velocity.runtime", report);
            StringAssert.Contains("renderer.mesh.alignment.velocity.stockUnalignedFallback", report);
        }

        [TestCase(ParticleSystemRenderSpace.View, "view")]
        [TestCase(ParticleSystemRenderSpace.Facing, "facing")]
        public void MeshCameraAlignment_UsesPairedRuntimeMetadataInLocalSpace(
            ParticleSystemRenderSpace alignment,
            string expectedMode)
        {
            var prefabPath = CreatePrefab("MeshCameraAlignment" + expectedMode, system =>
            {
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Mesh;
                renderer.alignment = alignment;
                var main = system.main;
                main.simulationSpace = ParticleSystemSimulationSpace.Local;
                main.startRotation3D = true;
                main.startRotationX = 0.35f;
                main.startRotationY = 0.6f;
                main.startRotationZ = 0.8f;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("unity_particle_quarks_exporter.mesh_camera_alignment.v1", json);
            StringAssert.Contains("\"mode\":\"" + expectedMode + "\"", json);
            StringAssert.Contains("\"forwardAxis\":[0,0,1]", json);
            StringAssert.Contains("\"upAxis\":[0,1,0]", json);
            StringAssert.Contains("\"eulerOrder\":\"YXZ\"", json);
            StringAssert.Contains("renderer.mesh.alignment." + expectedMode + ".runtime", report);
            StringAssert.Contains("renderer.mesh.alignment.cameraFacing.stockUnalignedFallback", report);
            StringAssert.Contains("\"unsupported\": []", report);
        }

        [Test]
        public void RendererPivot_UsesPairedRuntimeMetadata()
        {
            var prefabPath = CreatePrefab("RendererPivot", system =>
            {
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                renderer.pivot = new Vector3(0.1f, -0.48f, 0.2f);
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("unity_particle_quarks_exporter.renderer_pivot.v1", json);
            StringAssert.Contains("\"sourceRenderMode\":\"Billboard\"", json);
            StringAssert.Contains("\"value\":[0.1,-0.48,0.2]", json);
            StringAssert.Contains("\"geometryOffset\":[0.1,-0.48,-0.2]", json);
            StringAssert.Contains("renderer.pivot.runtime", report);
            StringAssert.Contains("renderer.pivot.stockCenteredFallback", report);
            StringAssert.Contains("\"unsupported\": []", report);
        }

        [Test]
        public void MeshCameraAlignment_WorldSimulationSpaceIsStrictFailureAndBestEffortFallback()
        {
            var prefabPath = CreatePrefab("MeshCameraAlignmentWorld", system =>
            {
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Mesh;
                renderer.alignment = ParticleSystemRenderSpace.Facing;
                var main = system.main;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
            });

            WriteConfig(prefabPath, "strict");
            var strictManifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);
            Assert.That(strictManifest.effects.Single().status, Is.EqualTo("failed"));
            Assert.That(File.Exists(Path.Combine(outputRoot, "fixture", "effect.quarks.json")), Is.False);

            WriteConfig(prefabPath, "best-effort");
            var bestEffortManifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));
            Assert.That(bestEffortManifest.effects.Single().status, Is.EqualTo("partial"));
            StringAssert.Contains("renderer.mesh.alignment.cameraFacingSimulationSpace", report);
            StringAssert.Contains("cameraFacingSimulationSpace.unalignedFallback", report);
            StringAssert.DoesNotContain("unity_particle_quarks_exporter.mesh_camera_alignment.v1", File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json")));
        }

        [Test]
        public void MeshScalarRotationModules_UseQuaternionBehaviors()
        {
            var prefabPath = CreatePrefab("MeshRotations", system =>
            {
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Mesh;
                renderer.alignment = ParticleSystemRenderSpace.Local;
                var main = system.main;
                main.startRotation3D = false;
                main.startRotation = 1;
                var rotation = system.rotationOverLifetime;
                rotation.enabled = true;
                rotation.separateAxes = false;
                rotation.z = 1.3962634f;
                var bySpeed = system.rotationBySpeed;
                bySpeed.enabled = true;
                bySpeed.separateAxes = false;
                bySpeed.z = new ParticleSystem.MinMaxCurve(-0.34906584f, 0.34906584f);
                bySpeed.range = new Vector2(0, 50);
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"startRotation\":{\"type\":\"Euler\"", json);
            Assert.That(Occurrences(json, "\"type\":\"Rotation3DOverLife\""), Is.EqualTo(2));
            Assert.That(Occurrences(json, "\"angleY\":{\"type\":\"ConstantValue\",\"value\":0}"), Is.GreaterThanOrEqualTo(3));
            StringAssert.Contains("\"angleZ\":{\"type\":\"ConstantValue\",\"value\":-1}", json);
            StringAssert.Contains("\"angleZ\":{\"type\":\"ConstantValue\",\"value\":-1.39626336}", json);
            StringAssert.DoesNotContain("\"type\":\"RotationOverLife\"", json);
            StringAssert.DoesNotContain("\"type\":\"RotationBySpeed\"", json);
            StringAssert.Contains("unity_particle_quarks_exporter.mesh_scalar_rotation.v2", json);
            StringAssert.Contains("rotation.meshScalarAxis", report);
            StringAssert.Contains("stockZFallback", report);
            StringAssert.Contains("rotationOverLifetime", report);
            StringAssert.Contains("rotationBySpeed.constantMesh", report);
        }

        [TestCase(ParticleSystemShapeType.Rectangle, 0f, 0f, "fixed")]
        [TestCase(ParticleSystemShapeType.Sphere, 0f, 0f, "position")]
        [TestCase(ParticleSystemShapeType.Sphere, 1f, 1f, "velocity")]
        [TestCase(ParticleSystemShapeType.Sphere, 1f, 0f, "uniformXY")]
        public void MeshScalarRotationAxis_UsesDeterministicUnityTechnicalClassification(
            ParticleSystemShapeType shapeType,
            float randomDirectionAmount,
            float startSpeed,
            string expectedMode)
        {
            var prefabPath = CreatePrefab("MeshAxis" + expectedMode, system =>
            {
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Mesh;
                renderer.alignment = ParticleSystemRenderSpace.Local;
                var main = system.main;
                main.startRotation3D = false;
                main.startRotation = 1;
                main.startSpeed = startSpeed;
                var shape = system.shape;
                shape.shapeType = shapeType;
                shape.radiusThickness = 0;
                shape.randomDirectionAmount = randomDirectionAmount;
            });
            var randomDirectionIsActive = randomDirectionAmount > 0.000001f && startSpeed > 0.000001f;
            WriteConfig(prefabPath, "strict");

            var first = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var firstJson = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var second = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var secondJson = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(first.effects.Single().status, Is.EqualTo("ready"));
            Assert.That(second.effects.Single().status, Is.EqualTo("ready"));
            Assert.That(secondJson, Is.EqualTo(firstJson));
            StringAssert.Contains("\"axisMode\":\"" + expectedMode + "\"", firstJson);
            if (expectedMode == "fixed") StringAssert.Contains("\"axis\":", firstJson);
            StringAssert.Contains("rotation.meshScalarAxis." + expectedMode + "Runtime", report);
            if (expectedMode == "uniformXY")
            {
                StringAssert.Contains("shape.randomDirectionAmount.meshRotationAxisRuntime", report);
                StringAssert.DoesNotContain("shape.randomDirectionAmount.zeroStartSpeed", report);
            }
            if (randomDirectionIsActive)
            {
                StringAssert.Contains("\"randomDirection\":{\"mode\":\"lerpRandomUnit\",\"amount\":1}", firstJson);
                StringAssert.Contains("shape.randomDirectionAmount.runtime", report);
                StringAssert.Contains("shape.randomDirectionAmount.stockShapeDirectionFallback", report);
            }
        }

        [Test]
        public void MeshScalarAndSeparateAxisRotation_RemainsExplicitStrictFailure()
        {
            var prefabPath = CreatePrefab("MeshMixedRotations", system =>
            {
                system.GetComponent<ParticleSystemRenderer>().renderMode = ParticleSystemRenderMode.Mesh;
                var main = system.main;
                main.startRotation3D = false;
                main.startRotation = 1;
                var rotation = system.rotationOverLifetime;
                rotation.enabled = true;
                rotation.separateAxes = true;
                rotation.x = 0.5f;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("failed"));
            Assert.That(File.Exists(Path.Combine(outputRoot, "fixture", "effect.quarks.json")), Is.False);
            StringAssert.Contains("rotation.meshScalarAxis.mixed3D", report);
            StringAssert.Contains("not been black-box matched", report);
        }

        [Test]
        public void PresentationMeshScalarAndSeparateAxisRotation_IsPublishedAsPartial()
        {
            var prefabPath = CreatePrefab("PresentationMeshMixedRotations", system =>
            {
                system.GetComponent<ParticleSystemRenderer>().renderMode = ParticleSystemRenderMode.Mesh;
                var main = system.main;
                main.startRotation3D = false;
                main.startRotation = 1;
                var rotation = system.rotationOverLifetime;
                rotation.enabled = true;
                rotation.separateAxes = true;
                rotation.x = 0.5f;
            });
            WriteConfig(prefabPath, "strict", "presentation");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("partial"));
            Assert.That(File.Exists(Path.Combine(outputRoot, "fixture", "effect.quarks.json")), Is.True);
            StringAssert.Contains("rotation.meshScalarAxis.mixed3D", report);
            StringAssert.Contains("stockZFallback", report);
            StringAssert.Contains("\"fatalUnsupported\": []", report);
        }

        [Test]
        public void MeshSpeedDependentRotation_UsesPairedMetadataWhenAxisIsClassifiable()
        {
            var prefabPath = CreatePrefab("MeshSpeedRotation", system =>
            {
                system.GetComponent<ParticleSystemRenderer>().renderMode = ParticleSystemRenderMode.Mesh;
                var bySpeed = system.rotationBySpeed;
                bySpeed.enabled = true;
                bySpeed.separateAxes = false;
                bySpeed.z = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, 0, 1, 1));
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("unity_particle_quarks_exporter.mesh_rotation_by_speed.v1", json);
            StringAssert.Contains("rotationBySpeed.meshSpeedDependent.runtime", report);
            StringAssert.Contains("rotationBySpeed.meshSpeedDependent.stockOmittedFallback", report);
        }

        [Test]
        public void WorldSpaceMeshCircleRotation_UsesRuntimeBirthTransformWithoutReplacingShape()
        {
            var prefabPath = CreatePrefab("MeshShapeRotation", system =>
            {
                var main = system.main;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                var shape = system.shape;
                shape.shapeType = ParticleSystemShapeType.Circle;
                shape.rotation = new Vector3(90, 0, 0);
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Mesh;
                renderer.alignment = ParticleSystemRenderSpace.Local;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"shape\":{\"type\":\"circle\"", json);
            StringAssert.Contains("\"birthPositionTransform\"", json);
            StringAssert.Contains("\"birthDirectionTransform\"", json);
            StringAssert.Contains("\"correctWorldSpaceBirthVelocity\":true", json);
            StringAssert.Contains("shape.transform.rotation", report);
            StringAssert.DoesNotContain("shape.circleMeshSurfaceBake", report);
        }

        [Test]
        public void ConstantWorldAxisVelocity_OnMeshCircle_UsesRuntimeMetadataWithoutReplacingStartSpeed()
        {
            var prefabPath = CreatePrefab("ConstantWorldVelocity", system =>
            {
                var main = system.main;
                main.startSpeed = 0.12f;
                var shape = system.shape;
                shape.shapeType = ParticleSystemShapeType.Circle;
                shape.rotation = new Vector3(90, 0, 0);
                shape.radiusThickness = 0;
                var velocity = system.velocityOverLifetime;
                velocity.enabled = true;
                velocity.space = ParticleSystemSimulationSpace.World;
                velocity.x = 0;
                velocity.y = 0.6f;
                velocity.z = 0;
                system.GetComponent<ParticleSystemRenderer>().renderMode = ParticleSystemRenderMode.Mesh;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"startSpeed\":{\"type\":\"ConstantValue\",\"value\":0.12}", json);
            StringAssert.Contains("unity_particle_quarks_exporter.velocity_over_lifetime.v2", json);
            StringAssert.Contains("velocityOverLifetime.linear", report);
            StringAssert.DoesNotContain("simultaneous Unity initial shape velocity is not preserved", report);
            StringAssert.Contains("\"shape\":{\"type\":\"circle\",\"radius\":1", json);
            StringAssert.Contains("\"thickness\":0", json);
            StringAssert.Contains("\"birthDirectionTransform\"", json);
        }

        [Test]
        public void FullRandomDirection_UsesUnitySourceRuntimeSemantics()
        {
            var prefabPath = CreatePrefab("FullRandomDirection", system =>
            {
                var shape = system.shape;
                shape.shapeType = ParticleSystemShapeType.Circle;
                shape.rotation = new Vector3(90, 0, 0);
                shape.randomDirectionAmount = 1;
                var velocity = system.velocityOverLifetime;
                velocity.enabled = true;
                velocity.space = ParticleSystemSimulationSpace.World;
                velocity.y = 0.6f;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("unity_particle_quarks_exporter.shape_semantics.v1", json);
            StringAssert.Contains("\"randomDirection\":{\"mode\":\"lerpRandomUnit\",\"amount\":1}", json);
            StringAssert.DoesNotContain("\"type\":\"ChangeEmitDirection\"", json);
            StringAssert.Contains("shape.randomDirectionAmount.randomUnitLerpRuntime", report);
            StringAssert.Contains("shape.randomDirectionAmount.stockShapeDirectionFallback", report);
            StringAssert.DoesNotContain("\"unsupported\":[\"shape.randomDirectionAmount\"]", report);
            StringAssert.Contains("velocityOverLifetime.linear", report);
        }

        [Test]
        public void ConeRandomDirection_UsesUnityConeSurfaceRuntimeSemantics()
        {
            var prefabPath = CreatePrefab("ConeRandomDirection", system =>
            {
                var shape = system.shape;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = 25;
                shape.radius = 2;
                shape.radiusThickness = 0.4f;
                shape.randomDirectionAmount = 0.5f;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"randomDirection\":{\"mode\":\"coneSurface\",\"amount\":0.5", json);
            StringAssert.Contains("\"angle\":0.436332", json);
            StringAssert.Contains("\"radius\":2", json);
            StringAssert.Contains("shape.randomDirectionAmount.coneSurfaceRuntime", report);
            StringAssert.Contains("shape.randomDirectionAmount.stockShapeDirectionFallback", report);
            StringAssert.Contains("\"unsupported\": []", report);
        }

        [Test]
        public void MissingMeshRenderer_IsOmittedInsteadOfBecomingBillboardGeometry()
        {
            var prefabPath = CreatePrefab("MissingRendererMesh", system =>
            {
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Mesh;
                renderer.mesh = null;
                var visible = new GameObject("VisibleBillboard");
                visible.transform.SetParent(system.transform, false);
                AddParticleSystem(visible);
            });
            WriteConfig(prefabPath, "best-effort");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("partial"));
            Assert.That(Occurrences(json, "\"type\":\"ParticleEmitter\""), Is.EqualTo(1));
            StringAssert.Contains("\"name\":\"VisibleBillboard\"", json);
            StringAssert.Contains("renderer.meshGeometry", report);
            StringAssert.Contains("instead of fabricating billboard geometry", report);
        }

        [Test]
        public void MeshRenderer_PositiveScaleKeepsIndicesAfterHandednessReflection()
        {
            var prefabPath = CreatePrefab("MeshRendererPositiveWinding", system =>
            {
                system.GetComponent<ParticleSystemRenderer>().renderMode = ParticleSystemRenderMode.Mesh;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"index\":{\"type\":\"Uint16Array\",\"array\":[0,1,2]}", json);
            StringAssert.DoesNotContain("\"index\":{\"type\":\"Uint16Array\",\"array\":[0,2,1]}", json);
        }

        [Test]
        public void MeshRenderer_NegativeScaleSwapsIndicesWhenItCancelsHandednessReflection()
        {
            var prefabPath = CreatePrefab("MeshRendererNegativeWinding", system =>
            {
                system.transform.localScale = new Vector3(-1, 1, 1);
                var main = system.main;
                main.scalingMode = ParticleSystemScalingMode.Local;
                system.GetComponent<ParticleSystemRenderer>().renderMode = ParticleSystemRenderMode.Mesh;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"index\":{\"type\":\"Uint16Array\",\"array\":[0,2,1]}", json);
            StringAssert.DoesNotContain("\"index\":{\"type\":\"Uint16Array\",\"array\":[0,1,2]}", json);
        }

        [Test]
        public void LegacyParticleAdditiveSoftMaterial_IsClassifiedExplicitly()
        {
            var shader = Shader.Find("Legacy Shaders/Particles/Additive (Soft)");
            Assert.That(shader, Is.Not.Null, "Built-in legacy particle shaders are required by this exporter fixture.");
            var material = new Material(shader);
            AssetDatabase.CreateAsset(material, FixtureRoot + "/LegacyAdditiveSoft.mat");
            var prefabPath = CreatePrefab("LegacyAdditiveSoft", system =>
            {
                system.GetComponent<ParticleSystemRenderer>().sharedMaterial = material;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"blending\":5", json);
            StringAssert.Contains("\"blendSrc\":201", json);
            StringAssert.Contains("\"blendDst\":203", json);
            StringAssert.Contains("\"fragmentColorMode\":\"legacySoftAdditive\"", json);
            StringAssert.Contains("\"depthWrite\":false", json);
            StringAssert.Contains("\"softParticles\":" + QualitySettings.softParticles.ToString().ToLowerInvariant(), json);
            StringAssert.Contains("material.fragmentColorRuntime.legacySoftAdditive", report);
        }

        [Test]
        public void LegacyPremultipliedParticleMaterial_MapsBlendAndDepthState()
        {
            var shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended Premultiply");
            Assert.That(shader, Is.Not.Null, "Built-in legacy premultiplied particle shader is required by this exporter fixture.");
            var material = new Material(shader);
            material.SetTexture("_MainTex", null);
            AssetDatabase.CreateAsset(material, FixtureRoot + "/LegacyPremultiplied.mat");
            var prefabPath = CreatePrefab("LegacyPremultiplied", system =>
            {
                system.GetComponent<ParticleSystemRenderer>().sharedMaterial = material;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"blending\":5", json);
            StringAssert.Contains("\"blendSrc\":201", json);
            StringAssert.Contains("\"blendDst\":205", json);
            StringAssert.Contains("\"fragmentColorMode\":\"legacyAlphaPremultiply\"", json);
            StringAssert.Contains("\"premultipliedAlpha\":false", json);
            StringAssert.Contains("\"transparent\":true", json);
            StringAssert.Contains("\"depthWrite\":false", json);
            StringAssert.Contains("material.fragmentColorRuntime.legacyAlphaPremultiply", report);
        }

        [TestCase("Legacy Shaders/Particles/Multiply", 200, 202, "legacyMultiply")]
        [TestCase("Legacy Shaders/Particles/Multiply (Double)", 208, 202, "legacyMultiplyDouble")]
        public void LegacyMultiplyProfiles_MapExactBlendAndFragmentFormula(
            string shaderName,
            int sourceBlend,
            int destinationBlend,
            string fragmentColorMode)
        {
            var shader = Shader.Find(shaderName);
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            material.SetTexture("_MainTex", null);
            AssetDatabase.CreateAsset(material, FixtureRoot + "/" + fragmentColorMode + ".mat");
            var prefabPath = CreatePrefab(fragmentColorMode, system =>
                system.GetComponent<ParticleSystemRenderer>().sharedMaterial = material);
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"blending\":5", json);
            StringAssert.Contains("\"blendSrc\":" + sourceBlend, json);
            StringAssert.Contains("\"blendDst\":" + destinationBlend, json);
            StringAssert.Contains("\"fragmentColorMode\":\"" + fragmentColorMode + "\"", json);
            StringAssert.Contains("material.fragmentColorRuntime." + fragmentColorMode, report);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BuiltInSoftParticles_RequireTheProjectQualitySwitch(bool enabled)
        {
            var previous = QualitySettings.softParticles;
            try
            {
                QualitySettings.softParticles = enabled;
                var shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
                Assert.That(shader, Is.Not.Null);
                var material = new Material(shader);
                material.SetFloat("_InvFade", 2);
                material.SetTexture("_MainTex", null);
                AssetDatabase.CreateAsset(material, FixtureRoot + "/SoftParticles-" + enabled + ".mat");
                var prefabPath = CreatePrefab("SoftParticles" + enabled, system =>
                    system.GetComponent<ParticleSystemRenderer>().sharedMaterial = material);
                WriteConfig(prefabPath, "strict");

                var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
                var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
                var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

                Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
                StringAssert.Contains("\"softParticles\":" + enabled.ToString().ToLowerInvariant(), json);
                if (enabled)
                {
                    StringAssert.Contains("\"softFarFade\":0.5", json);
                    StringAssert.Contains("material.softParticles.legacyInvFade", report);
                }
                else
                {
                    StringAssert.DoesNotContain("material.softParticles.legacyInvFade", report);
                    StringAssert.DoesNotContain("\"material.softParticles\"", report);
                }
            }
            finally
            {
                QualitySettings.softParticles = previous;
            }
        }

        [Test]
        public void UnsupportedLegacyParticleShader_HasStrictFailureAndNamedBestEffortFallback()
        {
            var shader = Shader.Find("Legacy Shaders/Particles/~Additive-Multiply");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            material.SetTexture("_MainTex", null);
            AssetDatabase.CreateAsset(material, FixtureRoot + "/UnsupportedLegacy.mat");
            var prefabPath = CreatePrefab("UnsupportedLegacy", system =>
                system.GetComponent<ParticleSystemRenderer>().sharedMaterial = material);

            WriteConfig(prefabPath, "strict");
            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);
            Assert.That(manifest.effects.Single().status, Is.EqualTo("profile_required"));
            Assert.That(manifest.effects.Single().publicationBlocked, Is.True);
            Assert.That(File.Exists(Path.Combine(outputRoot, "fixture", "effect.quarks.json")), Is.True);
            Assert.That(File.Exists(Path.Combine(outputRoot, "runtime-manifest.json")), Is.False);

            WriteConfig(prefabPath, "best-effort", unknownCustomShaderPolicy: "review-fallback");
            manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));
            Assert.That(manifest.effects.Single().status, Is.EqualTo("review_only"));
            Assert.That(manifest.effects.Single().publicationBlocked, Is.True);
            Assert.That(File.Exists(Path.Combine(outputRoot, "runtime-manifest.json")), Is.False);
            StringAssert.Contains("material.shaderBehavior.meshBasicFallback", report);
            StringAssert.Contains("outside the validated basic particle-shader set", report);
            StringAssert.Contains("shaderProfileGaps", report);
            StringAssert.Contains("\"requiredAction\": \"add-profile\"", report);

            WriteConfig(prefabPath, "strict", "presentation");
            manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);
            Assert.That(manifest.effects.Single().status, Is.EqualTo("profile_required"));
            Assert.That(File.Exists(Path.Combine(outputRoot, "fixture", "effect.quarks.json")), Is.True);
        }

        [Test]
        public void InternalErrorShader_RemainsFailedForPresentationAndPublishesNoFallback()
        {
            var shader = Shader.Find("Hidden/InternalErrorShader");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            material.name = "InternalError";
            AssetDatabase.CreateAsset(material, FixtureRoot + "/InternalError.mat");
            var prefabPath = CreatePrefab("InternalError", system =>
                system.GetComponent<ParticleSystemRenderer>().sharedMaterial = material);

            WriteConfig(prefabPath, "strict", "presentation");
            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("failed"));
            Assert.That(File.Exists(Path.Combine(outputRoot, "fixture", "effect.quarks.json")), Is.False);
            StringAssert.Contains("material.shaderResolution", report);
            StringAssert.Contains("Hidden/InternalErrorShader", report);
            StringAssert.Contains("\"shaderResolutionFailures\"", report);
            StringAssert.Contains("\"materialName\": \"InternalError\"", report);
            StringAssert.Contains("\"materialAssetPath\": \"Assets/__UnityParticleQuarksExporterTests/InternalError.mat\"", report);
            StringAssert.Contains("\"materialSlot\": \"renderer\"", report);
            StringAssert.Contains("\"resolvedShaderName\": \"Hidden/InternalErrorShader\"", report);
            StringAssert.Contains("\"failureKind\": \"internal_error_shader\"", report);
            StringAssert.Contains("playback-blocking unsupported features", manifest.effects.Single().errors.Single());
        }

        [Test]
        public void ParticleStandardNonMultiplyColorMode_HasExplicitFallback()
        {
            var shader = Shader.Find("Particles/Standard Unlit");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            material.SetFloat("_ColorMode", 1);
            material.SetTexture("_MainTex", null);
            AssetDatabase.CreateAsset(material, FixtureRoot + "/ParticleColorMode.mat");
            var prefabPath = CreatePrefab("ParticleColorMode", system =>
                system.GetComponent<ParticleSystemRenderer>().sharedMaterial = material);

            WriteConfig(prefabPath, "strict");
            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);
            Assert.That(manifest.effects.Single().status, Is.EqualTo("failed"));

            WriteConfig(prefabPath, "best-effort");
            manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));
            Assert.That(manifest.effects.Single().status, Is.EqualTo("partial"));
            StringAssert.Contains("material.particleColorMode.multiplyFallback", report);
        }

        [TestCase("Legacy Shaders/Particles/Additive", "BuiltInParticleUnlit")]
        [TestCase("Legacy Shaders/Particles/Anim Alpha Blended", "BuiltInParticleAnimAlphaBlended")]
        [TestCase("Legacy Shaders/Particles/VertexLit Blended", "BuiltInParticleVertexLit")]
        [TestCase("Mobile/Particles/VertexLit Blended", "BuiltInParticleVertexLit")]
        [TestCase("Particles/Standard Unlit", "BuiltInParticleUnlit")]
        [TestCase("Particles/Standard Surface", "BuiltInParticleStandardLit")]
        [TestCase("Standard", "BuiltInStandardMetallic")]
        [TestCase("Standard (Specular setup)", "BuiltInStandardSpecular")]
        [TestCase("Sprites/Default", "Sprite")]
        [TestCase("Unlit/Transparent Cutout", "BuiltInUnlitNoVertexColor")]
        [TestCase("Universal Render Pipeline/Particles/Unlit", "UrpParticleUnlit")]
        [TestCase("Universal Render Pipeline/Particles/Lit", "UrpParticleLit")]
        [TestCase("Universal Render Pipeline/Particles/Simple Lit", "UrpParticleSimpleLit")]
        [TestCase("Synty/Generic_ParticlesUnlit", "SyntyGenericParticlesUnlit")]
        [TestCase("Synty/Generic_ParticlesLit", "SyntyGenericParticlesLit")]
        [TestCase("Synty/Generic_Basic", "SyntyGenericBasic")]
        [TestCase("Universal Render Pipeline/Unlit", "UrpUnlit")]
        [TestCase("Universal Render Pipeline/Lit", "UrpLit")]
        [TestCase("Universal Render Pipeline/Simple Lit", "UrpSimpleLit")]
        [TestCase("HDRP/Unlit", "HdrpUnlit")]
        [TestCase("HDRP/Lit", "HdrpLit")]
        [TestCase("Hovl/Particles/Add_CenterGlow", "CustomHovlParticles")]
        [TestCase("Piloto Studio/UberFXSG", "CustomPilotoUberFxsg")]
        [TestCase("Effect/SoftDissolve_Additive_URP", "CustomVehicleEffect")]
        [TestCase("Shader Graphs/Fx_RockDissolve", "CustomShaderGraphRockDissolve")]
        [TestCase("Shader Graphs/Fx_ParticleDissolve_add", "CustomShaderGraphParticle")]
        [TestCase("Shader Graphs/CustomParticle", "Unsupported")]
        [TestCase("Synty/Generic_ParticlesUnlit Copy", "Unsupported")]
        [TestCase("Legacy Shaders/Particles/~Additive-Multiply", "Unsupported")]
        public void ShaderNameProfiles_AreExactAndPipelineScoped(string shaderName, string expectedProfile)
        {
            var profile = ShaderProfileRegistry.ResolveShaderName(shaderName);
            Assert.That(profile.ToString(), Is.EqualTo(expectedProfile));
        }

        [Test]
        public void ShaderProfileRegistry_MapsEveryExactNameOnce()
        {
            var entries = ShaderProfileRegistry.All
                .SelectMany(profile => profile.ShaderNames.Select(shaderName => (profile, shaderName)))
                .ToArray();

            Assert.That(entries, Is.Not.Empty);
            Assert.That(
                entries.Select(entry => entry.shaderName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                Is.EqualTo(entries.Length));
            foreach (var entry in entries)
                Assert.That(ShaderProfileRegistry.ResolveShaderName(entry.shaderName), Is.SameAs(entry.profile));
        }

        [Test]
        public void ShaderProfile_CustomCodeHookBuildsShaderParameters()
        {
            var profile = new CustomShaderGraphRockDissolveShaderProfile();
            var diagnostics = new ConversionDiagnostics();
            var context = new ShaderProfileMaterialContext(null, diagnostics, true);

            profile.ConfigureMaterial(context);

            Assert.That(context.shaderParametersOverride, Is.Not.Null);
            StringAssert.Contains("custom.shadergraph.rockDissolve", context.shaderParametersOverride.ToString());
            Assert.That(diagnostics.mapped, Does.Contain("material.shaderParameters.rockDissolve.v1.graphEvaluator"));
        }

        [TestCase("Legacy Shaders/Particles/Additive", 2, 204, 205, "stock", false)]
        [TestCase("Legacy Shaders/Particles/Additive (Soft)", 5, 201, 203, "legacySoftAdditive", true)]
        [TestCase("Legacy Shaders/Particles/Alpha Blended Premultiply", 5, 201, 205, "legacyAlphaPremultiply", true)]
        [TestCase("Legacy Shaders/Particles/Multiply", 5, 200, 202, "legacyMultiply", false)]
        [TestCase("Legacy Shaders/Particles/Multiply (Double)", 5, 208, 202, "legacyMultiplyDouble", false)]
        public void ShaderProfile_BuiltInBlendHookPreservesThreeFactors(
            string shaderName,
            int blending,
            int source,
            int destination,
            string fragmentColorMode,
            bool premultiplied)
        {
            var shader = Shader.Find(shaderName);
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            try
            {
                var profile = ShaderProfileRegistry.Resolve(material);
                var context = new ShaderProfileMaterialContext(material, new ConversionDiagnostics(), true);

                profile.ConfigureMaterial(context);

                Assert.That(context.blendStateOverride, Is.Not.Null);
                Assert.That(context.blendStateOverride.blending, Is.EqualTo(blending));
                Assert.That(context.blendStateOverride.blendSrc, Is.EqualTo(source));
                Assert.That(context.blendStateOverride.blendDst, Is.EqualTo(destination));
                Assert.That(context.blendStateOverride.fragmentColorMode, Is.EqualTo(fragmentColorMode));
                Assert.That(context.blendStateOverride.sourcePremultipliedAlpha, Is.EqualTo(premultiplied));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        [TestCase("Standard")]
        [TestCase("Standard (Specular setup)")]
        public void ShaderProfile_StandardCutoutHookPreservesAlphaTest(string shaderName)
        {
            var shader = Shader.Find(shaderName);
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            try
            {
                material.SetFloat("_Mode", 1);
                material.SetFloat("_Cutoff", 0.42f);
                var profile = ShaderProfileRegistry.Resolve(material);
                var context = new ShaderProfileMaterialContext(material, new ConversionDiagnostics(), true);

                profile.ConfigureMaterial(context);

                Assert.That(context.alphaTestOverride, Is.EqualTo(0.42f).Within(0.000001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        [TestCase("Synty/Generic_Basic", true, true, true, true, "_BUILTIN_CullMode")]
        [TestCase("Synty/Generic_ParticlesUnlit", true, true, true, false, "_BUILTIN_CullMode")]
        [TestCase("Synty/Generic_Basic", false, true, true, true, "_Cull")]
        [TestCase("Standard", true, true, true, true, "_Cull")]
        public void CullProperty_UsesTheSourceSyntyShaderGraphPipeline(
            string shaderName,
            bool builtInPipeline,
            bool hasBuiltInCullMode,
            bool hasCull,
            bool hasCullMode,
            string expectedProperty)
        {
            var profile = ShaderProfileRegistry.ResolveShaderName(shaderName);
            var property = profile.ResolveCullPropertyName(
                builtInPipeline,
                hasBuiltInCullMode,
                hasCull,
                hasCullMode);

            Assert.That(property, Is.EqualTo(expectedProperty));
        }

        [TestCase("default")]
        [TestCase("urp")]
        [TestCase("hdrp")]
        public void SourceRenderPipeline_IsRecordedInManifestAndReport(string sourceRenderPipeline)
        {
            var prefabPath = CreatePrefab("SourceRenderPipeline", _ => { });
            WriteConfig(prefabPath, "strict", sourceRenderPipeline: sourceRenderPipeline);

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.sourceRenderPipeline, Is.EqualTo(sourceRenderPipeline));
            StringAssert.Contains("\"sourceRenderPipeline\": \"" + sourceRenderPipeline + "\"", report);
        }

        [Test]
        public void BuiltInUnlitColor_NeutralizesParticleColorModules()
        {
            var shader = Shader.Find("Unlit/Color");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            material.SetColor("_Color", new Color(0.2f, 0.4f, 0.6f, 0.8f));
            AssetDatabase.CreateAsset(material, FixtureRoot + "/UnlitColor.mat");
            var prefabPath = CreatePrefab("UnlitColor", system =>
            {
                var main = system.main;
                main.startColor = Color.red;
                var color = system.colorOverLifetime;
                color.enabled = true;
                color.color = Color.green;
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Mesh;
                renderer.sharedMaterial = material;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"startColor\":{\"type\":\"ConstantColor\",\"color\":{\"r\":0.2,\"g\":0.4,\"b\":0.6,\"a\":0.8}}", json);
            StringAssert.DoesNotContain("\"type\":\"ColorOverLife\"", json);
            StringAssert.Contains("colorOverLifetime.notConsumedBySourceShader", report);
            StringAssert.Contains("material.shader.unlitNoVertexColorToParticleColor", report);
        }

        [Test]
        public void LegacyTint_MultipliesBothRandomStartColorGradients()
        {
            var shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            material.SetColor("_TintColor", new Color(0.25f, 0.5f, 0.75f, 0.5f));
            material.SetTexture("_MainTex", null);
            AssetDatabase.CreateAsset(material, FixtureRoot + "/GradientTint.mat");
            var minimum = new Gradient();
            minimum.SetKeys(
                new[] { new GradientColorKey(Color.red, 0), new GradientColorKey(Color.green, 1) },
                new[] { new GradientAlphaKey(0.25f, 0), new GradientAlphaKey(0.5f, 1) });
            var maximum = new Gradient();
            maximum.SetKeys(
                new[] { new GradientColorKey(Color.blue, 0), new GradientColorKey(Color.white, 1) },
                new[] { new GradientAlphaKey(0.75f, 0), new GradientAlphaKey(1, 1) });
            var prefabPath = CreatePrefab("GradientTint", system =>
            {
                var main = system.main;
                main.startColor = new ParticleSystem.MinMaxGradient(minimum, maximum);
                system.GetComponent<ParticleSystemRenderer>().sharedMaterial = material;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"type\":\"RandomColorBetweenGradient\"", json);
            StringAssert.Contains("\"value\":{\"r\":0.5,\"g\":0,\"b\":0}", json);
            StringAssert.Contains("\"value\":{\"r\":0,\"g\":1,\"b\":0}", json);
            StringAssert.Contains("\"value\":{\"r\":0,\"g\":0,\"b\":1.5}", json);
            StringAssert.Contains("\"value\":{\"r\":0.5,\"g\":1,\"b\":1.5}", json);
        }

        [Test]
        public void StandardMetallicGloss_RepackagesUnityChannelsForThreeOffline()
        {
            var sourceImage = new Texture2D(2, 1, TextureFormat.RGBA32, false, true);
            sourceImage.SetPixels32(new[]
            {
                new Color32(64, 7, 9, 128),
                new Color32(255, 11, 13, 0)
            });
            sourceImage.Apply(false, false);
            var texturePath = FixtureRoot + "/MetallicGloss.png";
            File.WriteAllBytes(texturePath, sourceImage.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(sourceImage);
            AssetDatabase.ImportAsset(texturePath, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(texturePath);
            importer.sRGBTexture = false;
            importer.isReadable = false;
            importer.SaveAndReimport();
            var packedMap = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);

            var material = new Material(Shader.Find("Standard"));
            material.SetTexture("_MetallicGlossMap", packedMap);
            material.SetFloat("_GlossMapScale", 0.5f);
            material.EnableKeyword("_METALLICGLOSSMAP");
            AssetDatabase.CreateAsset(material, FixtureRoot + "/MetallicGloss.mat");
            var prefabPath = CreatePrefab("MetallicGloss", system =>
            {
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Mesh;
                renderer.sharedMaterial = material;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));
            var textureDirectory = Path.Combine(outputRoot, "fixture", "textures");
            var metalnessPath = Directory.GetFiles(textureDirectory, "*metalness-blue-from-red*.png").Single();
            var roughnessPath = Directory.GetFiles(textureDirectory, "*roughness-green-from-one-minus-alpha*.png").Single();
            var metalness = new Texture2D(2, 1, TextureFormat.RGBA32, false, true);
            var roughness = new Texture2D(2, 1, TextureFormat.RGBA32, false, true);
            try
            {
                Assert.That(ImageConversion.LoadImage(metalness, File.ReadAllBytes(metalnessPath), false), Is.True);
                Assert.That(ImageConversion.LoadImage(roughness, File.ReadAllBytes(roughnessPath), false), Is.True);
                var metalnessPixels = metalness.GetPixels32();
                var roughnessPixels = roughness.GetPixels32();

                Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
                StringAssert.Contains("\"metalness\":1", json);
                StringAssert.Contains("\"roughness\":1", json);
                StringAssert.Contains("\"metalnessMap\"", json);
                StringAssert.Contains("\"roughnessMap\"", json);
                Assert.That(metalnessPixels[0].b, Is.EqualTo(64));
                Assert.That(metalnessPixels[1].b, Is.EqualTo(255));
                Assert.That(roughnessPixels[0].g, Is.EqualTo(191));
                Assert.That(roughnessPixels[1].g, Is.EqualTo(255));
                StringAssert.Contains("material.metallicGlossMap.channelRepack", report);
                StringAssert.DoesNotContain("\"material.metallicGlossMap\"", report);
                Assert.That(((TextureImporter)AssetImporter.GetAtPath(texturePath)).isReadable, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(metalness);
                UnityEngine.Object.DestroyImmediate(roughness);
            }
        }

        [Test]
        public void Trail_MapsLifetimeWidthColorAndDedicatedMaterialSlots()
        {
            var shader = Shader.Find("Legacy Shaders/Particles/Additive");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            material.SetTexture("_MainTex", null);
            AssetDatabase.CreateAsset(material, FixtureRoot + "/Trail.mat");
            var prefabPath = CreatePrefab("Trail", system =>
            {
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.None;
                renderer.trailMaterial = material;
                var trails = system.trails;
                trails.enabled = true;
                trails.lifetime = 1;
                trails.minVertexDistance = 0.001f;
                trails.dieWithParticles = false;
                trails.sizeAffectsWidth = false;
                trails.inheritParticleColor = true;
                trails.colorOverTrail = new ParticleSystem.MinMaxGradient(Color.white);
                trails.widthOverTrail = new ParticleSystem.MinMaxCurve(
                    0.1f,
                    AnimationCurve.EaseInOut(0, 0, 1, 1));
                var size = system.sizeOverLifetime;
                size.enabled = true;
                size.size = 50;
                var color = new Gradient();
                color.SetKeys(
                    new[] { new GradientColorKey(Color.blue, 0), new GradientColorKey(Color.cyan, 1) },
                    new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(1, 1) });
                trails.colorOverLifetime = new ParticleSystem.MinMaxGradient(color);
                var particleColor = system.colorOverLifetime;
                particleColor.enabled = true;
                particleColor.color = new ParticleSystem.MinMaxGradient(color);
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"renderMode\":3", json);
            StringAssert.Contains("\"startLength\":{\"type\":\"ConstantValue\",\"value\":60}", json);
            StringAssert.Contains("\"minVertexDistance\":0.001", json);
            StringAssert.Contains("\"type\":\"WidthOverLength\"", json);
            StringAssert.Contains("\"type\":\"ColorOverLife\"", json);
            StringAssert.Contains("\"trailInheritParticleColor\"", json);
            StringAssert.Contains("unity_particle_quarks_exporter.trail_inherit_particle_color.v1", json);
            StringAssert.Contains("\"startSize\":{\"type\":\"ConstantValue\",\"value\":0}", json);
            StringAssert.DoesNotContain("\"type\":\"SizeOverLife\"", json);
            StringAssert.Contains("\"blending\":2", json);
            StringAssert.Contains("renderer.trailMaterial", report);
            StringAssert.Contains("trails.lifetime.frameSamples", report);
            StringAssert.Contains("trails.widthOverTrail", report);
            StringAssert.Contains("trails.minVertexDistance.runtime", report);
            StringAssert.Contains("trails.inheritParticleColor.runtime", report);
            StringAssert.Contains("trails.inheritParticleColor.stockOmittedFallback", report);
            StringAssert.Contains("trails.headWidthSample", report);
            StringAssert.Contains("sizeOverLifetime.trailWidthIndependent", report);
        }

        [Test]
        public void TrailWithParticleHead_EmitsIndependentCompanionMetadata()
        {
            var trailMaterial = new Material(Shader.Find("Legacy Shaders/Particles/Additive"));
            trailMaterial.SetTexture("_MainTex", null);
            AssetDatabase.CreateAsset(trailMaterial, FixtureRoot + "/TrailWithHead.mat");
            var prefabPath = CreatePrefab("TrailWithHead", system =>
            {
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Stretch;
                renderer.velocityScale = 0.25f;
                renderer.lengthScale = 2f;
                renderer.sharedMaterial = fixtureMaterial;
                renderer.trailMaterial = trailMaterial;
                renderer.sortingOrder = 7;
                var trails = system.trails;
                trails.enabled = true;
                trails.lifetime = 0.5f;
                trails.sizeAffectsWidth = false;
                trails.inheritParticleColor = false;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"renderMode\":3", json);
            StringAssert.Contains("\"particleHead\":{", json);
            StringAssert.Contains("unity_particle_quarks_exporter.particle_head.v1", json);
            StringAssert.Contains("\"renderOrder\":7", json);
            StringAssert.Contains("\"renderMode\":1", json);
            StringAssert.Contains("\"rendererEmitterSettings\":{\"speedFactor\":0.25,\"lengthFactor\":2}", json);
            StringAssert.Contains("trails.particleHeadRenderer.metadata.v1", report);
            StringAssert.Contains("trails.particleHeadRenderer.stretchedBillboard", report);
        }

        [Test]
        public void TrailSizeAffectsWidth_EmitsPairedRuntimeMetadata()
        {
            var trailMaterial = new Material(Shader.Find("Legacy Shaders/Particles/Additive"));
            trailMaterial.SetTexture("_MainTex", null);
            AssetDatabase.CreateAsset(trailMaterial, FixtureRoot + "/TrailSizeWidth.mat");
            var prefabPath = CreatePrefab("TrailSizeWidth", system =>
            {
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.None;
                renderer.trailMaterial = trailMaterial;
                var trails = system.trails;
                trails.enabled = true;
                trails.sizeAffectsWidth = true;
                trails.colorOverTrail = new ParticleSystem.MinMaxGradient(Color.white);
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"sizeAffectsWidth\":true", json);
            StringAssert.Contains("trails.sizeAffectsWidth.runtime", report);
            StringAssert.Contains("trails.sizeAffectsWidth.stockWidthReplacementFallback", report);
            StringAssert.DoesNotContain("\"trails.sizeAffectsWidth\"", report);
        }

        [Test]
        public void TrailTwoCurveWidth_UsesMeanFallbackAndRemainsExplicitStrictFailure()
        {
            var material = new Material(Shader.Find("Legacy Shaders/Particles/Additive"));
            material.SetTexture("_MainTex", null);
            AssetDatabase.CreateAsset(material, FixtureRoot + "/TrailUnsupported.mat");
            var prefabPath = CreatePrefab("TrailUnsupported", system =>
            {
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.None;
                renderer.trailMaterial = material;
                var trails = system.trails;
                trails.enabled = true;
                trails.dieWithParticles = false;
                trails.sizeAffectsWidth = false;
                trails.widthOverTrail = new ParticleSystem.MinMaxCurve(
                    0.1f,
                    AnimationCurve.Linear(0, 0, 1, 0.5f),
                    AnimationCurve.Linear(0, 0, 1, 1));
                trails.colorOverTrail = new ParticleSystem.MinMaxGradient(Color.white);
            });
            WriteConfig(prefabPath, "best-effort");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("partial"));
            StringAssert.Contains("\"p3\":0.075", json);
            StringAssert.Contains("trails.widthOverTrail.twoCurvesMean", report);
            StringAssert.Contains("arithmetic mean curve", report);

            WriteConfig(prefabPath, "strict");
            manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);
            Assert.That(manifest.effects.Single().status, Is.EqualTo("failed"));
            Assert.That(File.Exists(Path.Combine(outputRoot, "fixture", "effect.quarks.json")), Is.False);
            StringAssert.Contains("trails.widthOverTrail.twoCurves", report);
        }

        [Test]
        public void TextureExport_PreservesSourcePixelsWithoutGraphicsDevice()
        {
            var sourceTexture = new Texture2D(5, 3, TextureFormat.RGBA32, false, false);
            var sourcePixels = Enumerable.Range(0, 15)
                .Select(index => new Color32(
                    (byte)(20 + index * 7),
                    (byte)(180 - index * 5),
                    (byte)(40 + index * 3),
                    (byte)(index == 0 ? 0 : 30 + index * 15)))
                .ToArray();
            sourceTexture.SetPixels32(sourcePixels);
            sourceTexture.Apply(false, false);
            var texturePath = FixtureRoot + "/AsymmetricAlpha.png";
            File.WriteAllBytes(texturePath, sourceTexture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(sourceTexture);
            AssetDatabase.ImportAsset(texturePath, ImportAssetOptions.ForceSynchronousImport);
            var importedTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            Assert.That(importedTexture, Is.Not.Null);

            var shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
            Assert.That(shader, Is.Not.Null, "Built-in legacy particle shaders are required by this exporter fixture.");
            var material = new Material(shader) { mainTexture = importedTexture };
            AssetDatabase.CreateAsset(material, FixtureRoot + "/AsymmetricAlpha.mat");
            var prefabPath = CreatePrefab("AsymmetricAlpha", system =>
                system.GetComponent<ParticleSystemRenderer>().sharedMaterial = material);
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var exportedPath = Directory.GetFiles(
                Path.Combine(outputRoot, "fixture", "textures"),
                "*.png",
                SearchOption.TopDirectoryOnly).Single();
            var exportedTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            Assert.That(ImageConversion.LoadImage(exportedTexture, File.ReadAllBytes(exportedPath), false), Is.True);
            var exportedPixels = exportedTexture.GetPixels32();

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            Assert.That(exportedTexture.width, Is.EqualTo(5));
            Assert.That(exportedTexture.height, Is.EqualTo(3));
            Assert.That(exportedPixels.Select(pixel => pixel.a).Distinct().Count(), Is.GreaterThan(1));
            Assert.That(exportedPixels.Any(pixel => pixel.a == 0), Is.True);
            Assert.That(exportedPixels.Select(pixel => pixel.r).Distinct().Count(), Is.GreaterThan(1));
            UnityEngine.Object.DestroyImmediate(exportedTexture);
        }

        [Test]
        public void TextureExport_BakesUnityGrayscaleAlphaImporterSemantics()
        {
            var sourceTexture = new Texture2D(3, 1, TextureFormat.RGBA32, false, false);
            sourceTexture.SetPixels32(new[]
            {
                new Color32(0, 0, 0, 255),
                new Color32(96, 96, 96, 255),
                new Color32(255, 255, 255, 255)
            });
            sourceTexture.Apply(false, false);
            var texturePath = FixtureRoot + "/GrayscaleAlpha.png";
            File.WriteAllBytes(texturePath, sourceTexture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(sourceTexture);
            AssetDatabase.ImportAsset(texturePath, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(texturePath);
            importer.alphaSource = TextureImporterAlphaSource.FromGrayScale;
            importer.isReadable = false;
            importer.SaveAndReimport();

            var importedTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            var shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader) { mainTexture = importedTexture };
            AssetDatabase.CreateAsset(material, FixtureRoot + "/GrayscaleAlpha.mat");
            var prefabPath = CreatePrefab("GrayscaleAlpha", system =>
                system.GetComponent<ParticleSystemRenderer>().sharedMaterial = material);
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));
            var exportedPath = Directory.GetFiles(
                Path.Combine(outputRoot, "fixture", "textures"),
                "*.png",
                SearchOption.TopDirectoryOnly).Single();
            var exportedTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            try
            {
                Assert.That(ImageConversion.LoadImage(exportedTexture, File.ReadAllBytes(exportedPath), false), Is.True);
                var alpha = exportedTexture.GetPixels32().Select(pixel => pixel.a).ToArray();
                Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
                Assert.That(alpha.Min(), Is.LessThan(16));
                // Unity 2022.3 and Unity 6 importers quantize a source white
                // texel to 237 when alphaSource=FromGrayScale. Keep a
                // high-end guard without making that stable importer detail
                // a false exporter failure.
                Assert.That(alpha.Max(), Is.GreaterThanOrEqualTo(230));
                // The importer/PNG round-trip may resample the 3x1 fixture to
                // four texels, while preserving at least three alpha levels.
                Assert.That(alpha.Distinct().Count(), Is.GreaterThanOrEqualTo(3));
                Assert.That(((TextureImporter)AssetImporter.GetAtPath(texturePath)).isReadable, Is.False);
                StringAssert.Contains("material.mainTextureImporterAlphaBake", report);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(exportedTexture);
            }
        }

        [Test]
        public void ReadableImportedTextureFallback_IsExplicitApproximation()
        {
            var readableTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            readableTexture.SetPixels32(new[]
            {
                new Color32(255, 0, 0, 0),
                new Color32(0, 255, 0, 85),
                new Color32(0, 0, 255, 170),
                new Color32(255, 255, 255, 255)
            });
            readableTexture.Apply(false, false);
            AssetDatabase.CreateAsset(readableTexture, FixtureRoot + "/ReadableTexture.asset");
            var shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader) { mainTexture = readableTexture };
            AssetDatabase.CreateAsset(material, FixtureRoot + "/ReadableTexture.mat");
            var prefabPath = CreatePrefab("ReadableTexture", system =>
                system.GetComponent<ParticleSystemRenderer>().sharedMaterial = material);
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            Assert.That(Directory.GetFiles(Path.Combine(outputRoot, "fixture", "textures"), "*.png").Length, Is.EqualTo(1));
            StringAssert.Contains("material.mainTextureImporterCpuReadback", report);
            StringAssert.Contains("Unity importer texels", report);
        }

        [Test]
        public void StretchedBillboardVelocityScale_IsNormalizedAtMaximumParticleSize()
        {
            var prefabPath = CreatePrefab("StretchedVelocity", system =>
            {
                var main = system.main;
                main.startSize = 0.01f;
                var size = system.sizeOverLifetime;
                size.enabled = true;
                size.size = 5;
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Stretch;
                renderer.velocityScale = 0.1f;
                renderer.lengthScale = 0.05f;
                renderer.cameraVelocityScale = 0;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"speedFactor\":2", json);
            StringAssert.Contains("renderer.stretchedBillboard.velocityScale", report);
            StringAssert.Contains("maximum expected particle size", report);
        }

        [Test]
        public void StretchedBillboardCameraVelocityScale_IsExplicitStrictFailure()
        {
            var prefabPath = CreatePrefab("StretchedCameraVelocity", system =>
            {
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Stretch;
                renderer.cameraVelocityScale = 0.5f;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("failed"));
            Assert.That(File.Exists(Path.Combine(outputRoot, "fixture", "effect.quarks.json")), Is.False);
            StringAssert.Contains("renderer.stretchedBillboard.cameraVelocityScale", report);
        }

        [Test]
        public void BirthAndDeathSubEmitters_AreLinkedByStableUuid()
        {
            var root = new GameObject("Subemitters");
            var parent = AddParticleSystem(root);
            var childObject = new GameObject("Child");
            childObject.transform.SetParent(root.transform, false);
            var child = AddParticleSystem(childObject);
            var subEmitters = parent.subEmitters;
            subEmitters.enabled = true;
            subEmitters.AddSubEmitter(child, ParticleSystemSubEmitterType.Birth, ParticleSystemSubEmitterProperties.InheritNothing);
            subEmitters.AddSubEmitter(child, ParticleSystemSubEmitterType.Death, ParticleSystemSubEmitterProperties.InheritNothing);
            subEmitters.SetSubEmitterEmitProbability(0, 0.25f);
            subEmitters.SetSubEmitterEmitProbability(1, 0.75f);
            var prefabPath = FixtureRoot + "/Subemitters.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            Assert.That(Occurrences(json, "EmitSubParticleSystem"), Is.EqualTo(2));
            StringAssert.Contains("onlyUsedByOther\":true", json);
            StringAssert.Contains("emitProbability\":0.25", json);
            StringAssert.Contains("emitProbability\":0.75", json);
            Assert.That(Occurrences(json, "useVelocityAsBasis\":false"), Is.EqualTo(2));
            StringAssert.Contains("subEmitters.Birth.inheritNothing", report);
            StringAssert.Contains("subEmitters.Death.triggerTransform", report);
        }

        [Test]
        public void SubEmitterColorSizeRotationAndLifetime_AreMappedToRuntimeMetadata()
        {
            var root = new GameObject("SubemitterInheritance");
            var parent = AddParticleSystem(root);
            var childObject = new GameObject("Child");
            childObject.transform.SetParent(root.transform, false);
            var child = AddParticleSystem(childObject);
            var properties = ParticleSystemSubEmitterProperties.InheritColor |
                             ParticleSystemSubEmitterProperties.InheritSize |
                             ParticleSystemSubEmitterProperties.InheritRotation |
                             ParticleSystemSubEmitterProperties.InheritLifetime;
            var subEmitters = parent.subEmitters;
            subEmitters.enabled = true;
            subEmitters.AddSubEmitter(child, ParticleSystemSubEmitterType.Death, properties);
            var prefabPath = FixtureRoot + "/SubemitterInheritance.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));
            var childId = UnityParticleQuarksStableId.Create(prefabPath, "SubemitterInheritance/Child", "particle-emitter");

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"schemaVersion\":\"unity_particle_quarks_exporter.user_data.v1\"", json);
            StringAssert.Contains(
                "\"subEmitterInheritance\":[{\"index\":0,\"subParticleSystem\":\"" + childId +
                "\",\"mode\":0,\"inheritColor\":true,\"inheritSize\":true,\"inheritRotation\":true,\"inheritLifetime\":true,\"inheritDuration\":false}]",
                json);
            StringAssert.Contains("\"useVelocityAsBasis\":false", json);
            StringAssert.Contains("subEmitters.Death.inheritColor", report);
            StringAssert.Contains("subEmitters.Death.inheritSize", report);
            StringAssert.Contains("subEmitters.Death.inheritRotation", report);
            StringAssert.Contains("subEmitters.Death.inheritLifetime", report);
        }

        [Test]
        public void SubEmitterDurationInheritance_UsesPairedRuntimeMetadataInStrictMode()
        {
            var root = new GameObject("SubemitterDuration");
            var parent = AddParticleSystem(root);
            var childObject = new GameObject("Child");
            childObject.transform.SetParent(root.transform, false);
            var child = AddParticleSystem(childObject);
            var subEmitters = parent.subEmitters;
            subEmitters.enabled = true;
            subEmitters.AddSubEmitter(
                child,
                ParticleSystemSubEmitterType.Death,
                ParticleSystemSubEmitterProperties.InheritDuration);
            var prefabPath = FixtureRoot + "/SubemitterDuration.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"inheritDuration\":true", json);
            StringAssert.Contains("subEmitters.Death.inheritDuration.runtime", report);
            StringAssert.Contains("subEmitters.Death.inheritDuration.stockChildDurationFallback", report);
        }

        [Test]
        public void UnitySubEmitterInheritance_UsesMeasuredScalarRules()
        {
            var color = TriggerManualSubEmitter(
                ParticleSystemSubEmitterProperties.InheritColor,
                10f,
                5f);
            var scalarSize = TriggerManualSubEmitter(
                ParticleSystemSubEmitterProperties.InheritSize,
                10f,
                5f);
            var billboardSize3D = TriggerManualSubEmitter(
                ParticleSystemSubEmitterProperties.InheritSize,
                10f,
                5f,
                new Vector3(3f, 5f, 7f),
                new Vector3(2f, 3f, 4f));
            var meshSize3D = TriggerManualSubEmitter(
                ParticleSystemSubEmitterProperties.InheritSize,
                10f,
                5f,
                new Vector3(3f, 5f, 7f),
                new Vector3(2f, 3f, 4f),
                true);
            var billboardRotation = TriggerManualSubEmitter(
                ParticleSystemSubEmitterProperties.InheritRotation,
                10f,
                5f);
            var meshRotation = TriggerManualSubEmitter(
                ParticleSystemSubEmitterProperties.InheritRotation,
                10f,
                5f,
                null,
                null,
                true,
                new Vector3(40f, 50f, 60f),
                new Vector3(10f, 20f, 30f));
            var lifetimeFull = TriggerManualSubEmitter(
                ParticleSystemSubEmitterProperties.InheritLifetime,
                10f,
                10f);
            var lifetimeHalf = TriggerManualSubEmitter(
                ParticleSystemSubEmitterProperties.InheritLifetime,
                10f,
                5f);
            var lifetimeTenth = TriggerManualSubEmitter(
                ParticleSystemSubEmitterProperties.InheritLifetime,
                10f,
                1f);
            var lifetimeDifferentStart = TriggerManualSubEmitter(
                ParticleSystemSubEmitterProperties.InheritLifetime,
                20f,
                5f);

            Assert.That(color.startColor, Is.EqualTo(new Color32(102, 38, 13, 64)));
            Assert.That(color.currentColor, Is.EqualTo(new Color32(102, 38, 13, 64)));
            Assert.That(scalarSize.startSize, Is.EqualTo(new Vector3(6f, 6f, 6f)));
            Assert.That(scalarSize.currentSize, Is.EqualTo(new Vector3(6f, 6f, 6f)));
            Assert.That(billboardSize3D.startSize, Is.EqualTo(new Vector3(6f, 6f, 6f)));
            Assert.That(meshSize3D.startSize, Is.EqualTo(new Vector3(6f, 6f, 6f)));
            Assert.That(billboardRotation.rotation, Is.EqualTo(new Vector3(0f, 0f, 90f)));
            Assert.That(meshRotation.rotation, Is.EqualTo(new Vector3(0f, 0f, 90f)));
            Assert.That(lifetimeFull.startLifetime, Is.EqualTo(40f));
            Assert.That(lifetimeHalf.startLifetime, Is.EqualTo(20f));
            Assert.That(lifetimeTenth.startLifetime, Is.EqualTo(4f));
            Assert.That(lifetimeDifferentStart.startLifetime, Is.EqualTo(20f));
        }

        private struct SubEmitterProbeResult
        {
            public Color32 startColor;
            public Color32 currentColor;
            public Vector3 startSize;
            public Vector3 currentSize;
            public Vector3 rotation;
            public float startLifetime;
            public float remainingLifetime;
        }

        private static SubEmitterProbeResult TriggerManualSubEmitter(
            ParticleSystemSubEmitterProperties properties,
            float parentStartLifetime,
            float parentRemainingLifetime,
            Vector3? parentSize = null,
            Vector3? childSize = null,
            bool meshRenderer = false,
            Vector3? parentRotation = null,
            Vector3? childRotation = null)
        {
            var root = new GameObject("SubEmitterSourceProbe");
            try
            {
                var parent = root.AddComponent<ParticleSystem>();
                if (meshRenderer)
                {
                    root.GetComponent<ParticleSystemRenderer>().renderMode = ParticleSystemRenderMode.Mesh;
                }
                var parentMain = parent.main;
                parentMain.playOnAwake = false;
                parentMain.startLifetime = parentStartLifetime;

                var childObject = new GameObject("Child");
                childObject.transform.SetParent(root.transform, false);
                var child = childObject.AddComponent<ParticleSystem>();
                if (meshRenderer)
                {
                    childObject.GetComponent<ParticleSystemRenderer>().renderMode = ParticleSystemRenderMode.Mesh;
                }
                var childMain = child.main;
                childMain.playOnAwake = false;
                childMain.loop = false;
                childMain.startLifetime = 4f;
                var configuredChildSize = childSize ?? new Vector3(2f, 2f, 2f);
                childMain.startSize3D = childSize.HasValue;
                childMain.startSizeX = configuredChildSize.x;
                childMain.startSizeY = configuredChildSize.y;
                childMain.startSizeZ = configuredChildSize.z;
                var configuredChildRotation = childRotation ?? new Vector3(0f, 0f, 30f);
                childMain.startRotation3D = meshRenderer;
                childMain.startRotationX = configuredChildRotation.x * Mathf.Deg2Rad;
                childMain.startRotationY = configuredChildRotation.y * Mathf.Deg2Rad;
                childMain.startRotationZ = configuredChildRotation.z * Mathf.Deg2Rad;
                if (childSize.HasValue)
                {
                    Assert.That(childMain.startSize3D, Is.True);
                    Assert.That(childMain.startSizeX.constant, Is.EqualTo(configuredChildSize.x));
                    Assert.That(childMain.startSizeY.constant, Is.EqualTo(configuredChildSize.y));
                    Assert.That(childMain.startSizeZ.constant, Is.EqualTo(configuredChildSize.z));
                }
                if (meshRenderer)
                {
                    Assert.That(childMain.startRotation3D, Is.True);
                    Assert.That(childMain.startRotationX.constant, Is.EqualTo(configuredChildRotation.x * Mathf.Deg2Rad).Within(0.000001f));
                    Assert.That(childMain.startRotationY.constant, Is.EqualTo(configuredChildRotation.y * Mathf.Deg2Rad).Within(0.000001f));
                    Assert.That(childMain.startRotationZ.constant, Is.EqualTo(configuredChildRotation.z * Mathf.Deg2Rad).Within(0.000001f));
                }
                childMain.startColor = new Color(0.8f, 0.6f, 0.4f, 0.5f);
                var childEmission = child.emission;
                childEmission.rateOverTime = 0f;
                childEmission.SetBursts(new[] { new ParticleSystem.Burst(0f, 1) });

                var subEmitters = parent.subEmitters;
                subEmitters.enabled = true;
                subEmitters.AddSubEmitter(child, ParticleSystemSubEmitterType.Manual, properties);

                var parentParticle = new ParticleSystem.Particle
                {
                    startLifetime = parentStartLifetime,
                    remainingLifetime = parentRemainingLifetime,
                    startColor = new Color32(128, 64, 32, 128),
                    startSize3D = parentSize ?? new Vector3(3f, 3f, 3f),
                    rotation3D = parentRotation ?? new Vector3(0f, 0f, 60f),
                    position = new Vector3(1f, 2f, 3f),
                    randomSeed = 12345
                };
                parent.TriggerSubEmitter(0, ref parentParticle);

                var particles = new ParticleSystem.Particle[4];
                var count = child.GetParticles(particles);
                Assert.That(count, Is.EqualTo(1));
                return new SubEmitterProbeResult
                {
                    startColor = particles[0].startColor,
                    currentColor = particles[0].GetCurrentColor(child),
                    startSize = particles[0].startSize3D,
                    currentSize = particles[0].GetCurrentSize3D(child),
                    rotation = particles[0].rotation3D,
                    startLifetime = particles[0].startLifetime,
                    remainingLifetime = particles[0].remainingLifetime
                };
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void StartSpeedTwoCurves_RemainsExplicitStrictFailureWithRuntimeBoxMapping()
        {
            var prefabPath = CreatePrefab("Unsupported", system =>
            {
                var shape = system.shape;
                shape.shapeType = ParticleSystemShapeType.Box;
                var main = system.main;
                main.startSpeed = new ParticleSystem.MinMaxCurve(1, AnimationCurve.Linear(0, 1, 1, 2), AnimationCurve.Linear(0, 2, 1, 3));
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("failed"));
            StringAssert.Contains("shape.boxVolume.runtime", report);
            StringAssert.Contains("main.startSpeed.twoCurves", report);
        }

        [Test]
        public void BoxShapeAndSizeTwoCurves_UseRuntimeMetadataWithStockFallbacks()
        {
            var prefabPath = CreatePrefab("BoxBestEffort", system =>
            {
                var shape = system.shape;
                shape.shapeType = ParticleSystemShapeType.Box;
                shape.scale = new Vector3(4, 6, 8);
                var size = system.sizeOverLifetime;
                size.enabled = true;
                size.size = new ParticleSystem.MinMaxCurve(
                    1,
                    AnimationCurve.Linear(0, 1, 1, 1),
                    AnimationCurve.Linear(0, 3, 1, 3));
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"shape\":{\"type\":\"mesh_surface\"", json);
            StringAssert.Contains("unity_particle_quarks_exporter.shape_semantics.v1", json);
            StringAssert.Contains("\"type\":\"boxVolume\"", json);
            StringAssert.Contains("unity_particle_quarks_exporter.size_over_lifetime.v1", json);
            StringAssert.Contains("shape.boxVolume.runtime", report);
            StringAssert.Contains("shape.boxDiscreteGrid.stockFallback", report);
            StringAssert.Contains("shape.boxDimensions", report);
            StringAssert.Contains("sizeOverLifetime.twoCurvesRuntime", report);
            StringAssert.Contains("sizeOverLifetime.twoCurves.stockMeanFallback", report);
            StringAssert.Contains("stable per-particle blend", report);
        }

        [Test]
        public void UnityDefaultParticleFallback_IsDeterministicRadialPng()
        {
            var first = QuarksMaterialConverter.EncodeUnityDefaultParticleFallback();
            var second = QuarksMaterialConverter.EncodeUnityDefaultParticleFallback();
            CollectionAssert.AreEqual(first, second);
            var image = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            try
            {
                Assert.That(ImageConversion.LoadImage(image, first, false), Is.True);
                Assert.That(image.width, Is.EqualTo(64));
                Assert.That(image.height, Is.EqualTo(64));
                Assert.That(image.GetPixel(32, 32).a, Is.GreaterThan(0.95f));
                Assert.That(image.GetPixel(0, 0).a, Is.LessThan(0.01f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(image);
            }
        }

        [Test]
        public void UnityDefaultParticleMaterial_ExportsRealTextureOrExplicitRadialFallback()
        {
            var material = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Particle.mat");
            Assert.That(material, Is.Not.Null);
            var prefabPath = CreatePrefab("DefaultParticleMaterial", system =>
                system.GetComponent<ParticleSystemRenderer>().sharedMaterial = material);
            WriteConfig(prefabPath, "best-effort");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().textures.Length, Is.EqualTo(1));
            StringAssert.EndsWith(".png", manifest.effects.Single().textures[0]);
            StringAssert.Contains("\"map\"", json);
            if (report.Contains("material.unityDefaultParticle.gpuReadback"))
            {
                Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
                StringAssert.DoesNotContain("material.mainTextureExport", report);
                StringAssert.Contains("material.unityDefaultParticle.alphaOnlyRgbExpansion", report);
                var texturePath = Path.Combine(
                    outputRoot,
                    manifest.effects.Single().textures.Single().Replace('/', Path.DirectorySeparatorChar));
                var image = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                try
                {
                    Assert.That(ImageConversion.LoadImage(image, File.ReadAllBytes(texturePath), false), Is.True);
                    var visiblePixels = image.GetPixels32().Where(pixel => pixel.a > 0).ToArray();
                    Assert.That(visiblePixels.Length, Is.GreaterThan(0));
                    Assert.That(visiblePixels.All(pixel => pixel.r == 255 && pixel.g == 255 && pixel.b == 255), Is.True,
                        "Alpha-only built-in textures must expand to white RGB plus the sampled alpha channel.");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(image);
                }
            }
            else
            {
                Assert.That(manifest.effects.Single().status, Is.EqualTo("partial"));
                StringAssert.Contains("material.mainTextureExport", report);
                StringAssert.Contains("material.unityDefaultParticle.radialFallback", report);
            }
        }

        [Test]
        public void ZeroStartSpeedMakesRandomDirectionInactive()
        {
            var prefabPath = CreatePrefab("InactiveRandomDirection", system =>
            {
                var shape = system.shape;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radiusThickness = 0;
                shape.randomDirectionAmount = 1;
                var main = system.main;
                main.startSpeed = 0;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("shape.randomDirectionAmount.zeroStartSpeed", report);
            StringAssert.DoesNotContain("\"randomDirectionAmount\":1", json);
            StringAssert.DoesNotContain("\"type\":\"ChangeEmitDirection\"", json);
        }

        [Test]
        public void EnabledZeroForceOverLifetimeIsInactive()
        {
            var prefabPath = CreatePrefab("InactiveForce", system =>
            {
                var force = system.forceOverLifetime;
                force.enabled = true;
                force.space = ParticleSystemSimulationSpace.World;
                force.x = 0;
                force.y = 0;
                force.z = 0;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("forceOverLifetime", report);
            StringAssert.DoesNotContain("forceOverLifetime.mixedSimulationSpace", report);
            StringAssert.DoesNotContain("\"type\":\"ForceOverLife\"", json);
        }

        [Test]
        public void PartialRandomDirectionAmount_UsesRuntimeRandomUnitLerp()
        {
            var prefabPath = CreatePrefab("RandomDirection", system =>
            {
                var shape = system.shape;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.randomDirectionAmount = 0.5f;
                var main = system.main;
                main.startSpeed = 2;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"randomDirection\":{\"mode\":\"lerpRandomUnit\",\"amount\":0.5}", json);
            StringAssert.DoesNotContain("\"type\":\"ChangeEmitDirection\"", json);
            StringAssert.Contains("shape.randomDirectionAmount.randomUnitLerpRuntime", report);
            StringAssert.Contains("shape.randomDirectionAmount.stockShapeDirectionFallback", report);
            StringAssert.Contains("lerped toward a random unit vector", report);
        }

        [Test]
        public void PointLights_ExportPairedRuntimeSemanticsWithoutBlockingStrict()
        {
            var prefabPath = CreatePrefab("PointLights", system =>
            {
                var light = system.gameObject.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = new Color(0.25f, 0.5f, 0.75f, 1);
                light.intensity = 3;
                light.range = 10;
                light.cullingMask = 5;
                light.shadows = LightShadows.Soft;

                var lights = system.lights;
                lights.enabled = true;
                lights.light = light;
                lights.ratio = 0.5f;
                lights.useRandomDistribution = false;
                lights.useParticleColor = false;
                lights.sizeAffectsRange = true;
                lights.alphaAffectsIntensity = true;
                lights.maxLights = 5;
                lights.range = new ParticleSystem.MinMaxCurve(2);
                lights.intensity = new ParticleSystem.MinMaxCurve(0.5f);
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"schemaVersion\":\"unity_particle_quarks_exporter.lights.v1\"", json);
            StringAssert.Contains("\"ratio\":0.5", json);
            StringAssert.Contains("\"randomDistribution\":false", json);
            StringAssert.Contains("\"useParticleColor\":false", json);
            StringAssert.Contains("\"sizeAffectsRange\":true", json);
            StringAssert.Contains("\"maxLights\":5", json);
            StringAssert.Contains("\"renderScaleMode\":", json);
            StringAssert.Contains("\"sourceRenderScale\":", json);
            StringAssert.Contains("\"particleColorMultiplier\":", json);
            StringAssert.Contains("\"color\":{\"r\":0.25,\"g\":0.5,\"b\":0.75,\"a\":1}", json);
            StringAssert.Contains("\"shadowMode\":\"soft\"", json);
            StringAssert.Contains("lights.point.runtime", report);
            StringAssert.Contains("lights.attenuation.threePointLightFallback", report);
            StringAssert.Contains("lights.shadows.threePointShadowFallback", report);
            StringAssert.DoesNotContain("lights.omittedFallback", report);
        }

        [Test]
        public void PointLights_PreserveControlEmitterWhenRendererIsNone()
        {
            var prefabPath = CreatePrefab("PointLightControlEmitter", system =>
            {
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.None;
                renderer.sharedMaterial = null;
                var light = system.gameObject.AddComponent<Light>();
                light.type = LightType.Point;
                var lights = system.lights;
                lights.enabled = true;
                lights.light = light;
                lights.ratio = 1;
                // Unity's default maxLights is zero in the isolated test
                // projects, which makes the module inactive rather than a
                // real control-emitter case.
                lights.maxLights = 1;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.Contains("\"type\":\"ParticleEmitter\"", json);
            StringAssert.Contains("unity_particle_quarks_exporter.lights.v1", json);
            StringAssert.Contains("material.shaderProfile.transparentControl", report);
            StringAssert.Contains("renderer.controlEmitter.particleColorRuntime", report);
            StringAssert.Contains("renderer.invisible", report);
        }

        [Test]
        public void InactiveLights_DoNotBlockStrictOrEmitRuntimeMetadata()
        {
            var prefabPath = CreatePrefab("InactiveLights", system =>
            {
                var lights = system.lights;
                lights.enabled = true;
                lights.light = null;
                lights.ratio = 1;
            });
            WriteConfig(prefabPath, "strict");

            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var json = File.ReadAllText(Path.Combine(outputRoot, "fixture", "effect.quarks.json"));
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));

            Assert.That(manifest.effects.Single().status, Is.EqualTo("ready"));
            StringAssert.DoesNotContain("unity_particle_quarks_exporter.lights.v1", json);
            StringAssert.Contains("lights.missingLightPrefab", report);
            StringAssert.DoesNotContain("lights.omittedFallback", report);
        }

        [Test]
        public void NonPointLights_ReportSpecificStrictBlocker()
        {
            var prefabPath = CreatePrefab("SpotLights", system =>
            {
                var light = system.gameObject.AddComponent<Light>();
                light.type = LightType.Spot;
                var lights = system.lights;
                lights.enabled = true;
                lights.light = light;
                lights.ratio = 1;
            });

            WriteConfig(prefabPath, "best-effort");
            var manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, true);
            var report = File.ReadAllText(Path.Combine(outputRoot, "fixture", "conversion-report.json"));
            Assert.That(manifest.effects.Single().status, Is.EqualTo("partial"));
            Assert.That(File.Exists(Path.Combine(outputRoot, "runtime-manifest.json")), Is.True);
            StringAssert.Contains("lights.lightType.Spot", report);
            StringAssert.Contains("lights.lightType.Spot.omittedFallback", report);

            WriteConfig(prefabPath, "strict");
            manifest = UnityParticleQuarksExportBatchmode.ExportConfig(configPath, false);
            Assert.That(manifest.effects.Single().status, Is.EqualTo("failed"));
            Assert.That(File.Exists(Path.Combine(outputRoot, "runtime-manifest.json")), Is.False);
        }

        private string CreatePrefab(string name, Action<ParticleSystem> configure)
        {
            var root = new GameObject(name);
            var system = AddParticleSystem(root);
            configure(system);
            var path = FixtureRoot + "/" + name + ".prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);
            return path;
        }

        private static Mesh CreateShapeMesh(string name, bool withColors)
        {
            var mesh = new Mesh { name = name };
            mesh.vertices = new[]
            {
                new Vector3(0, 0, 0),
                new Vector3(2, 0, 0),
                new Vector3(0, 3, 0)
            };
            mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward };
            mesh.triangles = new[] { 0, 1, 2 };
            if (withColors) mesh.colors = new[] { Color.red, Color.green, Color.blue };
            AssetDatabase.CreateAsset(mesh, FixtureRoot + "/" + name + ".asset");
            return mesh;
        }

        private ParticleSystem AddParticleSystem(GameObject target)
        {
            var system = target.AddComponent<ParticleSystem>();
            var renderer = target.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = fixtureMaterial;
            renderer.mesh = fixtureRendererMesh;
            return system;
        }

        private static UnityParticleQuarksEffectRequest EffectRequest(string id, string prefabPath)
        {
            return new UnityParticleQuarksEffectRequest
            {
                id = id,
                prefabPath = prefabPath,
                includeParticleSystemPaths = Array.Empty<string>(),
                excludeParticleSystemPaths = Array.Empty<string>()
            };
        }

        private void WriteConfig(
            string prefabPath,
            string mode,
            string target = "default",
            string sourceRenderPipeline = "",
            string runtimeProfile = "extended",
            string unknownCustomShaderPolicy = "require-profile",
            string schemaVersion = "unity_particle_quarks_pipeline.config.v1")
        {
            var config = new UnityParticleQuarksPipelineConfig
            {
                schemaVersion = schemaVersion,
                outputRoot = outputRoot,
                mode = mode,
                runtimeProfile = runtimeProfile,
                unknownCustomShaderPolicy = unknownCustomShaderPolicy,
                target = target,
                sourceRenderPipeline = sourceRenderPipeline,
                maxTextureSize = 256,
                effects = new[]
                {
                    new UnityParticleQuarksEffectRequest
                    {
                        id = "fixture",
                        prefabPath = prefabPath,
                        includeParticleSystemPaths = Array.Empty<string>(),
                        excludeParticleSystemPaths = Array.Empty<string>()
                    }
                }
            };
            File.WriteAllText(configPath, JsonUtility.ToJson(config, true));
        }

        private static int Occurrences(string value, string marker)
        {
            var count = 0;
            var offset = 0;
            while ((offset = value.IndexOf(marker, offset, StringComparison.Ordinal)) >= 0)
            {
                count++;
                offset += marker.Length;
            }
            return count;
        }

        private static float EmitterLinearDeterminant(string json)
        {
            var values = EmitterMatrix(json);
            return values[0] * (values[5] * values[10] - values[9] * values[6]) -
                   values[4] * (values[1] * values[10] - values[9] * values[2]) +
                   values[8] * (values[1] * values[6] - values[5] * values[2]);
        }

        private static float[] EmitterMatrix(string json)
        {
            var match = Regex.Match(
                json,
                "\\\"type\\\":\\\"ParticleEmitter\\\".*?\\\"matrix\\\":\\[(?<matrix>[^\\]]+)\\]",
                RegexOptions.Singleline);
            Assert.That(match.Success, Is.True, "ParticleEmitter matrix is required.");
            var values = match.Groups["matrix"].Value.Split(',')
                .Select(value => float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture))
                .ToArray();
            Assert.That(values.Length, Is.EqualTo(16));
            return values;
        }

        private static float[] JsonMatrix(string json, string field)
        {
            var match = Regex.Match(
                json,
                "\\\"" + Regex.Escape(field) + "\\\":\\[(?<matrix>[^\\]]+)\\]",
                RegexOptions.Singleline);
            Assert.That(match.Success, Is.True, field + " matrix is required.");
            var values = match.Groups["matrix"].Value.Split(',')
                .Select(value => float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture))
                .ToArray();
            Assert.That(values.Length, Is.EqualTo(16));
            return values;
        }
    }
}
