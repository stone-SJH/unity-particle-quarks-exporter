using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityParticleQuarksExporter.Editor
{
    internal sealed class QuarksExportResult
    {
        public string json;
        public string[] textures;
        public UnityParticleQuarksParticleSystemReport[] reports;
        public bool hasUnsupported;
        public bool hasProfileGaps;
        public bool hasFatalUnsupported;
        public string[] fatalUnsupported;
        public string runtimeTier;
        public int emitterCount;
    }

    internal sealed class ConversionDiagnostics
    {
        public readonly SortedSet<string> mapped = new SortedSet<string>(StringComparer.Ordinal);
        public readonly SortedSet<string> approximated = new SortedSet<string>(StringComparer.Ordinal);
        public readonly SortedSet<string> unsupported = new SortedSet<string>(StringComparer.Ordinal);
        public readonly SortedSet<string> fatalUnsupported = new SortedSet<string>(StringComparer.Ordinal);
        public readonly SortedSet<string> nonBlockingUnsupported = new SortedSet<string>(StringComparer.Ordinal);
        public readonly SortedSet<string> inactive = new SortedSet<string>(StringComparer.Ordinal);
        public readonly List<UnityParticleQuarksShaderResolutionFailure> shaderResolutionFailures = new List<UnityParticleQuarksShaderResolutionFailure>();
        public readonly List<UnityParticleQuarksShaderProfileGap> shaderProfileGaps = new List<UnityParticleQuarksShaderProfileGap>();
        public readonly List<UnityParticleQuarksMaterialProfileReport> materialProfiles = new List<UnityParticleQuarksMaterialProfileReport>();
        public readonly SortedSet<string> warnings = new SortedSet<string>(StringComparer.Ordinal);
        public bool requiresPairedRuntime;

        public UnityParticleQuarksParticleSystemReport ToReport(string path)
        {
            return new UnityParticleQuarksParticleSystemReport
            {
                path = path,
                status = shaderProfileGaps.Count > 0
                    ? "profile_required"
                    : unsupported.Count > 0 ? "partial" : "ready",
                runtimeTier = requiresPairedRuntime ? "paired" : "stock",
                mapped = mapped.ToArray(),
                approximated = approximated.ToArray(),
                unsupported = unsupported.ToArray(),
                fatalUnsupported = fatalUnsupported.ToArray(),
                nonBlockingUnsupported = nonBlockingUnsupported.ToArray(),
                inactive = inactive.ToArray(),
                shaderResolutionFailures = shaderResolutionFailures
                    .OrderBy(item => item.materialAssetPath, StringComparer.Ordinal)
                    .ThenBy(item => item.materialSlot, StringComparer.Ordinal)
                    .ThenBy(item => item.materialName, StringComparer.Ordinal)
                    .ToArray(),
                shaderProfileGaps = shaderProfileGaps
                    .OrderBy(item => item.shaderFingerprint, StringComparer.Ordinal)
                    .ThenBy(item => item.materialAssetPath, StringComparer.Ordinal)
                    .ThenBy(item => item.materialSlot, StringComparer.Ordinal)
                    .ToArray(),
                materialProfiles = materialProfiles
                    .OrderBy(item => item.materialAssetPath, StringComparer.Ordinal)
                    .ThenBy(item => item.materialSlot, StringComparer.Ordinal)
                    .ThenBy(item => item.materialName, StringComparer.Ordinal)
                    .ToArray(),
                warnings = warnings.ToArray()
            };
        }
    }
}
