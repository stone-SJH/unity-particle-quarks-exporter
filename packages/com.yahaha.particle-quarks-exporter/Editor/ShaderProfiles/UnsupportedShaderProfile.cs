namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class UnsupportedShaderProfile : ShaderProfile
    {
        public override string Name => "Unsupported";
        public override string DiagnosticId => string.Empty;
        public override bool IsSupported => false;
        public override bool ConsumesParticleColor => true;
    }
}
