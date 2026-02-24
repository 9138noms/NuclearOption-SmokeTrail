using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SmokeTrail
{
    public class SmokeTrailState
    {
        public Aircraft aircraft;
        public GameObject smokeObj;
        public ParticleSystem ps;
        public bool active;
        public Color color = Color.white;
        public float opacity = 0.8f;
        public float size = 8f;
        public float lifetime = 6f;
        public float rate = 60f;
    }

    public static class SmokeManager
    {
        public static Dictionary<int, SmokeTrailState> states = new Dictionary<int, SmokeTrailState>();

        public static SmokeTrailState GetOrCreate(Aircraft ac)
        {
            int id = ac.GetInstanceID();
            if (states.TryGetValue(id, out var s) && s.smokeObj != null) return s;

            s = new SmokeTrailState { aircraft = ac };

            // Create smoke particle system on the aircraft
            s.smokeObj = new GameObject("SmokeTrail");
            s.smokeObj.transform.SetParent(ac.transform, false);
            s.smokeObj.transform.localPosition = new Vector3(0, 0, -5f); // behind aircraft

            s.ps = s.smokeObj.AddComponent<ParticleSystem>();
            var emission = s.ps.emission;
            emission.enabled = false; // start off

            var main = s.ps.main;
            main.startLifetime = s.lifetime;
            main.startSpeed = 0f;
            main.startSize = s.size;
            main.startColor = s.color;
            main.simulationSpace = ParticleSystemSimulationSpace.Custom;
            main.customSimulationSpace = Datum.origin;
            main.maxParticles = 5000;
            main.gravityModifier = 0f;

            // Size over lifetime — grow slightly
            var sol = s.ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 0.5f), new Keyframe(0.3f, 1f), new Keyframe(1f, 1.5f)));

            // Color over lifetime — fade out
            var col = s.ps.colorOverLifetime;
            col.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new GradientAlphaKey[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.6f, 0.5f), new GradientAlphaKey(0f, 1f) });
            col.color = gradient;

            // Renderer — use procedural soft circle texture
            var renderer = s.smokeObj.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.material = CreateSmokeMaterial();

            s.ps.Stop();
            states[id] = s;
            return s;
        }

        public static void SetActive(SmokeTrailState s, bool on)
        {
            s.active = on;
            var emission = s.ps.emission;
            emission.rateOverTime = on ? s.rate : 0f;
            emission.enabled = on;
            if (on) s.ps.Play();
            else s.ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }

        public static void UpdateSettings(SmokeTrailState s)
        {
            var main = s.ps.main;
            main.startLifetime = s.lifetime;
            main.startSize = s.size;
            var c = s.color;
            c.a = s.opacity;
            main.startColor = c;
            var emission = s.ps.emission;
            if (s.active) emission.rateOverTime = s.rate;
        }

        private static Material cachedSmokeMat;
        private static Material CreateSmokeMaterial()
        {
            if (cachedSmokeMat != null) return cachedSmokeMat;

            // Create soft circle texture procedurally
            int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float center = size / 2f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x, y), new Vector2(center, center)) / center;
                    float alpha = Mathf.Clamp01(1f - dist * dist); // soft falloff
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            tex.Apply();

            // Find a working shader
            Shader sh = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                ?? Shader.Find("Particles/Standard Unlit")
                ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended")
                ?? Shader.Find("Sprites/Default");

            if (sh == null)
            {
                // Last resort: grab shader from any existing particle renderer
                foreach (var psr in UnityEngine.Object.FindObjectsOfType<ParticleSystemRenderer>())
                {
                    if (psr.material != null && psr.material.shader != null)
                    {
                        sh = psr.material.shader;
                        break;
                    }
                }
            }

            var mat = new Material(sh);
            mat.mainTexture = tex;
            // Try to set alpha blending
            try
            {
                mat.SetFloat("_Surface", 1); // URP: 0=Opaque, 1=Transparent
                mat.SetFloat("_Blend", 0);   // URP: 0=Alpha, 1=Premultiply, 2=Additive, 3=Multiply
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.renderQueue = 3000;
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
            }
            catch { }

            cachedSmokeMat = mat;
            return mat;
        }

        public static void Cleanup()
        {
            var toRemove = new List<int>();
            foreach (var kv in states)
            {
                if (kv.Value.aircraft == null || kv.Value.aircraft.disabled)
                {
                    if (kv.Value.smokeObj != null) UnityEngine.Object.Destroy(kv.Value.smokeObj);
                    toRemove.Add(kv.Key);
                }
            }
            foreach (var id in toRemove) states.Remove(id);
        }
    }

    public class SmokeTrailUI
    {
        private bool visible;
        private Rect windowRect = new Rect(20, 20, 340, 500);
        private int windowId = 94712;
        private Vector2 scrollPos;

        // Color presets
        private static readonly (string name, Color color)[] presets = new[]
        {
            ("White", Color.white),
            ("Red", Color.red),
            ("Blue", new Color(0.2f, 0.4f, 1f)),
            ("Green", new Color(0.2f, 1f, 0.2f)),
            ("Yellow", Color.yellow),
            ("Orange", new Color(1f, 0.5f, 0f)),
            ("Magenta", Color.magenta),
            ("Cyan", Color.cyan),
            ("Black", new Color(0.2f, 0.2f, 0.2f)),
        };

        private GUIStyle buttonStyle, activeButtonStyle, labelStyle, headerStyle, smallButtonStyle, wpLabelStyle, textFieldStyle;
        private bool stylesInit;
        private Texture2D previewTex;
        private GUIStyle previewStyle;

        private void InitStyles()
        {
            if (stylesInit) return;
            stylesInit = true;

            buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 12, padding = new RectOffset(6, 6, 4, 4) };
            activeButtonStyle = new GUIStyle(buttonStyle) { fontStyle = FontStyle.Bold };
            activeButtonStyle.normal.textColor = Color.green;
            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold };
            headerStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            smallButtonStyle = new GUIStyle(GUI.skin.button) { fontSize = 11, padding = new RectOffset(4, 4, 2, 2) };
            wpLabelStyle = new GUIStyle(GUI.skin.label) { fontSize = 11 };
            textFieldStyle = new GUIStyle(GUI.skin.textField) { fontSize = 12 };
        }

        public void HandleInput()
        {
            if (Input.GetKeyDown(KeyCode.F7))
                visible = !visible;
        }

        public void OnGUI()
        {
            if (!visible) return;
            InitStyles();
            windowRect = GUILayout.Window(windowId, windowRect, DrawWindow, "Smoke Trail v1.0.0");
        }

        private Aircraft selectedAircraft;

        private void DrawWindow(int id)
        {
            try
            {
            // Aircraft list
            var allAircraft = UnityEngine.Object.FindObjectsOfType<Aircraft>();
            var aiList = new List<(Aircraft ac, string name)>();

            foreach (var ac in allAircraft)
            {
                if (ac == null || ac.disabled) continue;
                string dname = "Aircraft";
                try { if (ac.definition != null) dname = ac.definition.name; } catch { }
                string uname = "";
                try
                {
                    if (ac.SavedUnit != null)
                        uname = ac.SavedUnit.UniqueName ?? "";
                    else if (!string.IsNullOrEmpty(ac.UniqueName))
                        uname = ac.UniqueName;
                }
                catch { }
                string display = string.IsNullOrEmpty(uname) ? dname : $"{dname} ({uname})";
                aiList.Add((ac, display));
            }

            GUILayout.Label("Smoke Trail", headerStyle);
            GUILayout.Label("F7 to toggle | Select aircraft, toggle smoke", wpLabelStyle);
            GUILayout.Space(4);

            // Aircraft scroll
            GUILayout.Label($"Aircraft ({aiList.Count})", labelStyle);
            scrollPos = GUILayout.BeginScrollView(scrollPos, GUILayout.Height(150));
            for (int i = 0; i < aiList.Count; i++)
            {
                var (ac, name) = aiList[i];
                bool isSelected = (selectedAircraft != null && ac == selectedAircraft);
                int acId = ac.GetInstanceID();
                bool hasSmokeActive = SmokeManager.states.TryGetValue(acId, out var st) && st.active;

                GUILayout.BeginHorizontal();
                var style = isSelected ? activeButtonStyle : buttonStyle;
                if (GUILayout.Button(name, style, GUILayout.Width(220)))
                    selectedAircraft = ac;
                if (hasSmokeActive)
                    GUILayout.Label("SMOKE", wpLabelStyle);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            GUILayout.Space(8);

            // Selected aircraft controls
            if (selectedAircraft != null && !selectedAircraft.disabled)
            {
                var state = SmokeManager.GetOrCreate(selectedAircraft);
                string selName = "Aircraft";
                try { if (selectedAircraft.definition != null) selName = selectedAircraft.definition.name; } catch { }

                GUILayout.Label($"Selected: {selName}", labelStyle);
                GUILayout.Space(4);

                // Toggle
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(state.active ? "SMOKE ON" : "SMOKE OFF",
                    state.active ? activeButtonStyle : buttonStyle, GUILayout.Height(30)))
                {
                    SmokeManager.SetActive(state, !state.active);
                }
                GUILayout.EndHorizontal();

                GUILayout.Space(4);

                // Color presets
                GUILayout.Label("Color", labelStyle);
                GUILayout.BeginHorizontal();
                int count = 0;
                foreach (var (pname, pcolor) in presets)
                {
                    var colorStyle = new GUIStyle(smallButtonStyle);
                    if (ColorClose(state.color, pcolor))
                    {
                        colorStyle.fontStyle = FontStyle.Bold;
                        colorStyle.normal.textColor = Color.green;
                    }
                    if (GUILayout.Button(pname, colorStyle, GUILayout.Width(55)))
                    {
                        state.color = pcolor;
                        SmokeManager.UpdateSettings(state);
                    }
                    count++;
                    if (count % 5 == 0) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
                }
                GUILayout.EndHorizontal();

                GUILayout.Space(4);

                // Custom RGB sliders
                GUILayout.Label($"R: {state.color.r:F2}", wpLabelStyle);
                float r = GUILayout.HorizontalSlider(state.color.r, 0f, 1f);
                GUILayout.Label($"G: {state.color.g:F2}", wpLabelStyle);
                float g = GUILayout.HorizontalSlider(state.color.g, 0f, 1f);
                GUILayout.Label($"B: {state.color.b:F2}", wpLabelStyle);
                float b = GUILayout.HorizontalSlider(state.color.b, 0f, 1f);
                if (r != state.color.r || g != state.color.g || b != state.color.b)
                {
                    state.color = new Color(r, g, b);
                    SmokeManager.UpdateSettings(state);
                }

                // Color preview using 1x1 texture
                if (previewTex == null)
                {
                    previewTex = new Texture2D(1, 1);
                    previewStyle = new GUIStyle();
                }
                previewTex.SetPixel(0, 0, state.color);
                previewTex.Apply();
                previewStyle.normal.background = previewTex;
                GUILayout.Box(GUIContent.none, previewStyle, GUILayout.Height(16), GUILayout.ExpandWidth(true));

                GUILayout.Space(4);

                // Sliders
                GUILayout.Label($"Opacity: {state.opacity:F1}", wpLabelStyle);
                state.opacity = GUILayout.HorizontalSlider(state.opacity, 0.1f, 1f);

                GUILayout.Label($"Size: {state.size:F0}", wpLabelStyle);
                state.size = GUILayout.HorizontalSlider(state.size, 1f, 30f);

                GUILayout.Label($"Lifetime: {state.lifetime:F1}s", wpLabelStyle);
                state.lifetime = GUILayout.HorizontalSlider(state.lifetime, 1f, 20f);

                GUILayout.Label($"Rate: {state.rate:F0}/s", wpLabelStyle);
                state.rate = GUILayout.HorizontalSlider(state.rate, 10f, 200f);

                if (GUI.changed)
                    SmokeManager.UpdateSettings(state);

                GUILayout.Space(4);

                // Batch controls
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("All ON", smallButtonStyle))
                {
                    foreach (var item in aiList)
                    {
                        var s = SmokeManager.GetOrCreate(item.ac);
                        SmokeManager.SetActive(s, true);
                    }
                }
                if (GUILayout.Button("All OFF", smallButtonStyle))
                {
                    foreach (var kv in SmokeManager.states)
                        SmokeManager.SetActive(kv.Value, false);
                }
                GUILayout.EndHorizontal();
            }

            }
            catch (Exception e)
            {
                GUILayout.Label($"Error: {e.Message}", wpLabelStyle);
            }
            GUI.DragWindow();
        }

        private static bool ColorClose(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) < 0.05f && Mathf.Abs(a.g - b.g) < 0.05f && Mathf.Abs(a.b - b.b) < 0.05f;
    }

    // ========== FRAME HELPER ==========

    public class FrameHelper : MonoBehaviour
    {
        public static FrameHelper Instance;
        public SmokeTrailUI ui = new SmokeTrailUI();
        private float cleanupTimer;

        void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            Plugin.Log?.LogInfo("FrameHelper Awake OK");
        }


        void Update()
        {
            ui.HandleInput();
            cleanupTimer += Time.deltaTime;
            if (cleanupTimer > 5f)
            {
                cleanupTimer = 0f;
                SmokeManager.Cleanup();
            }
        }

        void OnGUI()
        {
            ui.OnGUI();
        }
    }

    // ========== PLUGIN ==========

    [BepInPlugin("com.noms.smoketrail", "Smoke Trail", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        private static bool helperCreated;

        private void Awake()
        {
            Log = Logger;
            Logger.LogInfo("Smoke Trail v1.0.0 loaded");
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (helperCreated && FrameHelper.Instance != null) return;
            var go = new GameObject("SmokeTrail_Helper");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<FrameHelper>();
            helperCreated = true;
            Log?.LogInfo("FrameHelper created on standalone GameObject");
        }
    }
}
