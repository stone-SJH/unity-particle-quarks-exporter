using System.Collections.Generic;
using UnityEngine;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class SyntyGenericBasicShaderProfile : ShaderProfile
    {
        private static readonly string[] Names = { "Synty/Generic_Basic" };
        public override string Name => "SyntyGenericBasic";
        public override string DiagnosticId => "synty.genericBasic";
        public override IReadOnlyList<string> ShaderNames => Names;
        public override bool UsesLitMaterial => true;
        public override bool UsesSyntyPipelineCull => true;
        public override string UnlitNormalMapProperty => "_Normal_Map";
        public override IReadOnlyList<string> PreferredMainTextureProperties => new[] { "_Albedo_Map" };

        public override ShaderProfileLitMapSettings GetLitMapSettings(Material material)
        {
            return new ShaderProfileLitMapSettings
            {
                normalMapProperty = "_Normal_Map",
                normalScaleProperty = "_Normal_Amount",
                emissionMapProperty = "_Emission_Map",
                emissionMapActive = IsEnabled(material, "_Enable_Emission")
            };
        }

        public override void ConfigureMaterial(ShaderProfileMaterialContext context)
        {
            if (context.material == null ||
                !IsEnabled(context.material, "_Enable_Emission") ||
                !context.material.HasProperty("_Emission_Color")) return;
            context.materialEmissionOverride = context.material.GetColor("_Emission_Color");
            context.diagnostics.mapped.Add("material.emissive.syntyGeneric");
        }
    }
}
