using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityParticleQuarksExporter.Editor
{
    internal static class ShaderProfileRegistry
    {
        private static readonly ShaderProfile TransparentControl = new TransparentControlShaderProfile();
        private static readonly ShaderProfile Unsupported = new UnsupportedShaderProfile();

        private static readonly ShaderProfile[] Profiles =
        {
            new BuiltInParticleVertexLitShaderProfile(),
            new BuiltInParticleStandardLitShaderProfile(),
            new BuiltInStandardMetallicShaderProfile(),
            new BuiltInStandardSpecularShaderProfile(),
            new SpriteShaderProfile(),
            new BuiltInUnlitNoVertexColorShaderProfile(),
            new BuiltInParticleAnimAlphaBlendedShaderProfile(),
            new BuiltInParticleUnlitShaderProfile(),
            new SyntyGenericParticlesUnlitShaderProfile(),
            new SyntyGenericParticlesLitShaderProfile(),
            new SyntyGenericBasicShaderProfile(),
            new UrpParticleUnlitShaderProfile(),
            new UrpParticleLitShaderProfile(),
            new UrpParticleSimpleLitShaderProfile(),
            new UrpUnlitShaderProfile(),
            new UrpLitShaderProfile(),
            new UrpSimpleLitShaderProfile(),
            new HdrpUnlitShaderProfile(),
            new HdrpLitShaderProfile(),
            new CustomHovlParticlesShaderProfile(),
            new CustomPilotoUberFxsgShaderProfile(),
            new CustomVehicleEffectShaderProfile(),
            new CustomShaderGraphRockDissolveShaderProfile(),
            new CustomShaderGraphParticleShaderProfile()
        };

        static ShaderProfileRegistry()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var profile in Profiles)
            {
                if (string.IsNullOrWhiteSpace(profile.Name))
                    throw new InvalidOperationException("A shader profile is missing its registry name.");
                foreach (var shaderName in profile.ShaderNames)
                {
                    if (string.IsNullOrWhiteSpace(shaderName))
                        throw new InvalidOperationException("Shader profile " + profile.Name + " contains an empty shader name.");
                    if (!names.Add(shaderName))
                        throw new InvalidOperationException("Shader name is registered by more than one profile: " + shaderName);
                }
            }
        }

        public static IReadOnlyList<ShaderProfile> All => Profiles;

        public static ShaderProfile Resolve(Material material)
        {
            return material == null
                ? TransparentControl
                : ResolveShaderName(material.shader == null ? string.Empty : material.shader.name);
        }

        public static ShaderProfile ResolveShaderName(string shaderName)
        {
            foreach (var profile in Profiles)
            {
                if (profile.MatchesShaderName(shaderName)) return profile;
            }
            return Unsupported;
        }
    }
}
