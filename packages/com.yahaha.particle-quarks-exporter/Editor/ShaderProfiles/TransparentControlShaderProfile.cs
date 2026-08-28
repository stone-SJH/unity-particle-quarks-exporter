namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class TransparentControlShaderProfile : ShaderProfile
    {
        public override string Name => "TransparentControl";
        public override string DiagnosticId => "transparentControl";
        public override bool DoubleSidedByDefault => true;
        public override string GetProfileId(UnityEngine.Material material) => string.Empty;
    }
}
