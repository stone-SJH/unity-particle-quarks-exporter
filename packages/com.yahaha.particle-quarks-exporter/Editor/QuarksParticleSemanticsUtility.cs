using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UnityParticleQuarksExporter.Editor
{
    internal static class QuarksParticleSemanticsUtility
    {
        internal static JsonValue Curve(ParticleSystem.MinMaxCurve curve, ConversionDiagnostics diagnostics, string field)
        {
            switch (curve.mode)
            {
                case ParticleSystemCurveMode.Constant:
                    return Constant(curve.constant);
                case ParticleSystemCurveMode.TwoConstants:
                    return Interval(curve.constantMin, curve.constantMax);
                case ParticleSystemCurveMode.Curve:
                    DiagnoseEmptyCurve(curve.curve, field, diagnostics);
                    return AnimationCurveJson(curve.curve, curve.curveMultiplier);
                case ParticleSystemCurveMode.TwoCurves:
                    diagnostics.unsupported.Add(field + ".twoCurves");
                    diagnostics.approximated.Add(field + ".twoCurvesMean");
                    diagnostics.warnings.Add(field + " uses a per-particle random blend between two curves; stock Quarks has no random-between-curves scalar generator, so best-effort emits their arithmetic mean curve.");
                    DiagnoseEmptyCurvePair(curve.curveMin, curve.curveMax, field, diagnostics);
                    return AveragedAnimationCurveJson(curve.curveMin, curve.curveMax, curve.curveMultiplier);
                default:
                    diagnostics.unsupported.Add(field + ".unknownCurveMode");
                    diagnostics.approximated.Add(field + ".unknownCurveMode.zeroFallback");
                    diagnostics.warnings.Add(field + " uses an unknown Unity curve mode. Best-effort explicitly emits constant zero; strict export fails.");
                    return Constant(0);
            }
        }

        internal static JsonValue LimitVelocityStockFallback(
            ParticleSystem.MinMaxCurve curve,
            ConversionDiagnostics diagnostics)
        {
            if (curve.mode != ParticleSystemCurveMode.TwoCurves)
            {
                return Curve(curve, diagnostics, "limitVelocityOverLifetime.limit");
            }
            return AveragedAnimationCurveJson(curve.curveMin, curve.curveMax, curve.curveMultiplier);
        }

        internal static JsonValue TrailWidthCurve(
            ParticleSystem.MinMaxCurve curve,
            ConversionDiagnostics diagnostics,
            string field)
        {
            if (curve.mode != ParticleSystemCurveMode.TwoCurves)
            {
                return Curve(curve, diagnostics, field);
            }

            diagnostics.unsupported.Add(field + ".twoCurves");
            diagnostics.approximated.Add(field + ".twoCurvesMean");
            diagnostics.warnings.Add(field + " uses a per-particle random blend between two curves; stock Quarks has no random-between-curves generator, so best-effort emits their arithmetic mean curve.");
            DiagnoseEmptyCurvePair(curve.curveMin, curve.curveMax, field, diagnostics);
            return AveragedAnimationCurveJson(curve.curveMin, curve.curveMax, curve.curveMultiplier);
        }

        internal static JsonValue SizeOverLifetimeStockFallback(
            ParticleSystem.SizeOverLifetimeModule size,
            ConversionDiagnostics diagnostics)
        {
            if (!size.separateAxes)
            {
                return SizeCurveStockFallback(size.size, diagnostics, "sizeOverLifetime.size");
            }
            return Json.Object().Add("type", Json.String("Vector3Function"))
                .Add("x", SizeCurveStockFallback(size.x, diagnostics, "sizeOverLifetime.x"))
                .Add("y", SizeCurveStockFallback(size.y, diagnostics, "sizeOverLifetime.y"))
                .Add("z", SizeCurveStockFallback(size.z, diagnostics, "sizeOverLifetime.z"));
        }

        internal static JsonValue SizeCurveStockFallback(
            ParticleSystem.MinMaxCurve curve,
            ConversionDiagnostics diagnostics,
            string field)
        {
            switch (curve.mode)
            {
                case ParticleSystemCurveMode.Constant:
                    return Constant(curve.constant);
                case ParticleSystemCurveMode.TwoConstants:
                    return Interval(curve.constantMin, curve.constantMax);
                case ParticleSystemCurveMode.Curve:
                    DiagnoseEmptyCurve(curve.curve, field, diagnostics);
                    return AnimationCurveJson(curve.curve, curve.curveMultiplier);
                case ParticleSystemCurveMode.TwoCurves:
                    DiagnoseEmptyCurvePair(curve.curveMin, curve.curveMax, field, diagnostics);
                    return AveragedAnimationCurveJson(curve.curveMin, curve.curveMax, curve.curveMultiplier);
                default:
                    diagnostics.unsupported.Add(field + ".unknownCurveMode");
                    diagnostics.approximated.Add(field + ".unknownCurveMode.zeroFallback");
                    diagnostics.warnings.Add(field + " uses an unknown Unity curve mode. Best-effort explicitly emits constant zero; strict export fails.");
                    return Constant(0);
            }
        }

        internal static JsonValue TrailHeadWidth(
            ParticleSystem.MinMaxCurve curve,
            ConversionDiagnostics diagnostics,
            string field)
        {
            switch (curve.mode)
            {
                case ParticleSystemCurveMode.Constant:
                    return Constant(curve.constant);
                case ParticleSystemCurveMode.TwoConstants:
                    return Interval(curve.constantMin, curve.constantMax);
                case ParticleSystemCurveMode.Curve:
                    DiagnoseEmptyCurve(curve.curve, field, diagnostics);
                    return Constant((curve.curve == null ? 0 : curve.curve.Evaluate(0)) * curve.curveMultiplier);
                case ParticleSystemCurveMode.TwoCurves:
                    DiagnoseEmptyCurvePair(curve.curveMin, curve.curveMax, field, diagnostics);
                    var minimum = curve.curveMin == null ? 0 : curve.curveMin.Evaluate(0);
                    var maximum = curve.curveMax == null ? 0 : curve.curveMax.Evaluate(0);
                    return Constant((minimum + maximum) * 0.5f * curve.curveMultiplier);
                default:
                    diagnostics.unsupported.Add(field + ".unknownCurveMode");
                    diagnostics.approximated.Add(field + ".unknownCurveMode.zeroFallback");
                    diagnostics.warnings.Add(field + " uses an unknown Unity curve mode. Best-effort explicitly emits constant zero; strict export fails.");
                    return Constant(0);
            }
        }

        internal static JsonValue NegatedCurve(ParticleSystem.MinMaxCurve curve, ConversionDiagnostics diagnostics, string field)
        {
            return ScaleCurve(curve, -1, diagnostics, field);
        }

        internal static JsonValue ScaleCurve(ParticleSystem.MinMaxCurve curve, float scale, ConversionDiagnostics diagnostics, string field)
        {
            switch (curve.mode)
            {
                case ParticleSystemCurveMode.Constant: return Constant(curve.constant * scale);
                case ParticleSystemCurveMode.TwoConstants: return Interval(curve.constantMin * scale, curve.constantMax * scale);
                case ParticleSystemCurveMode.Curve:
                    DiagnoseEmptyCurve(curve.curve, field, diagnostics);
                    return AnimationCurveJson(curve.curve, curve.curveMultiplier * scale);
                case ParticleSystemCurveMode.TwoCurves:
                    diagnostics.unsupported.Add(field + ".twoCurves");
                    diagnostics.approximated.Add(field + ".twoCurvesMean");
                    diagnostics.warnings.Add(field + " uses a per-particle random blend between two curves; stock Quarks has no random-between-curves scalar generator, so best-effort emits their arithmetic mean curve.");
                    DiagnoseEmptyCurvePair(curve.curveMin, curve.curveMax, field, diagnostics);
                    return AveragedAnimationCurveJson(curve.curveMin, curve.curveMax, curve.curveMultiplier * scale);
                default:
                    diagnostics.unsupported.Add(field + ".unknownCurveMode");
                    diagnostics.approximated.Add(field + ".unknownCurveMode.zeroFallback");
                    diagnostics.warnings.Add(field + " uses an unknown Unity curve mode. Best-effort explicitly emits constant zero; strict export fails.");
                    return Constant(0);
            }
        }

        internal static JsonValue AnimationCurveJson(AnimationCurve curve, float multiplier)
        {
            var keys = curve == null ? Array.Empty<Keyframe>() : curve.keys;
            if (keys.Length == 0) return Constant(0);
            if (keys.Length == 1) return Constant(keys[0].value * multiplier);
            var bounded = new List<Keyframe>();
            var hasZeroKey = keys.Any(key => Mathf.Abs(key.time) <= 0.000001f);
            var zeroKey = hasZeroKey ? keys.First(key => Mathf.Abs(key.time) <= 0.000001f) : default(Keyframe);
            bounded.Add(hasZeroKey
                ? new Keyframe(0, zeroKey.value, zeroKey.inTangent, zeroKey.outTangent)
                : new Keyframe(0, curve.Evaluate(0), AnimationCurveSlope(curve, 0, true), AnimationCurveSlope(curve, 0, false)));
            bounded.AddRange(keys.Where(key => key.time > 0.000001f && key.time < 0.999999f));
            var hasOneKey = keys.Any(key => Mathf.Abs(key.time - 1) <= 0.000001f);
            var oneKey = hasOneKey ? keys.First(key => Mathf.Abs(key.time - 1) <= 0.000001f) : default(Keyframe);
            bounded.Add(hasOneKey
                ? new Keyframe(1, oneKey.value, oneKey.inTangent, oneKey.outTangent)
                : new Keyframe(1, curve.Evaluate(1), AnimationCurveSlope(curve, 1, true), AnimationCurveSlope(curve, 1, false)));
            var functions = Json.Array();
            for (var index = 0; index + 1 < bounded.Count; index++)
            {
                var left = bounded[index];
                var right = bounded[index + 1];
                var delta = right.time - left.time;
                if (delta <= 0.000001f) continue;
                var constantExtension = right.time <= keys[0].time + 0.000001f ||
                                        left.time >= keys[keys.Length - 1].time - 0.000001f;
                var leftOutTangent = constantExtension ? 0 : left.outTangent;
                var rightInTangent = constantExtension ? 0 : right.inTangent;
                var p0 = left.value * multiplier;
                var p1 = (left.value + leftOutTangent * delta / 3f) * multiplier;
                var p2 = (right.value - rightInTangent * delta / 3f) * multiplier;
                var p3 = right.value * multiplier;
                functions.Add(Json.Object()
                    .Add("function", Json.Object()
                        .Add("p0", Json.Number(Finite(p0)))
                        .Add("p1", Json.Number(Finite(p1)))
                        .Add("p2", Json.Number(Finite(p2)))
                        .Add("p3", Json.Number(Finite(p3))))
                    .Add("start", Json.Number(left.time)));
            }
            return Json.Object().Add("type", Json.String("PiecewiseBezier")).Add("functions", functions);
        }

        internal static JsonObject VelocityCurveMetadata(
            ParticleSystem.MinMaxCurve curve,
            ConversionDiagnostics diagnostics,
            string field)
        {
            switch (curve.mode)
            {
                case ParticleSystemCurveMode.Constant:
                    return Json.Object()
                        .Add("mode", Json.String("constant"))
                        .Add("value", Constant(curve.constant));
                case ParticleSystemCurveMode.TwoConstants:
                    return Json.Object()
                        .Add("mode", Json.String("twoConstants"))
                        .Add("value", Interval(curve.constantMin, curve.constantMax));
                case ParticleSystemCurveMode.Curve:
                    DiagnoseEmptyCurve(curve.curve, field, diagnostics);
                    return Json.Object()
                        .Add("mode", Json.String("curve"))
                        .Add("value", AnimationCurveJson(curve.curve, curve.curveMultiplier));
                case ParticleSystemCurveMode.TwoCurves:
                    DiagnoseEmptyCurvePair(curve.curveMin, curve.curveMax, field, diagnostics);
                    return Json.Object()
                        .Add("mode", Json.String("twoCurves"))
                        .Add("minimum", AnimationCurveJson(curve.curveMin, curve.curveMultiplier))
                        .Add("maximum", AnimationCurveJson(curve.curveMax, curve.curveMultiplier));
                default:
                    diagnostics.unsupported.Add(field + ".unknownCurveMode");
                    diagnostics.approximated.Add(field + ".unknownCurveMode.zeroFallback");
                    diagnostics.warnings.Add(field + " uses an unknown Unity curve mode. Best-effort explicitly emits constant zero; strict export fails.");
                    return Json.Object()
                        .Add("mode", Json.String("constant"))
                        .Add("value", Constant(0));
            }
        }

        internal static void DiagnoseEmptyCurve(
            AnimationCurve curve,
            string field,
            ConversionDiagnostics diagnostics)
        {
            if (curve != null && curve.length > 0) return;
            diagnostics.unsupported.Add(field + ".emptyCurve");
            diagnostics.approximated.Add(field + ".emptyCurve.zeroFallback");
            diagnostics.warnings.Add(field + " selects Curve mode but contains no keys. Best-effort explicitly emits constant zero; strict export fails.");
        }

        internal static void DiagnoseEmptyCurvePair(
            AnimationCurve minimum,
            AnimationCurve maximum,
            string field,
            ConversionDiagnostics diagnostics)
        {
            if (minimum != null && minimum.length > 0 && maximum != null && maximum.length > 0) return;
            diagnostics.unsupported.Add(field + ".twoCurvesMissingKeys");
            diagnostics.approximated.Add(field + ".twoCurvesMissingKeys.zeroBranchFallback");
            diagnostics.warnings.Add(field + " selects TwoCurves but at least one curve has no keys. Best-effort treats each missing branch as constant zero; strict export fails.");
        }

        internal static JsonValue AveragedAnimationCurveJson(
            AnimationCurve minimum,
            AnimationCurve maximum,
            float multiplier)
        {
            var times = new SortedSet<float>();
            times.Add(0);
            times.Add(1);
            if (minimum != null)
            {
                foreach (var key in minimum.keys) times.Add(Mathf.Clamp01(key.time));
            }
            if (maximum != null)
            {
                foreach (var key in maximum.keys) times.Add(Mathf.Clamp01(key.time));
            }

            var ordered = times.ToArray();
            var functions = Json.Array();
            for (var index = 0; index + 1 < ordered.Length; index++)
            {
                var start = ordered[index];
                var end = ordered[index + 1];
                var delta = end - start;
                if (delta <= 0.000001f) continue;
                var p0 = AverageCurveValue(minimum, maximum, start) * multiplier;
                var p1 = (AverageCurveValue(minimum, maximum, start) +
                          AverageCurveSlope(minimum, maximum, start, false) * delta / 3f) * multiplier;
                var p3 = AverageCurveValue(minimum, maximum, end) * multiplier;
                var p2 = (AverageCurveValue(minimum, maximum, end) -
                          AverageCurveSlope(minimum, maximum, end, true) * delta / 3f) * multiplier;
                functions.Add(Json.Object()
                    .Add("function", Json.Object()
                        .Add("p0", Json.Number(Finite(p0)))
                        .Add("p1", Json.Number(Finite(p1)))
                        .Add("p2", Json.Number(Finite(p2)))
                        .Add("p3", Json.Number(Finite(p3))))
                    .Add("start", Json.Number(start)));
            }
            return Json.Object().Add("type", Json.String("PiecewiseBezier")).Add("functions", functions);
        }

        internal static float AverageCurveValue(AnimationCurve minimum, AnimationCurve maximum, float time)
        {
            return ((minimum == null ? 0 : minimum.Evaluate(time)) +
                    (maximum == null ? 0 : maximum.Evaluate(time))) * 0.5f;
        }

        internal static float AverageCurveSlope(
            AnimationCurve minimum,
            AnimationCurve maximum,
            float time,
            bool fromLeft)
        {
            return (AnimationCurveSlope(minimum, time, fromLeft) +
                    AnimationCurveSlope(maximum, time, fromLeft)) * 0.5f;
        }

        internal static float AnimationCurveSlope(AnimationCurve curve, float time, bool fromLeft)
        {
            if (curve == null || curve.length == 0) return 0;
            var keys = curve.keys;
            const float epsilon = 0.000001f;
            if (time < keys[0].time - epsilon || time > keys[keys.Length - 1].time + epsilon) return 0;
            for (var index = 0; index < keys.Length; index++)
            {
                if (Mathf.Abs(time - keys[index].time) <= epsilon)
                {
                    if (fromLeft) return index == 0 ? 0 : Finite(keys[index].inTangent);
                    return index + 1 == keys.Length ? 0 : Finite(keys[index].outTangent);
                }
            }

            for (var index = 0; index + 1 < keys.Length; index++)
            {
                var left = keys[index];
                var right = keys[index + 1];
                if (time <= left.time || time >= right.time) continue;
                var delta = right.time - left.time;
                var t = (time - left.time) / delta;
                var t2 = t * t;
                var derivative =
                    (6 * t2 - 6 * t) * left.value +
                    (3 * t2 - 4 * t + 1) * delta * left.outTangent +
                    (-6 * t2 + 6 * t) * right.value +
                    (3 * t2 - 2 * t) * delta * right.inTangent;
                return Finite(derivative / delta);
            }
            return 0;
        }

        internal static JsonValue Gradient(
            ParticleSystem.MinMaxGradient gradient,
            ConversionDiagnostics diagnostics,
            string field,
            Color? multiplier = null)
        {
            var factor = multiplier ?? Color.white;
            switch (gradient.mode)
            {
                case ParticleSystemGradientMode.Color: return ConstantColor(MultiplyColor(gradient.color, factor));
                case ParticleSystemGradientMode.TwoColors: return Json.Object()
                    .Add("type", Json.String("ColorRange"))
                    .Add("a", ColorJson(MultiplyColor(gradient.colorMin, factor)))
                    .Add("b", ColorJson(MultiplyColor(gradient.colorMax, factor)));
                case ParticleSystemGradientMode.Gradient:
                    return GradientJson(gradient.gradient, diagnostics, field, factor);
                case ParticleSystemGradientMode.TwoGradients: return Json.Object()
                    .Add("type", Json.String("RandomColorBetweenGradient"))
                    .Add("gradient1", GradientJson(gradient.gradientMin, diagnostics, field + ".min", factor))
                    .Add("gradient2", GradientJson(gradient.gradientMax, diagnostics, field + ".max", factor));
                case ParticleSystemGradientMode.RandomColor:
                    if (!string.Equals(field, "main.startColor", StringComparison.Ordinal))
                    {
                        diagnostics.unsupported.Add(field + ".randomColor");
                    }
                    diagnostics.approximated.Add(field + ".randomColor.emissionTimeGradientFallback");
                    diagnostics.warnings.Add(field + " Random Color samples one stable random point from the source gradient per particle. Best-effort stock JSON uses an emission-time Gradient; only Main Start Color is corrected by paired-runtime metadata.");
                    return GradientJson(gradient.gradient, diagnostics, field, factor);
                default:
                    diagnostics.unsupported.Add(field + ".unknownGradientMode");
                    diagnostics.approximated.Add(field + ".unknownGradientMode.whiteFallback");
                    diagnostics.warnings.Add(field + " uses an unknown Unity gradient mode. Best-effort explicitly emits opaque white; strict export fails.");
                    return ConstantColor(factor);
            }
        }

        internal static JsonValue GradientJson(
            UnityEngine.Gradient gradient,
            ConversionDiagnostics diagnostics,
            string field,
            Color? multiplier = null)
        {
            var factor = multiplier ?? Color.white;
            if (gradient == null)
            {
                diagnostics.unsupported.Add(field + ".missingGradient");
                diagnostics.approximated.Add(field + ".missingGradient.whiteFallback");
                diagnostics.warnings.Add(field + " selects a gradient mode but has no Gradient object. Best-effort explicitly emits opaque white; strict export fails.");
                return ConstantColor(factor);
            }
            if (gradient.colorSpace == ColorSpace.Uninitialized)
            {
                diagnostics.mapped.Add(field + ".projectDefaultGradientColorSpace");
            }
            else if (gradient.colorSpace != ColorSpace.Gamma)
            {
                diagnostics.mapped.Add(field + ".linearGradientColorSpace.directKeys");
            }
            if (gradient.mode == GradientMode.Fixed)
            {
                diagnostics.unsupported.Add(field + ".fixedGradient");
                diagnostics.approximated.Add(field + ".fixedGradient.linearInterpolationFallback");
                diagnostics.warnings.Add(field + " uses fixed-step Gradient interpolation. Best-effort explicitly uses linear interpolation; strict export fails.");
            }
            var colors = Json.Array();
            foreach (var key in gradient.colorKeys.OrderBy(item => item.time))
            {
                var color = MultiplyColor(key.color, factor);
                colors.Add(Json.Object().Add("value", Json.Object()
                    .Add("r", Json.Number(color.r)).Add("g", Json.Number(color.g)).Add("b", Json.Number(color.b)))
                    .Add("pos", Json.Number(key.time)));
            }
            var alphas = Json.Array();
            foreach (var key in gradient.alphaKeys.OrderBy(item => item.time))
            {
                alphas.Add(Json.Object()
                    .Add("value", Json.Number(key.alpha * factor.a))
                    .Add("pos", Json.Number(key.time)));
            }
            return Json.Object()
                .Add("type", Json.String("Gradient"))
                .Add("color", Json.Object().Add("type", Json.String("CLinearFunction")).Add("subType", Json.String("Color")).Add("keys", colors))
                .Add("alpha", Json.Object().Add("type", Json.String("CLinearFunction")).Add("subType", Json.String("Number")).Add("keys", alphas));
        }

        internal static JsonValue Euler(
            ParticleSystem.MinMaxCurve x,
            ParticleSystem.MinMaxCurve y,
            ParticleSystem.MinMaxCurve z,
            Vector3 angularAxisSigns,
            ConversionDiagnostics diagnostics,
            string field,
            string eulerOrder = "XYZ")
        {
            return Json.Object().Add("type", Json.String("Euler"))
                .Add("angleX", ScaleCurve(x, angularAxisSigns.x, diagnostics, field + ".x"))
                .Add("angleY", ScaleCurve(y, angularAxisSigns.y, diagnostics, field + ".y"))
                .Add("angleZ", ScaleCurve(z, angularAxisSigns.z, diagnostics, field + ".z"))
                // Unity's RotationOrder names describe the reverse
                // multiplication order used by Quarks/Three Euler math.
                // Keep the source order explicit, then emit the target
                // equivalent so rotations such as Unity ZXY (-90,-180,0)
                // preserve their authored up axis.
                .Add("eulerOrder", Json.String(UnityEulerOrderToQuarks(eulerOrder)));
        }

        internal static string UnityEulerOrderToQuarks(string unityOrder)
        {
            switch (unityOrder)
            {
                case "XYZ": return "ZYX";
                case "XZY": return "YZX";
                case "YZX": return "XZY";
                case "YXZ": return "ZXY";
                case "ZXY": return "YXZ";
                case "ZYX": return "XYZ";
                default: return unityOrder;
            }
        }

        internal static JsonValue ScalarMeshEuler(
            ParticleSystem.MinMaxCurve angle,
            float angleSign,
            ConversionDiagnostics diagnostics,
            string field)
        {
            return Json.Object().Add("type", Json.String("Euler"))
                .Add("angleX", Constant(0))
                .Add("angleY", Constant(0))
                .Add("angleZ", ScaleCurve(angle, angleSign, diagnostics, field + ".z"))
                .Add("eulerOrder", Json.String("XYZ"));
        }

        internal static Vector3 AngularAxisSigns(Vector3 particleAxisSigns)
        {
            var determinant = particleAxisSigns.x * particleAxisSigns.y * particleAxisSigns.z;
            return new Vector3(
                determinant * particleAxisSigns.x,
                -determinant * particleAxisSigns.y,
                -determinant * particleAxisSigns.z);
        }

        internal static float ScalarAngleSign(Vector3 particleAxisSigns)
        {
            return -particleAxisSigns.x * particleAxisSigns.y * particleAxisSigns.z;
        }

        internal static JsonValue VectorFunction(ParticleSystem.MinMaxCurve x, ParticleSystem.MinMaxCurve y, ParticleSystem.MinMaxCurve z, ConversionDiagnostics diagnostics, string field)
        {
            return Json.Object().Add("type", Json.String("Vector3Function"))
                .Add("x", Curve(x, diagnostics, field + ".x"))
                .Add("y", Curve(y, diagnostics, field + ".y"))
                .Add("z", Curve(z, diagnostics, field + ".z"));
        }

        internal static JsonValue Constant(float value) => Json.Object().Add("type", Json.String("ConstantValue")).Add("value", Json.Number(Finite(value)));
        internal static JsonValue Interval(float min, float max) => Json.Object().Add("type", Json.String("IntervalValue")).Add("a", Json.Number(Finite(min))).Add("b", Json.Number(Finite(max)));
        internal static JsonValue ConstantColor(Color color) => Json.Object().Add("type", Json.String("ConstantColor")).Add("color", ColorJson(color));
        internal static Color MultiplyColor(Color left, Color right) => new Color(
            left.r * right.r,
            left.g * right.g,
            left.b * right.b,
            left.a * right.a);
        internal static JsonObject ColorJson(Color color)
        {
            return Json.Object().Add("r", Json.Number(color.r)).Add("g", Json.Number(color.g)).Add("b", Json.Number(color.b)).Add("a", Json.Number(color.a));
        }

        internal static ParticleSystem.MinMaxCurve ZeroCurve() => new ParticleSystem.MinMaxCurve(0);

        internal static bool CurveHasEffect(ParticleSystem.MinMaxCurve curve)
        {
            switch (curve.mode)
            {
                case ParticleSystemCurveMode.Constant: return Mathf.Abs(curve.constant) > 0.000001f;
                case ParticleSystemCurveMode.TwoConstants: return Mathf.Abs(curve.constantMin) > 0.000001f || Mathf.Abs(curve.constantMax) > 0.000001f;
                case ParticleSystemCurveMode.Curve:
                    return AnimationCurveMagnitude(curve.curve) * Mathf.Abs(curve.curveMultiplier) > 0.000001f;
                case ParticleSystemCurveMode.TwoCurves:
                    return Mathf.Max(
                        AnimationCurveMagnitude(curve.curveMin),
                        AnimationCurveMagnitude(curve.curveMax)) * Mathf.Abs(curve.curveMultiplier) > 0.000001f;
                // Unknown modes may carry nonzero serialized data. Treat them as
                // active so the field-specific converter emits a strict,
                // explicitly named fallback instead of silently skipping it.
                default: return true;
            }
        }

        internal static bool CurveIsSpeedIndependent(ParticleSystem.MinMaxCurve curve)
        {
            return curve.mode == ParticleSystemCurveMode.Constant ||
                   curve.mode == ParticleSystemCurveMode.TwoConstants;
        }

        internal static bool GradientHasEffect(ParticleSystem.MinMaxGradient gradient)
        {
            switch (gradient.mode)
            {
                case ParticleSystemGradientMode.Color:
                    return !ColorIsWhite(gradient.color);
                case ParticleSystemGradientMode.TwoColors:
                    return !ColorIsWhite(gradient.colorMin) || !ColorIsWhite(gradient.colorMax);
                case ParticleSystemGradientMode.Gradient:
                case ParticleSystemGradientMode.RandomColor:
                    return GradientHasEffect(gradient.gradient);
                case ParticleSystemGradientMode.TwoGradients:
                    return GradientHasEffect(gradient.gradientMin) || GradientHasEffect(gradient.gradientMax);
                default:
                    return true;
            }
        }

        internal static bool GradientHasEffect(UnityEngine.Gradient gradient)
        {
            return gradient != null &&
                   (gradient.colorKeys.Any(key => !ColorIsWhite(new Color(key.color.r, key.color.g, key.color.b, 1))) ||
                    gradient.alphaKeys.Any(key => Mathf.Abs(key.alpha - 1f) > 0.000001f));
        }

        internal static bool ColorIsWhite(Color color)
        {
            return Mathf.Abs(color.r - 1f) <= 0.000001f &&
                   Mathf.Abs(color.g - 1f) <= 0.000001f &&
                   Mathf.Abs(color.b - 1f) <= 0.000001f &&
                   Mathf.Abs(color.a - 1f) <= 0.000001f;
        }

        internal static bool CurveDiffersFrom(ParticleSystem.MinMaxCurve curve, float expected)
        {
            switch (curve.mode)
            {
                case ParticleSystemCurveMode.Constant:
                    return Mathf.Abs(curve.constant - expected) > 0.000001f;
                case ParticleSystemCurveMode.TwoConstants:
                    return Mathf.Abs(curve.constantMin - expected) > 0.000001f ||
                           Mathf.Abs(curve.constantMax - expected) > 0.000001f;
                case ParticleSystemCurveMode.Curve:
                    return curve.curve != null && curve.curve.keys.Any(key =>
                        Mathf.Abs(key.value * curve.curveMultiplier - expected) > 0.000001f);
                case ParticleSystemCurveMode.TwoCurves:
                    return (curve.curveMin != null && curve.curveMin.keys.Any(key =>
                               Mathf.Abs(key.value * curve.curveMultiplier - expected) > 0.000001f)) ||
                           (curve.curveMax != null && curve.curveMax.keys.Any(key =>
                               Mathf.Abs(key.value * curve.curveMultiplier - expected) > 0.000001f));
                default:
                    return true;
            }
        }

        internal static float MaximumRenderedParticleSize(ParticleSystem system)
        {
            var main = system.main;
            var startSize = main.startSize3D
                ? (CurveMagnitude(main.startSizeX) + CurveMagnitude(main.startSizeY)) * 0.5f
                : CurveMagnitude(main.startSize);
            var sizeOverLifetime = system.sizeOverLifetime;
            if (!sizeOverLifetime.enabled) return startSize;
            var lifetimeScale = sizeOverLifetime.separateAxes
                ? (CurveMagnitude(sizeOverLifetime.x) + CurveMagnitude(sizeOverLifetime.y)) * 0.5f
                : CurveMagnitude(sizeOverLifetime.size);
            return startSize * lifetimeScale;
        }

        internal static float CurveMagnitude(ParticleSystem.MinMaxCurve curve)
        {
            switch (curve.mode)
            {
                case ParticleSystemCurveMode.Constant:
                    return Mathf.Abs(curve.constant);
                case ParticleSystemCurveMode.TwoConstants:
                    return Mathf.Max(Mathf.Abs(curve.constantMin), Mathf.Abs(curve.constantMax));
                case ParticleSystemCurveMode.Curve:
                    return AnimationCurveMagnitude(curve.curve) * Mathf.Abs(curve.curveMultiplier);
                case ParticleSystemCurveMode.TwoCurves:
                    return Mathf.Max(AnimationCurveMagnitude(curve.curveMin), AnimationCurveMagnitude(curve.curveMax)) *
                           Mathf.Abs(curve.curveMultiplier);
                default:
                    return 0;
            }
        }

        internal static float AnimationCurveMagnitude(AnimationCurve curve)
        {
            if (curve == null || curve.length == 0) return 0;
            var keys = curve.keys;
            var firstTime = keys[0].time;
            var lastTime = keys[keys.Length - 1].time;
            var maximum = keys.Max(key => Mathf.Abs(key.value));
            for (var index = 0; index <= 64; index++)
            {
                maximum = Mathf.Max(maximum, Mathf.Abs(curve.Evaluate(Mathf.Lerp(firstTime, lastTime, index / 64f))));
            }
            return maximum;
        }

        internal static bool Approximately(Vector3 left, Vector3 right)
        {
            return Mathf.Abs(left.x - right.x) <= 0.000001f &&
                   Mathf.Abs(left.y - right.y) <= 0.000001f &&
                   Mathf.Abs(left.z - right.z) <= 0.000001f;
        }

        internal static bool Approximately(Matrix4x4 left, Matrix4x4 right)
        {
            for (var row = 0; row < 4; row++)
            for (var column = 0; column < 4; column++)
            {
                if (Mathf.Abs(left[row, column] - right[row, column]) > 0.000001f) return false;
            }
            return true;
        }

        internal static JsonArray MatrixArray(Matrix4x4 matrix)
        {
            return Json.Array()
                .Add(Json.Number(matrix.m00)).Add(Json.Number(matrix.m10)).Add(Json.Number(matrix.m20)).Add(Json.Number(matrix.m30))
                .Add(Json.Number(matrix.m01)).Add(Json.Number(matrix.m11)).Add(Json.Number(matrix.m21)).Add(Json.Number(matrix.m31))
                .Add(Json.Number(matrix.m02)).Add(Json.Number(matrix.m12)).Add(Json.Number(matrix.m22)).Add(Json.Number(matrix.m32))
                .Add(Json.Number(matrix.m03)).Add(Json.Number(matrix.m13)).Add(Json.Number(matrix.m23)).Add(Json.Number(matrix.m33));
        }

        internal static JsonArray VectorArray(Vector3 vector)
        {
            return Json.Array()
                .Add(Json.Number(Finite(vector.x)))
                .Add(Json.Number(Finite(vector.y)))
                .Add(Json.Number(Finite(vector.z)));
        }

        internal static float Finite(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? 0 : value;
        }
    }
}
