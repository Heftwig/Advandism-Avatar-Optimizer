using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;

public static class Material_Merger_Utility {
    const string FOLDER_PATH = "Assets/Atlas_merger";
    static readonly HashSet<string> EXCLUDED_TEXTURE_PROPERTIES = new HashSet<string> {
        "_ToonRamp",
        "_Ramp",
        "_Gradient",
        "_LUT",
        "_Lookup",
        "_ShadowRamp",
        "_LightRamp"
    };
    
    struct TriangleKey {
        public int a;
        public int b;
        public int c;
        public string material;
    }

    class TexturePropertyRules {
        public string propertyName;
        public bool useDilate;
        public bool isNormalMap;
        public bool isLinear;
    }

    static readonly Dictionary<string, TexturePropertyRules> RULES = new() {
        {"_MainTex", new TexturePropertyRules {propertyName = "_MainTex", useDilate = true, isLinear = false}},
        {"_BaseMap", new TexturePropertyRules {propertyName = "_BaseMap", useDilate = true, isLinear = false}},
        {"_BumpMap", new TexturePropertyRules {propertyName = "_BumpMap", useDilate = false, isNormalMap = true, isLinear = true}},
        {"_MetallicGlossMap", new TexturePropertyRules {propertyName = "_MetallicGlossMap", useDilate = false, isLinear = true}},
        {"_OcclusionMap", new TexturePropertyRules {propertyName = "_OcclusionMap", useDilate = true, isLinear = true}},
        {"_EmissionMap", new TexturePropertyRules {propertyName = "_EmissionMap", useDilate = true, isLinear = false}}
    };

    public static void Merge(GameObject root, int size, int anisolevel, List<TextureImporterPlatformSettings> settings_list, HashSet<Material> selectedMaterials) {
        // end my autistic suffering pls
        var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        Dictionary<SkinnedMeshRenderer, Material[]> backupMaterials = new();
        foreach (var renderer in renderers)
            backupMaterials[renderer] = renderer.sharedMaterials;
        foreach (var renderer in renderers) {
            var materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++) {
                var material = materials[i];
                if (material && !selectedMaterials.Contains(material))
                    materials[i] = null;
            }
            renderer.sharedMaterials = materials;
        }
        Dictionary<Mesh, HashSet<int>> shared = FindSharedVertexUsage(renderers);
        foreach (var renderer in renderers) {
            Mesh mesh = renderer.sharedMesh;
            if (mesh && shared.TryGetValue(mesh, out var verts) && verts.Count > 0)
                renderer.sharedMesh = DuplicateVerticesForSharedUsage(mesh, verts);
        }
        HashSet<Texture2D> globalTextures = new();
        Dictionary<Texture2D, Texture2D> processedTextures = new();
        Dictionary<string, Dictionary<Material, Texture2D>> textureProperties = CollectTextureProperties(renderers, globalTextures, processedTextures);
        Dictionary<Texture2D, Rect> uvMap;
        Dictionary<string, Texture2D> atlases = BuildPropertyAtlases(textureProperties, size, out uvMap);
        atlases = SaveAtlases(atlases, size, anisolevel, settings_list);
        Dictionary<Material, Texture2D> mainTexMap = null;
        if (textureProperties.ContainsKey("_MainTex"))
            mainTexMap = textureProperties["_MainTex"];
        else if (textureProperties.ContainsKey("_BaseMap"))
            mainTexMap = textureProperties["_BaseMap"];
        if (mainTexMap != null)
            ApplyUVs(renderers, uvMap, mainTexMap, size);
        Material atlas_Material = CreateAtlasMaterial();
        ApplyAtlasesToMaterial(atlas_Material, atlases);
        EditorUtility.SetDirty(atlas_Material);
        AssetDatabase.SaveAssets();
        ApplyAtlasMaterialToRenderers(renderers, atlas_Material, selectedMaterials);
    }

    static Dictionary<string, Texture2D> SaveAtlases(Dictionary<string, Texture2D> atlases, int size, int anisolevel, List<TextureImporterPlatformSettings> settings_list) {
        Dictionary<string, Texture2D> saved = new();
        string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        foreach (var kvp in atlases) {
            Texture2D atlas = kvp.Value;
            RenderTexture rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(atlas, rt);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D readable = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
            readable.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            readable.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            byte[] png = readable.EncodeToPNG();
            string path = $"{FOLDER_PATH}/{kvp.Key}_Atlas_{timestamp}.png";
            File.WriteAllBytes(path, png);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Default;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.mipmapFilter = TextureImporterMipFilter.KaiserFilter;
            importer.mipmapEnabled = true;
            importer.mipMapBias = 0f;
            importer.anisoLevel = anisolevel;
            importer.maxTextureSize = settings_list[0].maxTextureSize;
            importer.streamingMipmaps = true;
            importer.compressionQuality = settings_list[0].compressionQuality;
            importer.crunchedCompression = true;
            foreach (var platform in settings_list) {
                importer.SetPlatformTextureSettings(platform);
            }
            importer.SaveAndReimport();
            saved[kvp.Key] = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
        return saved;
    }

    static Dictionary<Mesh, Dictionary<string, List<int[]>>> BuildMaterialTriangleMap(SkinnedMeshRenderer[] renderers) {
        Dictionary<Mesh, Dictionary<string, List<int[]>>> result = new();
        Dictionary<Mesh, int[][]> triCache = new();
        foreach (var renderer in renderers) {
            var mesh = renderer.sharedMesh;
            if (mesh) {
                if (!result.TryGetValue(mesh, out var matMap)) {
                    matMap = new Dictionary<string, List<int[]>>();
                    result[mesh] = matMap;
                }
                int[][] cachedTris;
                if (!triCache.TryGetValue(mesh, out cachedTris)) {
                    cachedTris = new int[mesh.subMeshCount][];
                    for (int i = 0; i < mesh.subMeshCount; i++)
                        cachedTris[i] = mesh.GetTriangles(i);
                    triCache[mesh] = cachedTris;
                }
                var materials = renderer.sharedMaterials;
                int subCount = mesh.subMeshCount < materials.Length ? mesh.subMeshCount : materials.Length;
                for (int i = 0; i < subCount; i++) {
                    var material = materials[i];
                    string key = material ? material.name : "NULL";
                    if (!matMap.TryGetValue(key, out var list)) {
                        list = new List<int[]>();
                        matMap[key] = list;
                    }
                    list.Add(cachedTris[i]);
                }
            }
        }
        return result;
    }

    static Dictionary<Mesh, HashSet<int>> FindSharedVertexUsage(SkinnedMeshRenderer[] renderers) {
        Dictionary<Mesh, HashSet<int>> sharedVertices = new();
        Dictionary<Mesh, int[][]> triangleCache = new();
        foreach (var renderer in renderers) {
            Mesh mesh = renderer.sharedMesh;
            if (mesh) {
                if (!sharedVertices.TryGetValue(mesh, out var vertexSet)) {
                    vertexSet = new HashSet<int>();
                    sharedVertices[mesh] = vertexSet;
                }
                if (!triangleCache.TryGetValue(mesh, out var cachedTris)) {
                    int subMeshCount = mesh.subMeshCount;
                    cachedTris = new int[subMeshCount][];
                    for (int i = 0; i < subMeshCount; i++)
                        cachedTris[i] = mesh.GetTriangles(i);
                    triangleCache[mesh] = cachedTris;
                }
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < (mesh.subMeshCount < materials.Length ? mesh.subMeshCount : materials.Length); i++) {
                    int[] tris = cachedTris[i];
                    for (int t = 0; t < tris.Length; t++)
                        vertexSet.Add(tris[t]);
                }
            }
        }
        return sharedVertices;
    }

    static Mesh DuplicateVerticesForSharedUsage(Mesh mesh, HashSet<int> sharedVertices) {
        Mesh newMesh = Object.Instantiate(mesh);
        Vector3[] oldVertices = mesh.vertices;
        Vector2[] oldUV = mesh.uv;
        List<Vector3> newVertices = new List<Vector3>(oldVertices);
        List<Vector2> newUV = new List<Vector2>(oldUV);
        Dictionary<int, int> vertexMap = new();
        foreach (int index in sharedVertices) {
            int newIndex = newVertices.Count;
            newVertices.Add(oldVertices[index]);
            newUV.Add(oldUV.Length > index ? oldUV[index] : Vector2.zero);
            vertexMap[index] = newIndex;
        }
        newMesh.vertices = newVertices.ToArray();
        newMesh.uv = newUV.ToArray();
        return newMesh;
    }

    static void ApplyAtlasMaterialToRenderers(SkinnedMeshRenderer[] renderers, Material atlasMaterial, HashSet<Material> selectedMaterials) {
        foreach (var renderer in renderers) {
            var materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++) {
                var material = materials[i];
                if (material && selectedMaterials.Contains(material))
                    materials[i] = atlasMaterial;
            }
            renderer.sharedMaterials = materials;
            EditorUtility.SetDirty(renderer);
        }
    }

    static Texture2D Dilate(Texture2D sourceTexture) {
        Texture2D dilatedTexture = new Texture2D(sourceTexture.width, sourceTexture.height, TextureFormat.RGBA32, false);
        Color[] sourcePixels = sourceTexture.GetPixels();
        Color[] outputPixels = new Color[sourcePixels.Length];
        int width = sourceTexture.width;
        int height = sourceTexture.height;
        for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++) {
                int index = y * width + x;
                Color currentPixel = sourcePixels[index];
                if (currentPixel.a > 0f) {
                    outputPixels[index] = currentPixel;
                } else {
                    Color found = Color.clear;
                    bool hasFound = false;
                    for (int radius = 1; radius <= 2 && !hasFound; radius++) {
                        for (int offsetY = -radius; offsetY <= radius && !hasFound; offsetY++) {
                            for (int offsetX = -radius; offsetX <= radius; offsetX++) {
                                int sampleX = x + offsetX;
                                int sampleY = y + offsetY;
                                if (sampleX >= 0 && sampleX < width && sampleY >= 0 && sampleY < height) {
                                    Color neighborPixel = sourcePixels[sampleY * width + sampleX];
                                    if (neighborPixel.a > 0f) {
                                        found = neighborPixel;
                                        hasFound = true;
                                    }
                                }
                            }
                        }
                    }
                    outputPixels[index] = hasFound ? found : currentPixel;
                }
            }
        }
        dilatedTexture.SetPixels(outputPixels);
        dilatedTexture.Apply();
        return dilatedTexture;
    }

    static Mesh SaveMeshAsset(Mesh mesh) {
        const string folder = "Assets/Atlas_merger";
        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder("Assets", "Atlas_merger");
        string path = $"{folder}/{mesh.name}.asset";
        AssetDatabase.CreateAsset(Object.Instantiate(mesh), path);
        AssetDatabase.SaveAssets();
        return AssetDatabase.LoadAssetAtPath<Mesh>(path);
    }

    static void ApplyUVs(SkinnedMeshRenderer[] renderers, Dictionary<Texture2D, Rect> uvMap, Dictionary<Material, Texture2D> matToTex, int size) {
        HashSet<Mesh> done = new();
        Dictionary<Mesh, int[][]> triCache = new();
        foreach (var renderer in renderers) {
            var mesh = renderer.sharedMesh;
            if (mesh && !done.Contains(mesh)) {
                int[][] cachedTris;
                if (!triCache.TryGetValue(mesh, out cachedTris)) {
                    cachedTris = new int[mesh.subMeshCount][];
                    for (int i = 0; i < mesh.subMeshCount; i++)
                        cachedTris[i] = mesh.GetTriangles(i);
                    triCache[mesh] = cachedTris;
                }
                Mesh newMesh = Object.Instantiate(mesh);
                newMesh.name = mesh.name + $"_Atlas{System.DateTime.Now:yyyyMMdd_HHmmss}";
                Undo.RegisterCreatedObjectUndo(newMesh, "Atlas Mesh");
                Vector2[][] oldUVs = GetAllUVChannels(mesh);
                Vector2[][] newUVs = CloneUVChannels(oldUVs);
                Vector2[] baseUV = oldUVs[0];
                bool[][] written = new bool[newUVs.Length][];
                for (int i = 0; i < newUVs.Length; i++)
                    written[i] = new bool[newUVs[0].Length];
                var materials = renderer.sharedMaterials;
                int subCount = mesh.subMeshCount < materials.Length ? mesh.subMeshCount : materials.Length;
                for (int i = 0; i < subCount; i++) {
                    var material = materials[i];
                    Texture2D tex;
                    Rect rect;
                    if (material && matToTex.TryGetValue(material, out tex) && uvMap.TryGetValue(tex, out rect)) {
                        float texelPadX = 1f / size;
                        float texelPadY = 1f / size;
                        float padX = texelPadX * rect.width;
                        float padY = texelPadY * rect.height;
                        Rect safeRect = new Rect(rect.x + padX, rect.y + padY, rect.width - padX * 2f, rect.height - padY * 2f);
                        int[] tris = cachedTris[i];
                        for (int t = 0; t < tris.Length; t++) {
                            int idx = tris[t];
                            if (written[0][idx])
                                continue;
                            Vector2 uv = baseUV[idx];
                            float u = uv.x;
                            float v = uv.y;
                            u = u < 0f ? 0f : (u > 1f ? 1f : u);
                            v = v < 0f ? 0f : (v > 1f ? 1f : v);
                            Vector2 mapped = new Vector2(safeRect.x + u * safeRect.width, safeRect.y + v * safeRect.height);
                            for (int c = 0; c < newUVs.Length; c++) {
                                if (!written[c][idx]) {
                                    newUVs[c][idx] = mapped;
                                    written[c][idx] = true;
                                }
                            }
                        }
                    }
                }
                SetAllUVChannels(newMesh, newUVs);
                renderer.sharedMesh = SaveMeshAsset(newMesh);
                done.Add(mesh);
            }
        }
    }

    static Vector2[][] GetAllUVChannels(Mesh mesh) {
        Vector2[][] uvs = new Vector2[8][];
        uvs[0] = mesh.uv;
        for (int i = 1; i < 8; i++) {
            var list = new List<Vector2>();
            mesh.GetUVs(i, list);
            uvs[i] = list.Count > 0 ? list.ToArray() : uvs[0];
        }
        return uvs;
    }

    static Vector2[][] CloneUVChannels(Vector2[][] src) {
        Vector2[][] copy = new Vector2[src.Length][];
        for (int i = 0; i < src.Length; i++)
            copy[i] = (Vector2[])src[i].Clone();
        return copy;
    }

    static void SetAllUVChannels(Mesh mesh, Vector2[][] uvs) {
        mesh.uv = uvs[0];
        for (int i = 1; i < uvs.Length; i++) {
            var list = new List<Vector2>(uvs[i].Length);
            list.AddRange(uvs[i]);
            mesh.SetUVs(i, list);
        }
    }

    static Material CreateAtlasMaterial() {
        if (!AssetDatabase.IsValidFolder(FOLDER_PATH))
            AssetDatabase.CreateFolder("Assets", "Atlas_merger");
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        string path = $"{FOLDER_PATH}/Atlas_Material{System.DateTime.Now:yyyyMMdd_HHmmss}.mat";
        Material material = new Material(shader);
        AssetDatabase.CreateAsset(material, path);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        material = AssetDatabase.LoadAssetAtPath<Material>(path);
        material.mainTextureScale = Vector2.one;
        material.mainTextureOffset = Vector2.zero;
        return material;
    }

    static Texture2D MakeReadable(Texture2D src, Dictionary<Texture2D, Texture2D> cache) {
        if (cache.TryGetValue(src, out var texture))
            return texture;
        RenderTexture render_texture = RenderTexture.GetTemporary(src.width, src.height);
        Graphics.Blit(src, render_texture);
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = render_texture;
        Texture2D new_texture = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
        new_texture.ReadPixels(new Rect(0, 0, render_texture.width, render_texture.height), 0, 0);
        new_texture.Apply();
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(render_texture);
        cache[src] = new_texture;
        return new_texture;
    }

    static List<string> GetTextureProperties(Material material) {
        List<string> properties = new();
        Shader shader = material.shader;
        for (int i = 0; i < shader.GetPropertyCount(); i++) {
            if (shader.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Texture) {
                string property_Name = shader.GetPropertyName(i);
                if (!EXCLUDED_TEXTURE_PROPERTIES.Contains(property_Name) && material.GetTexture(property_Name))
                    properties.Add(property_Name);
            }
        }
        return properties;
    }

    static HashSet<string> GetTextureProperties(SkinnedMeshRenderer[] renderers) {
        HashSet<string> properties = new();
        foreach (var renderer in renderers) {
            foreach (var material in renderer.sharedMaterials) {
                if (material)
                    foreach (var property in GetTextureProperties(material))
                        properties.Add(property);
            }
        }
        return properties;
    }

    static Dictionary<Material, Texture2D> GetTexturesForProperty(SkinnedMeshRenderer[] renderers, string propertyName, Dictionary<Texture2D, Texture2D> cache, HashSet<Texture2D> globalTextures, Dictionary<Texture2D, Texture2D> processedTextures) {
        Dictionary<Material, Texture2D> textures = new();
        foreach (var renderer in renderers) {
            if (renderer && (renderer.sharedMaterials == null))
                continue;
            foreach (var material in renderer.sharedMaterials) {
                if (!material || !material.HasProperty(propertyName))
                    continue;
                Texture2D texture = material.GetTexture(propertyName) as Texture2D;
                if (texture) {
                    Texture2D readable = MakeReadable(texture, cache);
                    if (RULES.TryGetValue(propertyName, out var rule) && rule.useDilate)
                        readable = Dilate(readable);
                    textures[material] = readable;
                }
            }
        }
        return textures;
    }

    static Dictionary<string, Dictionary<Material, Texture2D>> CollectTextureProperties(SkinnedMeshRenderer[] renderers, HashSet<Texture2D> globalTextures, Dictionary<Texture2D, Texture2D> processedTextures) {
        Dictionary<string, Dictionary<Material, Texture2D>> textureProperties = new();
        Dictionary<Texture2D, Texture2D> cache = new();
        foreach (string v in GetTextureProperties(renderers)) {
            textureProperties[v] = GetTexturesForProperty(renderers, v, cache, globalTextures, processedTextures);
        }
        return textureProperties;
    }

    static Dictionary<string, Texture2D> BuildPropertyAtlases(Dictionary<string, Dictionary<Material, Texture2D>> textureProperties, int size, out Dictionary<Texture2D, Rect> uvMap) {
        Dictionary<string, Texture2D> atlases = new();
        uvMap = new Dictionary<Texture2D, Rect>();
        foreach (var property in textureProperties) {
            if (ShouldAtlasProperty(property.Value)) {
                List<Texture2D> textures = new();
                HashSet<Texture2D> uniqueTextures = new();
                foreach (var texture in property.Value.Values) {
                    if (texture && uniqueTextures.Add(texture))
                        textures.Add(texture);
                }
                textures.Sort((x, y) => (y.width * y.height).CompareTo(x.width * x.height));
                Texture2D atlas = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
                Texture2D[] textureArray = textures.ToArray();
                Rect[] rects = atlas.PackTextures(textureArray, 2, size);
                if (RULES.ContainsKey(property.Key))
                    for (int i = 0; i < textureArray.Length; i++)
                        uvMap[textureArray[i]] = rects[i];
                atlas.Apply();
                atlas.filterMode = FilterMode.Bilinear;
                atlases[property.Key] = atlas;
            }
        }
        return atlases;
    }

    static void ApplyAtlasesToMaterial(Material material, Dictionary<string, Texture2D> atlases) {
        foreach (var atlas in atlases) {
            if (material.HasProperty(atlas.Key))
                material.SetTexture(atlas.Key, atlas.Value);
        }
    }

    static bool ShouldAtlasProperty(Dictionary<Material, Texture2D> data) {
        if (data == null || data.Count <= 1)
            return false;
        Texture2D first = null;
        foreach (var kvp in data) {
            if (!first)
                first = kvp.Value;
            else if (kvp.Value != first)
                return true;
        }
        return false;
    }

    static Vector2[] GetUV(Mesh mesh, int channel) {
        if (channel == 0)
            return mesh.uv;
        if (channel == 1)
            return mesh.uv2;
        if (channel == 2)
            return mesh.uv3;
        if (channel == 3)
            return mesh.uv4;
        List<Vector2> uvList = new();
        mesh.GetUVs(channel, uvList);
        return uvList.Count > 0 ? uvList.ToArray() : mesh.uv;
    }

    static void SetUV(Mesh mesh, int channel, Vector2[] uv) {
        if (channel == 0)
            mesh.uv = uv;
        else if (channel == 1)
            mesh.uv2 = uv;
        else if (channel == 2)
            mesh.uv3 = uv;
        else if (channel == 3)
            mesh.uv4 = uv;
        else {
            List<Vector2> uvList = new();
            for (int i = 0; i < uv.Length; i++)
                uvList.Add(uv[i]);
            mesh.SetUVs(channel, uvList);
        }
    }

    static Mesh SplitMeshForUVSafety(Mesh mesh) {
        Mesh newMesh = Object.Instantiate(mesh);
        Vector3[] vertices = mesh.vertices;
        Vector3[] normals = mesh.normals;
        Vector2[] uvs = mesh.uv;
        int subMeshCount = mesh.subMeshCount;
        List<Vector3> outVertices = new();
        List<Vector3> outNormals = new();
        List<Vector2> outUVs = new();
        List<int> outTriangles = new();
        Dictionary<int, int> vertexMap = new();
        for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++) {
            int[] triangles = mesh.GetTriangles(subMeshIndex);
            for (int i = 0; i < triangles.Length; i++) {
                int oldVertexIndex = triangles[i];
                if (!vertexMap.TryGetValue(oldVertexIndex, out int newVertexIndex)) {
                    newVertexIndex = outVertices.Count;
                    vertexMap[oldVertexIndex] = newVertexIndex;
                    outVertices.Add(vertices[oldVertexIndex]);
                    if (normals != null && normals.Length > oldVertexIndex)
                        outNormals.Add(normals[oldVertexIndex]);
                    if (uvs != null && uvs.Length > oldVertexIndex)
                        outUVs.Add(uvs[oldVertexIndex]);
                    else
                        outUVs.Add(Vector2.zero);
                }
                outTriangles.Add(newVertexIndex);
            }
        }
        newMesh.SetVertices(outVertices);
        newMesh.SetNormals(outNormals);
        newMesh.SetUVs(0, outUVs);
        newMesh.triangles = outTriangles.ToArray();
        return newMesh;
    }
}