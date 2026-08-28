using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityParticleQuarksExporter.Editor;

namespace UnityParticleQuarksExporter.Editor.Tests
{
    public sealed class ShaderProfileTests
    {
        [Test]
        public void SupportedSingleNameProfilesExposeStableIds()
        {
            var profiles = ShaderProfileRegistry.All
                .Where(profile => profile.IsSupported && profile.ShaderNames.Count == 1)
                .ToArray();

            Assert.That(profiles, Is.Not.Empty);
            foreach (var profile in profiles)
            {
                Assert.That(profile.GetProfileId(null), Is.Not.Empty, profile.Name);
                Assert.That(profile.MatchesShaderName(profile.ShaderNames[0]), Is.True, profile.Name);
            }
        }

        [Test]
        public void NewShaderProfileSubclassUsesTheOneFileExtensionContract()
        {
            var profile = new TestParticlesUnlitShaderProfile();
            var diagnostics = new ConversionDiagnostics();
            var context = new ShaderProfileMaterialContext(null, diagnostics, true);

            Assert.That(profile.MatchesShaderName("Tests/Particles/Unlit"), Is.True);
            Assert.That(profile.GetProfileId(null), Is.EqualTo("tests.particlesUnlit"));
            Assert.That(profile.ConversionKind, Is.EqualTo(ShaderProfileConversionKind.UnlitParticle));
            Assert.That(profile.ConsumesParticleColor, Is.True);

            profile.ConfigureMaterial(context);

            Assert.That(context.shaderParametersOverride, Is.Not.Null);
            Assert.That(context.shaderParametersOverride.ToString(), Does.Contain("tests.particlesUnlit"));
        }

        private sealed class TestParticlesUnlitShaderProfile : ShaderProfile
        {
            private static readonly string[] Names = { "Tests/Particles/Unlit" };

            public override string Name => "TestParticlesUnlit";
            public override string DiagnosticId => "tests.particlesUnlit";
            public override IReadOnlyList<string> ShaderNames => Names;
            public override ShaderProfileConversionKind ConversionKind => ShaderProfileConversionKind.UnlitParticle;
            public override bool ConsumesParticleColor => true;
            public override bool FixedTransparent => true;
            public override string GetProfileId(Material material) => "tests.particlesUnlit";

            public override void ConfigureMaterial(ShaderProfileMaterialContext context)
            {
                context.shaderParametersOverride = Json.Object()
                    .Add("schemaVersion", Json.String("tests.shader_profile.v1"))
                    .Add("profile", Json.String("tests.particlesUnlit"));
            }
        }
    }
}
