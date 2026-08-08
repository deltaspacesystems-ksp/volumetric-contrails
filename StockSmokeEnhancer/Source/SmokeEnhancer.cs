using System;
using System.Collections.Generic;
using UnityEngine;

namespace StockSmokeEnhancer
{
    /// <summary>
    /// Scans active ParticleSystems (stock engine effects: smoke, exhaust, sparks) and
    /// multiplies their emission / lifetime / size according to SmokeEnhancerSettings.
    ///
    /// Emission is re-applied every frame from the value stock just wrote (stock drives
    /// it from a throttle curve every frame, so multiplying "current value" every frame
    /// is equivalent to multiplying "base value" - it does not compound).
    ///
    /// Lifetime/size are NOT re-written by stock every frame, so instead we cache the
    /// original ("base") curve the first time we see a given ParticleSystem instance and
    /// re-derive the boosted curve from that cached base every frame. This is what makes
    /// slider changes in the UI apply live to already-running effects, without the value
    /// exploding from repeated multiplication.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class SmokeEnhancer : MonoBehaviour
    {
        private static readonly string[] Keywords =
        {
            "smoke", "exhaust", "monoprop", "srb", "flame", "plume", "shock", "spark"
        };

        private readonly Dictionary<int, ParticleSystem.MinMaxCurve> baseLifetime = new Dictionary<int, ParticleSystem.MinMaxCurve>();
        private readonly Dictionary<int, ParticleSystem.MinMaxCurve> baseSize = new Dictionary<int, ParticleSystem.MinMaxCurve>();
        private readonly HashSet<int> activeIds = new HashSet<int>();
        private readonly List<int> staleIds = new List<int>();

        private void Awake()
        {
            SmokeEnhancerSettings.Load();
        }

        private void LateUpdate()
        {
            activeIds.Clear();

            ParticleSystem[] systems = FindObjectsOfType<ParticleSystem>();
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem ps = systems[i];
                if (ps == null || !IsTargetEffect(ps.gameObject.name)) continue;

                int id = ps.GetInstanceID();
                activeIds.Add(id);

                BoostEmission(ps);
                BoostLifetimeAndSize(ps, id);
            }

            PruneStaleCacheEntries();
        }

        private static bool IsTargetEffect(string name)
        {
            for (int i = 0; i < Keywords.Length; i++)
            {
                if (name.IndexOf(Keywords[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static void BoostEmission(ParticleSystem ps)
        {
            ParticleSystem.EmissionModule emission = ps.emission;
            if (!emission.enabled) return;

            emission.rateOverTime = ScaleCurve(emission.rateOverTime, SmokeEnhancerSettings.EmissionMultiplier);
            emission.rateOverDistance = ScaleCurve(emission.rateOverDistance, SmokeEnhancerSettings.EmissionMultiplier);
        }

        private void BoostLifetimeAndSize(ParticleSystem ps, int id)
        {
            ParticleSystem.MainModule main = ps.main;

            if (!baseLifetime.TryGetValue(id, out ParticleSystem.MinMaxCurve lifetimeBase))
            {
                lifetimeBase = main.startLifetime;
                baseLifetime[id] = lifetimeBase;
            }
            main.startLifetime = ScaleCurve(lifetimeBase, SmokeEnhancerSettings.LifetimeMultiplier);

            if (!baseSize.TryGetValue(id, out ParticleSystem.MinMaxCurve sizeBase))
            {
                sizeBase = main.startSize;
                baseSize[id] = sizeBase;
            }
            main.startSize = ScaleCurve(sizeBase, SmokeEnhancerSettings.SizeMultiplier);
        }

        private void PruneStaleCacheEntries()
        {
            staleIds.Clear();
            foreach (int id in baseLifetime.Keys)
            {
                if (!activeIds.Contains(id)) staleIds.Add(id);
            }
            for (int i = 0; i < staleIds.Count; i++)
            {
                baseLifetime.Remove(staleIds[i]);
                baseSize.Remove(staleIds[i]);
            }
        }

        /// <summary>Multiplies a curve's value regardless of its mode (constant, two constants, curve, two curves).</summary>
        private static ParticleSystem.MinMaxCurve ScaleCurve(ParticleSystem.MinMaxCurve curve, float factor)
        {
            switch (curve.mode)
            {
                case ParticleSystemCurveMode.Constant:
                    curve.constant *= factor;
                    break;
                case ParticleSystemCurveMode.TwoConstants:
                    curve.constantMin *= factor;
                    curve.constantMax *= factor;
                    break;
                default: // Curve, TwoCurves
                    curve.curveMultiplier *= factor;
                    break;
            }

            return curve;
        }
    }
}
