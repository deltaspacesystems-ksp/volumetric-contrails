using System.Collections.Generic;
using UnityEngine;

namespace VolumetricContrails
{
    /// <summary>
    /// Buduje proceduralny mesh "rurki" z listy punktów historii - "connected curve
    /// segments" jak KSA. Każdy segment ma własne, zduplikowane wierzchołki niosące
    /// oś/promień cylindra do prawdziwego raymarchingu w shaderze.
    ///
    /// v2: pozycje przechowywane względem body.bodyTransform (tak samo jak
    /// GroundSmokeMesh) - odporne na floating origin/Krakensbane/przełączanie mapy,
    /// bo ten układ jest zawsze poprawnie utrzymywany przez samo KSP. Zero ręcznej
    /// korekcji (GameEvents.onFloatingOriginShift) potrzebnej.
    /// </summary>
    public class ContrailTrailMesh : MonoBehaviour
    {
        private const int RING_SEGMENTS = 8;

        private struct ContrailPoint
        {
            public Vector3 localPos; // względem body.bodyTransform
            public Vector3 velocity; // w przestrzeni świata
            public float age;
        }

        private readonly List<ContrailPoint> points = new List<ContrailPoint>();

        private float startRadius;
        private float maxRadius;
        private float growthSharpness;
        private float lifeTime;
        private int maxPoints;
        private CelestialBody body;

        private Mesh mesh;
        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;

        private readonly List<Vector3> vertBuffer = new List<Vector3>(1024);
        private readonly List<Vector2> uvBuffer = new List<Vector2>(1024);
        private readonly List<Vector4> segABuffer = new List<Vector4>(1024);
        private readonly List<Vector3> segBBuffer = new List<Vector3>(1024);
        private readonly List<Color> colorBuffer = new List<Color>(1024);
        private readonly List<int> triBuffer = new List<int>(2048);

        public bool HasActivePoints => points.Count > 1;

        private static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x)
                && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
                && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        }

        private Vector3 WorldToLocal(Vector3 worldPos) => body.bodyTransform.InverseTransformPoint(worldPos);
        private Vector3 LocalToWorld(Vector3 localPos) => body.bodyTransform.TransformPoint(localPos);

        public void Initialize(float startRadius, float maxRadius, float growthSharpness, float lifeTime, int maxPoints, CelestialBody body)
        {
            this.startRadius = startRadius;
            this.maxRadius = maxRadius;
            this.growthSharpness = growthSharpness;
            this.lifeTime = lifeTime;
            this.maxPoints = maxPoints;
            this.body = body;

            mesh = new Mesh { name = "ContrailMesh" };
            mesh.MarkDynamic();

            meshFilter = gameObject.AddComponent<MeshFilter>();
            meshFilter.mesh = mesh;

            meshRenderer = gameObject.AddComponent<MeshRenderer>();
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;

            Shader shader = ShaderCache.ContrailShader;
            if (shader != null)
            {
                meshRenderer.material = new Material(shader);
            }
            else
            {
                Debug.LogWarning("[VolumetricContrails] ShaderCache.ContrailShader jest null - AssetLoader nie wczytał shadera (sprawdź KSP.log przy starcie gry).");
            }
        }

        public void AddPoint(Vector3 worldPos, Vector3 initialVelocity)
        {
            points.Add(new ContrailPoint
            {
                localPos = WorldToLocal(worldPos),
                velocity = initialVelocity,
                age = 0f
            });
        }

        public void Tick(float dt)
        {
            if (dt <= 0f) return;
            if (body == null) return;

            for (int i = points.Count - 1; i >= 0; i--)
            {
                ContrailPoint p = points[i];
                p.age += dt;

                if (p.age >= lifeTime)
                {
                    points.RemoveAt(i);
                    continue;
                }

                Vector3 worldPos = LocalToWorld(p.localPos);
                p.velocity = Vector3.Lerp(p.velocity, Vector3.zero, dt * 0.5f);
                Vector3 newWorldPos = worldPos + p.velocity * dt;

                if (!IsFinite(newWorldPos) || !IsFinite(p.velocity))
                {
                    Debug.LogWarning(string.Format(
                        "[VolumetricContrails] Wykryto nieprawidłowy punkt (NaN/Infinity) - usuwam. pos={0} vel={1} age={2:F1}",
                        newWorldPos, p.velocity, p.age));
                    points.RemoveAt(i);
                    continue;
                }

                p.localPos = WorldToLocal(newWorldPos);
                points[i] = p;
            }

            if (points.Count > maxPoints)
            {
                points.RemoveRange(0, points.Count - maxPoints);
            }

            RebuildMesh();
        }

        private float RadiusForAge(float age)
        {
            float t = Mathf.Clamp01(age / lifeTime);
            float eased = 1f - Mathf.Pow(1f - t, growthSharpness);
            return Mathf.Lerp(startRadius, maxRadius, eased);
        }

        private float AlphaForAge(float age)
        {
            float t = Mathf.Clamp01(age / lifeTime);
            float fadeIn = Mathf.Clamp01(t / 0.08f);
            float fadeOut = 1f - Mathf.Clamp01((t - 0.7f) / 0.3f);
            return fadeIn * fadeOut;
        }

        private void RebuildMesh()
        {
            vertBuffer.Clear();
            uvBuffer.Clear();
            segABuffer.Clear();
            segBBuffer.Clear();
            colorBuffer.Clear();
            triBuffer.Clear();

            if (points.Count < 2)
            {
                mesh.Clear();
                return;
            }

            for (int i = 0; i < points.Count - 1; i++)
            {
                Vector3 posA = LocalToWorld(points[i].localPos);
                Vector3 posB = LocalToWorld(points[i + 1].localPos);

                float segmentDist = Vector3.Distance(posA, posB);
                if (segmentDist > 2000f) continue;

                float radiusA = RadiusForAge(points[i].age);
                float radiusB = RadiusForAge(points[i + 1].age);
                float cylinderRadius = (radiusA + radiusB) * 0.5f;

                float alphaA = AlphaForAge(points[i].age);
                float alphaB = AlphaForAge(points[i + 1].age);

                Vector3 dir = (posB - posA).normalized;
                if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;

                Vector3 up = Vector3.up;
                if (Mathf.Abs(Vector3.Dot(dir, up)) > 0.99f) up = Vector3.right;
                Vector3 right = Vector3.Cross(dir, up).normalized;
                up = Vector3.Cross(right, dir).normalized;

                float vA = points[i].age / lifeTime;
                float vB = points[i + 1].age / lifeTime;

                int baseIndex = vertBuffer.Count;

                for (int s = 0; s < RING_SEGMENTS; s++)
                {
                    float angle = (s / (float)RING_SEGMENTS) * Mathf.PI * 2f;
                    Vector3 radial = Mathf.Cos(angle) * right + Mathf.Sin(angle) * up;

                    Vector3 worldA = posA + radial * radiusA;
                    vertBuffer.Add(transform.InverseTransformPoint(worldA));
                    uvBuffer.Add(new Vector2(s / (float)RING_SEGMENTS, vA));
                    segABuffer.Add(new Vector4(posA.x, posA.y, posA.z, cylinderRadius));
                    segBBuffer.Add(posB);
                    colorBuffer.Add(new Color(1f, 1f, 1f, alphaA));
                }

                for (int s = 0; s < RING_SEGMENTS; s++)
                {
                    float angle = (s / (float)RING_SEGMENTS) * Mathf.PI * 2f;
                    Vector3 radial = Mathf.Cos(angle) * right + Mathf.Sin(angle) * up;

                    Vector3 worldB = posB + radial * radiusB;
                    vertBuffer.Add(transform.InverseTransformPoint(worldB));
                    uvBuffer.Add(new Vector2(s / (float)RING_SEGMENTS, vB));
                    segABuffer.Add(new Vector4(posA.x, posA.y, posA.z, cylinderRadius));
                    segBBuffer.Add(posB);
                    colorBuffer.Add(new Color(1f, 1f, 1f, alphaB));
                }

                int ringA = baseIndex;
                int ringB = baseIndex + RING_SEGMENTS;

                for (int s = 0; s < RING_SEGMENTS; s++)
                {
                    int a = ringA + s;
                    int b = ringA + (s + 1) % RING_SEGMENTS;
                    int c = ringB + s;
                    int d = ringB + (s + 1) % RING_SEGMENTS;

                    triBuffer.Add(a); triBuffer.Add(c); triBuffer.Add(b);
                    triBuffer.Add(b); triBuffer.Add(c); triBuffer.Add(d);
                }
            }

            mesh.Clear();
            mesh.SetVertices(vertBuffer);
            mesh.SetUVs(0, uvBuffer);
            mesh.SetUVs(1, segABuffer);
            mesh.SetUVs(2, segBBuffer);
            mesh.SetColors(colorBuffer);
            mesh.SetTriangles(triBuffer, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }
    }
}
