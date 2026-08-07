using System.IO;
using UnityEngine;

namespace VolumetricContrails
{
    /// <summary>
    /// Statyczny cache shadera - ContrailTrailMesh czyta stąd zamiast robić Shader.Find,
    /// bo Shader.Find nie znajdzie custom shadera dopóki AssetBundle się nie załaduje.
    /// </summary>
    public static class ShaderCache
    {
        public static Shader ContrailShader;
        public static Shader SmokeShader;
    }

    /// <summary>
    /// KSPAddon uruchamiany raz, na ekranie startowym gry (MainMenu), zanim jakikolwiek
    /// statek zostanie załadowany - więc ShaderCache.ContrailShader jest gotowy zanim
    /// ContrailVesselController w ogóle zacznie działać.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class AssetLoader : MonoBehaviour
    {
        // Ścieżka względna do GameData - dostosuj jeśli zmienisz nazwę folderu bundla.
        private const string BundleRelativePath = "VolumetricContrails/Bundles/volumetriccontrails_bundle";
        private const string ContrailMaterialAssetName = "ContrailMat";
        private const string SmokeMaterialAssetName = "SmokeMat"; // nowy materiał - dodaj go w Unity obok ContrailMat

        private void Awake()
        {
            string bundlePath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", BundleRelativePath);

            if (!File.Exists(bundlePath))
            {
                Debug.LogError("[VolumetricContrails] Nie znaleziono AssetBundle pod: " + bundlePath);
                return;
            }

            AssetBundle bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle == null)
            {
                Debug.LogError("[VolumetricContrails] AssetBundle.LoadFromFile zwrócił null - " +
                    "sprawdź czy wersja Unity użyta do zbudowania bundla zgadza się z wersją KSP.");
                return;
            }

            LoadShaderFromMaterial(bundle, ContrailMaterialAssetName, ref ShaderCache.ContrailShader);
            LoadShaderFromMaterial(bundle, SmokeMaterialAssetName, ref ShaderCache.SmokeShader);

            // Nie zwalniamy assetów (false) - shadery muszą zostać w pamięci na cały czas gry.
            bundle.Unload(false);
        }

        private void LoadShaderFromMaterial(AssetBundle bundle, string materialAssetName, ref Shader target)
        {
            Material mat = bundle.LoadAsset<Material>(materialAssetName);
            if (mat == null)
            {
                Debug.LogError("[VolumetricContrails] Nie znaleziono materiału '" + materialAssetName +
                    "' w bundlu - sprawdź dokładną nazwę assetu (jeśli to SmokeMat, dodaj go w Unity - patrz instrukcja).");
                return;
            }

            target = mat.shader;
            Debug.Log("[VolumetricContrails] Shader wczytany poprawnie z '" + materialAssetName + "': " + target.name);
        }
    }
}
