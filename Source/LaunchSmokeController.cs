using System.Collections.Generic;
using UnityEngine;

namespace VolumetricContrails
{
    /// <summary>
    /// Dym startowy jako niezależne bilbordy (SmokeBillboardMesh). Kontroler decyduje
    /// TYLKO kiedy/gdzie spawnować kolejny kłąb, na bazie klastrowania silników
    /// (EngineClusterUtils - ten sam sprawdzony mechanizm co ContrailVesselController).
    /// </summary>
    public class LaunchSmokeController : VesselModule
    {
        public float clusterDistanceThreshold = 3.5f;

        public float offsetDistance = 5f;
        public float spawnMaxAltitude = 15000f;
        public float minThrottle = 0.15f;
        public float spawnInterval = 0.2f;

        // Wyrzut z silnika - LEKKI, nie gwałtowny (zgodnie z ostatnią wskazówką:
        // "ma tylko lekko go wyrzucać").
        public float ejectionSpeed = 12f;

        public float lifeTime = 25f;
        public int maxPuffsPerGroup = 400;

        public float clusterStartSize = 3f;
        public float maxSize = 45f;
        public float growthSharpness = 1.2f;

        public float minSpeedForBillowing = 20f;
        public float maxSpeedForThinTrail = 250f;
        public float thinTrailSizeMultiplier = 0.2f;

        public float buoyancySpeed = 2.2f;
        public Vector3 windDrift = new Vector3(1f, 0f, 0f);

        public float fadeStartAltitude = 12000f;
        public float fadeEndAltitude = 15000f;

        public bool debugLogging = true;
        private float debugLogTimer;

        private class TrackedGroup
        {
            public int id;
            public HashSet<uint> partIds;
            public Vector3 centroid;
            public SmokeBillboardMesh smokeMesh;
            public float spawnTimer;
        }

        private readonly List<TrackedGroup> trackedGroups = new List<TrackedGroup>();
        private int nextGroupId;
        private int lastPartCount = -1;

        private float SizeMultiplierForSpeed(float speed)
        {
            if (speed <= minSpeedForBillowing) return 1f;
            if (speed >= maxSpeedForThinTrail) return thinTrailSizeMultiplier;
            float t = (speed - minSpeedForBillowing) / (maxSpeedForThinTrail - minSpeedForBillowing);
            return Mathf.Lerp(1f, thinTrailSizeMultiplier, t);
        }

        private float SizeMultiplierForAltitude(double altitude)
        {
            if (altitude <= fadeStartAltitude) return 1f;
            if (altitude >= fadeEndAltitude) return 0.35f;
            float t = (float)((altitude - fadeStartAltitude) / (fadeEndAltitude - fadeStartAltitude));
            return Mathf.Lerp(1f, 0.35f, t);
        }

        private void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            if (vessel == null || !vessel.loaded) return;
            if (vessel.vesselType == VesselType.Debris) return;

            if (vessel.Parts.Count != lastPartCount)
            {
                RecomputeGroups();
                lastPartCount = vessel.Parts.Count;
            }

            bool canSpawn = vessel.altitude <= spawnMaxAltitude;

            bool logThisFrame = false;
            if (debugLogging)
            {
                debugLogTimer -= TimeWarp.fixedDeltaTime;
                if (debugLogTimer <= 0f)
                {
                    logThisFrame = true;
                    debugLogTimer = 1f;
                }
            }

            List<EngineSample> liveSamples = canSpawn
                ? EngineClusterUtils.GatherEngineSamples(vessel)
                : new List<EngineSample>();

            float currentSpeed = (float)vessel.srfSpeed;
            float sizeMultiplier = SizeMultiplierForSpeed(currentSpeed) * SizeMultiplierForAltitude(vessel.altitude);

            if (logThisFrame)
            {
                Debug.Log(string.Format(
                    "[VolumetricContrails][Smoke] vessel={0} alt={1:F0} speed={2:F0} sizeMult={3:F2} canSpawn={4} liveEngines={5} grup={6}",
                    vessel.vesselName, vessel.altitude, currentSpeed, sizeMultiplier, canSpawn, liveSamples.Count, trackedGroups.Count));
            }

            foreach (TrackedGroup g in trackedGroups)
            {
                if (canSpawn)
                {
                    List<EngineSample> groupSamples = EngineClusterUtils.FilterSamplesByPartIds(liveSamples, g.partIds);

                    if (groupSamples.Count > 0)
                    {
                        float aggThrottle = EngineClusterUtils.ComputeMaxThrottle(groupSamples);
                        g.centroid = EngineClusterUtils.ComputeCentroid(groupSamples);

                        if (aggThrottle >= minThrottle)
                        {
                            g.spawnTimer -= TimeWarp.fixedDeltaTime;
                            if (g.spawnTimer <= 0f)
                            {
                                Vector3 centroid = EngineClusterUtils.ComputeCentroid(groupSamples);
                                Vector3 avgForward = EngineClusterUtils.ComputeAverageForward(groupSamples);
                                Vector3 spawnPos = centroid + avgForward * offsetDistance;
                                Vector3 initialVelocity = avgForward * ejectionSpeed + vessel.GetSrfVelocity() * 0.1f;

                                g.smokeMesh.AddPuff(spawnPos, initialVelocity);

                                g.spawnTimer = spawnInterval / Mathf.Lerp(0.6f, 1.4f, Mathf.Clamp01(aggThrottle));
                            }
                        }
                    }
                }

                g.smokeMesh.Tick(TimeWarp.fixedDeltaTime);
            }

            for (int i = trackedGroups.Count - 1; i >= 0; i--)
            {
                TrackedGroup g = trackedGroups[i];
                if (g.partIds.Count == 0 && !g.smokeMesh.HasActivePuffs)
                {
                    Object.Destroy(g.smokeMesh.gameObject);
                    trackedGroups.RemoveAt(i);
                }
            }
        }

        private void RecomputeGroups()
        {
            List<HashSet<uint>> newGroups = EngineClusterUtils.GroupEnginePartsStructurally(vessel, clusterDistanceThreshold);

            bool[] claimed = new bool[trackedGroups.Count];

            foreach (HashSet<uint> newPartIds in newGroups)
            {
                int bestIndex = -1;
                int bestOverlap = 0;

                for (int i = 0; i < trackedGroups.Count; i++)
                {
                    if (claimed[i]) continue;

                    int overlap = 0;
                    foreach (uint id in newPartIds)
                    {
                        if (trackedGroups[i].partIds.Contains(id)) overlap++;
                    }

                    if (overlap > bestOverlap)
                    {
                        bestOverlap = overlap;
                        bestIndex = i;
                    }
                }

                if (bestIndex >= 0)
                {
                    trackedGroups[bestIndex].partIds = newPartIds;
                    claimed[bestIndex] = true;
                }
                else
                {
                    TrackedGroup g = new TrackedGroup
                    {
                        id = nextGroupId++,
                        partIds = newPartIds
                    };

                    GameObject smokeObj = new GameObject("SmokeBillboardGroup_" + g.id);
                    g.smokeMesh = smokeObj.AddComponent<SmokeBillboardMesh>();

                    float sizeFactor = EngineClusterUtils.ClusterSizeFactor(newPartIds.Count);

                    g.smokeMesh.Initialize(
                        clusterStartSize * sizeFactor,
                        maxSize * sizeFactor,
                        growthSharpness,
                        lifeTime,
                        maxPuffsPerGroup,
                        buoyancySpeed,
                        windDrift,
                        vessel.mainBody,
                        fadeStartAltitude,
                        fadeEndAltitude);

                    trackedGroups.Add(g);

                    if (debugLogging)
                    {
                        Debug.Log(string.Format(
                            "[VolumetricContrails][Smoke] RecomputeGroups: nowa grupa id={0}, {1} części",
                            g.id, newPartIds.Count));
                    }
                }
            }

            for (int i = 0; i < trackedGroups.Count; i++)
            {
                if (!claimed[i])
                {
                    trackedGroups[i].partIds = new HashSet<uint>();
                }
            }
        }

        private void OnDestroy()
        {
            foreach (TrackedGroup g in trackedGroups)
            {
                if (g.smokeMesh != null) Object.Destroy(g.smokeMesh.gameObject);
            }
            trackedGroups.Clear();
        }
    }
}
