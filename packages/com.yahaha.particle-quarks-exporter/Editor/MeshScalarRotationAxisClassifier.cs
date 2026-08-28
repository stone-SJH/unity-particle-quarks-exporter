using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityParticleQuarksExporter.Editor
{
    internal enum MeshScalarRotationAxisMode
    {
        Fixed,
        Position,
        Velocity,
        UniformXY
    }

    internal sealed class MeshScalarRotationAxisClassification
    {
        public MeshScalarRotationAxisMode mode;
        public Vector3 axis;
    }

    internal static class MeshScalarRotationAxisClassifier
    {
        private const int ProbeParticleCount = 128;
        private const float DirectionEpsilon = 0.0001f;
        private const float CorrelationThreshold = 0.995f;
        private const float FixedThreshold = 0.9995f;
        private const uint ProbeSeed = 0x6a09e667u;

        public static bool TryClassify(
            ParticleSystem source,
            out MeshScalarRotationAxisClassification classification,
            out string failure)
        {
            classification = null;
            failure = null;
            ParticleSystem probe = null;
            try
            {
                probe = UnityEngine.Object.Instantiate(source);
                probe.gameObject.hideFlags = HideFlags.HideAndDontSave;
                probe.transform.SetParent(null, false);
                probe.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                probe.transform.localScale = Vector3.one;

                var main = probe.main;
                main.loop = false;
                main.playOnAwake = false;
                main.duration = 1;
                main.startDelay = 0;
                main.startLifetime = 10;
                main.startRotation3D = false;
                main.startRotation = 1.234f;
                main.maxParticles = ProbeParticleCount;
                main.gravityModifier = 0;

                var emission = probe.emission;
                emission.enabled = true;
                emission.rateOverTime = 640f;
                emission.rateOverDistance = 0;
                emission.SetBursts(Array.Empty<ParticleSystem.Burst>());

                var velocityOverLifetime = probe.velocityOverLifetime;
                velocityOverLifetime.enabled = false;
                var forceOverLifetime = probe.forceOverLifetime;
                forceOverLifetime.enabled = false;
                var limitVelocityOverLifetime = probe.limitVelocityOverLifetime;
                limitVelocityOverLifetime.enabled = false;
                var noise = probe.noise;
                noise.enabled = false;
                var collision = probe.collision;
                collision.enabled = false;
                var trigger = probe.trigger;
                trigger.enabled = false;
                var externalForces = probe.externalForces;
                externalForces.enabled = false;

                // Particle.axisOfRotation is derived from the Shape-local
                // direction before Shape position/rotation/scale is applied.
                // Probe that canonical rule; the exporter emits the authored
                // Shape transform separately for runtime reconstruction.
                var shape = probe.shape;
                if (shape.enabled)
                {
                    shape.position = Vector3.zero;
                    shape.rotation = Vector3.zero;
                    shape.scale = Vector3.one;
                }

                probe.useAutoRandomSeed = false;
                probe.randomSeed = ProbeSeed;
                probe.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                probe.Simulate(0.1f, true, true, true);

                var particles = new ParticleSystem.Particle[ProbeParticleCount];
                var count = probe.GetParticles(particles);
                if (count < 16)
                {
                    failure = "Unity technical probe emitted too few particles to classify the Mesh scalar-rotation axis.";
                    return false;
                }

                var samples = new List<AxisSample>(count);
                for (var index = 0; index < count; index++)
                {
                    var axis = particles[index].axisOfRotation;
                    if (!IsFinite(axis) || axis.sqrMagnitude < DirectionEpsilon * DirectionEpsilon)
                    {
                        failure = "Unity technical probe returned an invalid Particle.axisOfRotation.";
                        return false;
                    }
                    samples.Add(new AxisSample
                    {
                        axis = axis.normalized,
                        position = particles[index].position,
                        velocity = particles[index].velocity
                    });
                }

                if (TryFixed(samples, out var fixedAxis))
                {
                    classification = new MeshScalarRotationAxisClassification
                    {
                        mode = MeshScalarRotationAxisMode.Fixed,
                        axis = CleanAxis(fixedAxis)
                    };
                    return true;
                }
                if (MatchesDirection(samples, sample => sample.position))
                {
                    classification = new MeshScalarRotationAxisClassification
                    {
                        mode = MeshScalarRotationAxisMode.Position
                    };
                    return true;
                }
                if (MatchesDirection(samples, sample => sample.velocity))
                {
                    classification = new MeshScalarRotationAxisClassification
                    {
                        mode = MeshScalarRotationAxisMode.Velocity
                    };
                    return true;
                }
                if (IsUniformXY(samples))
                {
                    classification = new MeshScalarRotationAxisClassification
                    {
                        mode = MeshScalarRotationAxisMode.UniformXY
                    };
                    return true;
                }

                failure = "Unity Particle.axisOfRotation samples do not match a supported fixed, position-derived, velocity-derived, or uniform-XY rule.";
                return false;
            }
            catch (Exception exception)
            {
                failure = "Unity technical probe failed: " + exception.Message;
                return false;
            }
            finally
            {
                if (probe != null) UnityEngine.Object.DestroyImmediate(probe.gameObject);
            }
        }

        private static bool TryFixed(IReadOnlyList<AxisSample> samples, out Vector3 axis)
        {
            axis = samples[0].axis;
            var sum = Vector3.zero;
            for (var index = 0; index < samples.Count; index++)
            {
                if (Vector3.Dot(axis, samples[index].axis) < FixedThreshold) return false;
                sum += samples[index].axis;
            }
            axis = sum.normalized;
            return axis.sqrMagnitude > 0.99f;
        }

        private static bool MatchesDirection(
            IReadOnlyList<AxisSample> samples,
            Func<AxisSample, Vector3> selectDirection)
        {
            var valid = 0;
            var correlated = 0;
            var totalDot = 0f;
            for (var index = 0; index < samples.Count; index++)
            {
                var direction = selectDirection(samples[index]);
                var expected = Vector3.Cross(Vector3.forward, direction);
                if (expected.sqrMagnitude < 0.000001f) continue;
                expected.Normalize();
                valid++;
                var dot = Vector3.Dot(expected, samples[index].axis);
                totalDot += dot;
                if (dot >= CorrelationThreshold) correlated++;
            }
            return valid >= Math.Max(12, samples.Count * 3 / 4) &&
                   correlated >= Mathf.CeilToInt(valid * 0.95f) &&
                   totalDot / valid >= 0.98f;
        }

        private static bool IsUniformXY(IReadOnlyList<AxisSample> samples)
        {
            var firstX = 0f;
            var firstY = 0f;
            var secondX = 0f;
            var secondY = 0f;
            var thirdX = 0f;
            var thirdY = 0f;
            var fourthX = 0f;
            var fourthY = 0f;
            for (var index = 0; index < samples.Count; index++)
            {
                var axis = samples[index].axis;
                if (Mathf.Abs(axis.z) > 0.001f) return false;
                var planarLength = Mathf.Sqrt(axis.x * axis.x + axis.y * axis.y);
                if (planarLength < 0.999f) return false;
                var x = axis.x / planarLength;
                var y = axis.y / planarLength;
                firstX += x;
                firstY += y;
                var cos2 = x * x - y * y;
                var sin2 = 2 * x * y;
                secondX += cos2;
                secondY += sin2;
                thirdX += cos2 * x - sin2 * y;
                thirdY += sin2 * x + cos2 * y;
                fourthX += cos2 * cos2 - sin2 * sin2;
                fourthY += 2 * cos2 * sin2;
            }
            var inverseCount = 1f / samples.Count;
            var firstMoment = Mathf.Sqrt(firstX * firstX + firstY * firstY) * inverseCount;
            var secondMoment = Mathf.Sqrt(secondX * secondX + secondY * secondY) * inverseCount;
            var thirdMoment = Mathf.Sqrt(thirdX * thirdX + thirdY * thirdY) * inverseCount;
            var fourthMoment = Mathf.Sqrt(fourthX * fourthX + fourthY * fourthY) * inverseCount;
            return firstMoment < 0.25f && secondMoment < 0.35f &&
                   thirdMoment < 0.35f && fourthMoment < 0.35f;
        }

        private static Vector3 CleanAxis(Vector3 axis)
        {
            axis.Normalize();
            axis.x = CleanComponent(axis.x);
            axis.y = CleanComponent(axis.y);
            axis.z = CleanComponent(axis.z);
            return axis.normalized;
        }

        private static float CleanComponent(float value)
        {
            if (Mathf.Abs(value) < 0.000001f) return 0;
            if (Mathf.Abs(value - 1) < 0.000001f) return 1;
            if (Mathf.Abs(value + 1) < 0.000001f) return -1;
            return value;
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                   !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                   !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        private struct AxisSample
        {
            public Vector3 axis;
            public Vector3 position;
            public Vector3 velocity;
        }
    }
}
