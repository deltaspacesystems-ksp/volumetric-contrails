using System.Collections.Generic;
using UnityEngine;

namespace VolumetricContrails
{
    /// <summary>
    /// Dym startowy jako niezależne bilbordy (kwadraty zawsze zwrócone w stronę
    /// kamery, rozwijane w vertex shaderze - patrz SmokeBillboard.shader), nie
    /// natywny ParticleSystem (ten miał nierozwiązany bug z floating origin/
    /// Krakensbane - Unity samo integruje pozycje cząstek, więc nie dało się
    /// zastosować naszej sprawdzonej poprawki body.bodyTransform).
    ///
    /// Tutaj WSZYSTKO - fizykę i geometrię - kontrolujemy sami, więc możemy użyć
    /// dokładnie tego samego, już przetestowanego mechanizmu co ContrailTrailMesh:
    /// pozycje przechowywane względem body.bodyTransform, korygowane świeżo przy
    /// każdym odtworzeniu pozycji światowej - odporne na floating origin z założenia.
    /// </summary>
    public class SmokeBillboardMesh : MonoBehaviour
    {
        private struct Puff
        {
            public Vector3 localPos; // względem body.bodyTransform
            public Vector3 velocity; // w przestrzeni świata
            public float age;
            public float rotation;
            public float rotationSpeed;
            public float sizeMultiplier; // losowa wariacja rozmiaru per-kłąb
        }

        private readonly List<Puff> puffs = new List<Puff>();

        private float startSize;
        private float maxSize;
        private float growthSharpness;
        private float lifeTime;
        private int maxPuffs;

        private float buoyancySpeed;
        private Vector3 windDrift;
        private CelestialBody body;
        private float fadeStartAltitude;
        private float fadeEndAltitude;
        private const float GroundBounceDamping = 0.35f;
        private const float VelocityConvergeRate = 0.35f; // jak szybko impet wyrzutu przechodzi w unoszenie

        private Mesh mesh;
        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;

        private readonly List<Vector3> vertBuffer = new List<Vector3>(2048);
        private readonly List<Vector2> cornerBuffer = new List<Vector2>(2048);
        private readonly List<Vector2> sizeRotBuffer = new List<Vector2>(2048);
        private readonly List<Color> colorBuffer = new List<Color>(2048);
        private readonly List<int> triBuffer = new List<int>(3072);

        public bool HasActivePuffs => puffs.Count > 0;

        private static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x)
                && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
                && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        }

        private Vector3 WorldToLocal(Vector3 worldPos) => body.bodyTransform.InverseTransformPoint(worldPos);
        private Vector3 LocalToWorld(Vector3 localPos) => body.bodyTransform.TransformPoint(localPos);

        public void Initialize(
            float startSize, float maxSize, float growthSharpness, float lifeTime, int maxPuffs,
            float buoyancySpeed, Vector3 windDrift, CelestialBody body,
            float fadeStartAltitude, float fadeEndAltitude)
        {
            this.startSize = startSize;
            this.maxSize = maxSize;
            this.growthSharpness = growthSharpness;
            this.lifeTime = lifeTime;
            this.maxPuffs = maxPuffs;
            this.buoyancySpeed = buoyancySpeed;
            this.windDrift = windDrift;
            this.body = body;
            this.fadeStartAltitude = fadeStartAltitude;
            this.fadeEndAltitude = fadeEndAltitude;

            mesh = new Mesh { name = "SmokeBillboardMesh" };
            mesh.MarkDynamic();

            meshFilter = gameObject.AddComponent<MeshFilter>();
            meshFilter.mesh = mesh;

            meshRenderer = gameObject.AddComponent<MeshRenderer>();
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            // Mesh ma świadomie ogromne, nierealistyczne bounds - wierzchołki są
            // rozwijane w vertex shaderze do pełnego rozmiaru bilbordu, więc silnik
            // renderujący (frustum culling) musi wiedzieć że faktyczny zasięg jest
            // większy niż surowe pozycje "center" sugerują.
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000000f);

            Shader shader = ShaderCache.SmokeShader;
            if (shader != null)
            {
                meshRenderer.material = new Material(shader);
            }
            else
            {
                Debug.LogWarning("[VolumetricContrails] ShaderCache.SmokeShader jest null przy tworzeniu SmokeBillboardMesh.");
            }
        }

        public void AddPuff(Vector3 worldPos, Vector3 initialVelocity)
        {
            puffs.Add(new Puff
            {
                localPos = WorldToLocal(worldPos),
                velocity = initialVelocity,
                age = 0f,
                rotation = Random.Range(0f, Mathf.PI * 2f),
                rotationSpeed = Random.Range(-0.4f, 0.4f),
                sizeMultiplier = Random.Range(0.8f, 1.25f)
            });
        }

        public void Tick(float dt)
        {
            if (dt <= 0f) return;
            if (body == null) return;

            for (int i = puffs.Count - 1; i >= 0; i--)
            {
                Puff p = puffs[i];
                p.age += dt;

                if (p.age >= lifeTime)
                {
                    puffs.RemoveAt(i);
                    continue;
                }

                Vector3 worldPos = LocalToWorld(p.localPos);
                Vector3 up = (worldPos - body.position).normalized;

                Vector3 target = windDrift + up * buoyancySpeed;
                p.velocity = Vector3.Lerp(p.velocity, target, dt * VelocityConvergeRate);
                Vector3 newWorldPos = worldPos + p.velocity * dt;

                TryBounceOffGround(ref newWorldPos, ref p.velocity, up);

                if (!IsFinite(newWorldPos) || !IsFinite(p.velocity))
                {
                    Debug.LogWarning(string.Format(
                        "[VolumetricContrails][Smoke] Wykryto nieprawidłowy kłąb (NaN/Infinity) - usuwam. pos={0} vel={1} age={2:F1}",
                        newWorldPos, p.velocity, p.age));
                    puffs.RemoveAt(i);
                    continue;
                }

                p.localPos = WorldToLocal(newWorldPos);
                p.rotation += p.rotationSpeed * dt;
                puffs[i] = p;
            }

            if (puffs.Count > maxPuffs)
            {
                puffs.RemoveRange(0, puffs.Count - maxPuffs);
            }

            RebuildMesh();
        }

        private void TryBounceOffGround(ref Vector3 worldPos, ref Vector3 velocity, Vector3 up)
        {
            if (body.pqsController == null) return;

            double altitude = body.GetAltitude(worldPos);
            if (altitude > 500.0 || altitude < -500.0) return;

            Vector3d radialDir = ((Vector3d)worldPos - body.position).normalized;
            double terrainRadius = body.pqsController.GetSurfaceHeight(radialDir);
            double groundAltitude = terrainRadius - body.Radius;

            const float buffer = 1.0f;
            if (altitude < groundAltitude + buffer)
            {
                float penetration = (float)(groundAltitude + buffer - altitude);
                worldPos += up * penetration;

                float verticalSpeed = Vector3.Dot(velocity, up);
                if (verticalSpeed < 0f)
                {
                    velocity -= up * verticalSpeed;
                    velocity += up * (-verticalSpeed * GroundBounceDamping);
                }
            }
        }

        private float SizeForPuff(Puff p)
        {
            float t = Mathf.Clamp01(p.age / lifeTime);
            float eased = 1f - Mathf.Pow(1f - t, growthSharpness);
            return Mathf.Lerp(startSize, maxSize, eased) * p.sizeMultiplier;
        }

        private float AlphaForAge(float age)
        {
            float t = Mathf.Clamp01(age / lifeTime);
            float fadeIn = Mathf.Clamp01(t / 0.1f);
            float fadeOut = 1f - Mathf.Clamp01((t - 0.65f) / 0.35f);
            return fadeIn * fadeOut;
        }

        private float AlphaForAltitude(Vector3 worldPos)
        {
            double altitude = body.GetAltitude(worldPos);
            if (altitude <= fadeStartAltitude) return 1f;
            if (altitude >= fadeEndAltitude) return 0f;
            return 1f - (float)((altitude - fadeStartAltitude) / (fadeEndAltitude - fadeStartAltitude));
        }

        private void RebuildMesh()
        {
            vertBuffer.Clear();
            cornerBuffer.Clear();
            sizeRotBuffer.Clear();
            colorBuffer.Clear();
            triBuffer.Clear();

            if (puffs.Count == 0)
            {
                mesh.Clear();
                return;
            }

            for (int i = 0; i < puffs.Count; i++)
            {
                Puff p = puffs[i];
                Vector3 worldPos = LocalToWorld(p.localPos);

                float size = SizeForPuff(p);
                float alpha = AlphaForAge(p.age) * AlphaForAltitude(worldPos);

                int baseIndex = vertBuffer.Count;

                // 4 wierzchołki - shader rozwija je do pełnego kwadratu przy renderze,
                // tu tylko zapisujemy DANE (środek, róg, rozmiar, rotacja).
                Vector2[] corners = { new Vector2(-1,-1), new Vector2(1,-1), new Vector2(1,1), new Vector2(-1,1) };
                for (int c = 0; c < 4; c++)
                {
                    vertBuffer.Add(worldPos); // POSITION = środek świata (nie lokalna transformacja - shader traktuje to jako gotową pozycję świata)
                    cornerBuffer.Add(corners[c]);
                    sizeRotBuffer.Add(new Vector2(size, p.rotation));
                    colorBuffer.Add(new Color(1f, 1f, 1f, alpha));
                }

                triBuffer.Add(baseIndex); triBuffer.Add(baseIndex + 1); triBuffer.Add(baseIndex + 2);
                triBuffer.Add(baseIndex); triBuffer.Add(baseIndex + 2); triBuffer.Add(baseIndex + 3);
            }

            mesh.Clear();
            mesh.SetVertices(vertBuffer);
            mesh.SetUVs(0, cornerBuffer);
            mesh.SetUVs(1, sizeRotBuffer);
            mesh.SetColors(colorBuffer);
            mesh.SetTriangles(triBuffer, 0);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000000f);
        }
    }
}
