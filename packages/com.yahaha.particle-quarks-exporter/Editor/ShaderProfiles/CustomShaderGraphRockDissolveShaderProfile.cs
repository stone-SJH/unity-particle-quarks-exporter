using System.Collections.Generic;
using UnityEngine;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class CustomShaderGraphRockDissolveShaderProfile : ShaderProfile
    {
        private static readonly string[] Names = { "Shader Graphs/Fx_RockDissolve" };
        private static readonly string[] TextureProperties =
        {
            "_MainTex", "_BaseMap", "Texture2D_F593E37E", "Texture2D_EDA87E5",
            "Texture2D_FF0A21CE", "_SampleTexture2D_A2DE9010_Texture_1",
            "_SampleTexture2D_1F6DF57E_Texture_1"
        };
        public override string Name => "CustomShaderGraphRockDissolve";
        public override string DiagnosticId => "custom.shadergraph.rockDissolve";
        public override IReadOnlyList<string> ShaderNames => Names;
        public override bool ConsumesParticleColor => true;
        public override bool FixedTransparent => true;
        public override bool CustomParticle => true;
        public override ShaderProfileConversionKind ConversionKind => ShaderProfileConversionKind.UnlitParticle;
        public override IReadOnlyList<string> PreferredMainTextureProperties => TextureProperties;
        public override string GetProfileId(Material material) => "custom.shadergraph.rockDissolve";
        public override string[] GetPropertyAliases(Material material) => new[] { "Texture2D_EDA87E5" };

        public override void ConfigureMaterial(ShaderProfileMaterialContext context)
        {
            context.shaderParametersOverride = Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.material.shader_parameters.v1"))
                .Add("profile", Json.String("custom.shadergraph.rockDissolve"))
                .Add("colorOperation", Json.String("rockDissolveVertexCustomDataLerp"))
                .Add("alphaOperation", Json.String("rockDissolveClip"));
            context.diagnostics.mapped.Add("material.shaderParameters.rockDissolve.v1.graphEvaluator");
        }

        public override JsonObject BuildParticleCustomDataMetadata(
            ParticleSystem system,
            ConversionDiagnostics diagnostics)
        {
            if (system == null) return null;
            var module = system.customData;
            if (!module.enabled ||
                module.GetMode(ParticleSystemCustomData.Custom1) != ParticleSystemCustomDataMode.Vector ||
                module.GetVectorComponentCount(ParticleSystemCustomData.Custom1) < 1)
            {
                diagnostics.unsupported.Add("material.shaderProfile.custom.shadergraph.rockDissolve.customDataContract");
                diagnostics.approximated.Add("material.shaderProfile.custom.shadergraph.rockDissolve.genericTextureFallback");
                diagnostics.warnings.Add("Fx_RockDissolve requires the authored Custom1 Vector stream. The dedicated profile cannot evaluate dissolve without it.");
                return null;
            }

            var custom2Mode = module.GetMode(ParticleSystemCustomData.Custom2);
            if (custom2Mode != ParticleSystemCustomDataMode.Color &&
                custom2Mode != ParticleSystemCustomDataMode.Disabled)
            {
                diagnostics.unsupported.Add("material.shaderProfile.custom.shadergraph.rockDissolve.custom2Mode");
                diagnostics.approximated.Add("material.shaderProfile.custom.shadergraph.rockDissolve.genericTextureFallback");
                diagnostics.warnings.Add("Fx_RockDissolve Custom2 must be an authored Color stream or disabled so the graph receives its zero-vector default.");
                return null;
            }

            var custom1 = Json.Array();
            var componentCount = Mathf.Clamp(
                module.GetVectorComponentCount(ParticleSystemCustomData.Custom1),
                1,
                4);
            for (var index = 0; index < 4; index++)
            {
                custom1.Add(index < componentCount
                    ? QuarksParticleSemanticsUtility.VelocityCurveMetadata(module.GetVector(ParticleSystemCustomData.Custom1, index), diagnostics, "customData.custom1." + index)
                    : QuarksParticleSemanticsUtility.VelocityCurveMetadata(new ParticleSystem.MinMaxCurve(0), diagnostics, "customData.custom1." + index));
            }

            diagnostics.mapped.Add("material.shaderParameters.rockDissolve.v1");
            diagnostics.mapped.Add("material.shaderParameters.rockDissolve.custom1Vector");
            diagnostics.mapped.Add(custom2Mode == ParticleSystemCustomDataMode.Color
                ? "material.shaderParameters.rockDissolve.custom2Color"
                : "material.shaderParameters.rockDissolve.custom2DisabledZeroDefault");
            return Json.Object()
                .Add("schemaVersion", Json.String("unity_particle_quarks_exporter.custom_data.v1"))
                .Add("custom1", Json.Object()
                    .Add("mode", Json.String("vector"))
                    .Add("components", custom1))
                .Add("custom2", Json.Object()
                    .Add("mode", Json.String("color"))
                    .Add("value", custom2Mode == ParticleSystemCustomDataMode.Color
                        ? QuarksParticleSemanticsUtility.Gradient(module.GetColor(ParticleSystemCustomData.Custom2), diagnostics, "customData.custom2")
                        : QuarksParticleSemanticsUtility.ConstantColor(Color.clear)));
        }
    }
}
