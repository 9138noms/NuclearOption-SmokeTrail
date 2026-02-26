using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Mirage;
using Mirage.Serialization;

namespace SmokeTrail
{
    // ========== NETWORK MESSAGE ==========

    public struct SmokeNetMessage
    {
        public uint netId;      // NetworkIdentity netId to identify aircraft
        public bool active;     // smoke on/off
        public float r, g, b;   // color
        public float opacity;
        public float size;
        public float lifetime;
        public float rate;
    }

    // ========== SMOKE STATE ==========

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

    // ========== SMOKE MANAGER ==========

    public static class SmokeManager
    {
        public static Dictionary<int, SmokeTrailState> states = new Dictionary<int, SmokeTrailState>();

        public static SmokeTrailState GetOrCreate(Aircraft ac)
        {
            int id = ac.GetInstanceID();
            if (states.TryGetValue(id, out var s) && s.smokeObj != null) return s;

            s = new SmokeTrailState { aircraft = ac };

            s.smokeObj = new GameObject("SmokeTrail");
            s.smokeObj.transform.SetParent(ac.transform, false);
            s.smokeObj.transform.localPosition = new Vector3(0, 0, -5f);

            s.ps = s.smokeObj.AddComponent<ParticleSystem>();
            var emission = s.ps.emission;
            emission.enabled = false;

            var main = s.ps.main;
            main.startLifetime = s.lifetime;
            main.startSpeed = 0f;
            main.startSize = s.size;
            main.startColor = s.color;
            main.simulationSpace = ParticleSystemSimulationSpace.Custom;
            main.customSimulationSpace = Datum.origin;
            main.maxParticles = 5000;
            main.gravityModifier = 0f;

            var sol = s.ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 0.5f), new Keyframe(0.3f, 1f), new Keyframe(1f, 1.5f)));

            var col = s.ps.colorOverLifetime;
            col.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new GradientAlphaKey[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.6f, 0.5f), new GradientAlphaKey(0f, 1f) });
            col.color = gradient;

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

        public static void ApplyNetMessage(SmokeNetMessage msg)
        {
            // Find aircraft by NetworkIdentity netId
            var aircraft = FindAircraftByNetId(msg.netId);
            if (aircraft == null) return;

            var state = GetOrCreate(aircraft);
            state.color = new Color(msg.r, msg.g, msg.b);
            state.opacity = msg.opacity;
            state.size = msg.size;
            state.lifetime = msg.lifetime;
            state.rate = msg.rate;
            UpdateSettings(state);
            SetActive(state, msg.active);
        }

        public static Aircraft FindAircraftByNetId(uint netId)
        {
            foreach (var ac in UnityEngine.Object.FindObjectsOfType<Aircraft>())
            {
                if (ac == null || ac.disabled) continue;
                var ni = ac.GetComponent<NetworkIdentity>();
                if (ni != null && ni.NetId == netId)
                    return ac;
            }
            return null;
        }

        public static uint GetNetId(Aircraft ac)
        {
            var ni = ac.GetComponent<NetworkIdentity>();
            return ni != null ? ni.NetId : 0;
        }

        public static SmokeNetMessage StateToMessage(SmokeTrailState s)
        {
            return new SmokeNetMessage
            {
                netId = GetNetId(s.aircraft),
                active = s.active,
                r = s.color.r,
                g = s.color.g,
                b = s.color.b,
                opacity = s.opacity,
                size = s.size,
                lifetime = s.lifetime,
                rate = s.rate
            };
        }

        private static Material cachedSmokeMat;
        private static Material CreateSmokeMaterial()
        {
            if (cachedSmokeMat != null) return cachedSmokeMat;

            int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float center = size / 2f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x, y), new Vector2(center, center)) / center;
                    float alpha = Mathf.Clamp01(1f - dist * dist);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            tex.Apply();

            Shader sh = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                ?? Shader.Find("Particles/Standard Unlit")
                ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended")
                ?? Shader.Find("Sprites/Default");

            if (sh == null)
            {
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
            try
            {
                mat.SetFloat("_Surface", 1);
                mat.SetFloat("_Blend", 0);
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

    // ========== NETWORK MANAGER ==========

    public static class SmokeNetwork
    {
        private static bool registered;
        private static NetworkServer cachedServer;
        private static NetworkClient cachedClient;

        public static bool IsMultiplayer => cachedServer != null || cachedClient != null;
        public static bool IsServer => cachedServer != null && cachedServer.Active;

        public static void Initialize()
        {
            if (registered) return;

            try
            {
                // Register custom serializer for SmokeNetMessage
                RegisterSerializers();
                registered = true;
                Plugin.Log?.LogInfo("SmokeNetwork serializers registered");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"SmokeNetwork init failed: {e}");
            }
        }

        private static void RegisterSerializers()
        {
            // Use reflection to set Writer<SmokeNetMessage>.Write and Reader<SmokeNetMessage>.Read
            var writerType = typeof(Writer<>).MakeGenericType(typeof(SmokeNetMessage));
            var writerProp = writerType.GetProperty("Write", BindingFlags.Public | BindingFlags.Static);
            if (writerProp == null)
                writerProp = writerType.GetProperty("Write", BindingFlags.NonPublic | BindingFlags.Static);

            // Try field directly if property setter fails
            var writerField = writerType.GetField("<Write>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static);

            Action<NetworkWriter, SmokeNetMessage> writeFunc = (writer, msg) =>
            {
                writer.WriteUInt32(msg.netId);
                writer.WriteBoolean(msg.active);
                writer.WriteSingle(msg.r);
                writer.WriteSingle(msg.g);
                writer.WriteSingle(msg.b);
                writer.WriteSingle(msg.opacity);
                writer.WriteSingle(msg.size);
                writer.WriteSingle(msg.lifetime);
                writer.WriteSingle(msg.rate);
            };

            if (writerProp != null && writerProp.CanWrite)
                writerProp.SetValue(null, writeFunc);
            else if (writerField != null)
                writerField.SetValue(null, writeFunc);
            else
                Plugin.Log?.LogWarning("Could not register Writer<SmokeNetMessage>");

            var readerType = typeof(Reader<>).MakeGenericType(typeof(SmokeNetMessage));
            var readerProp = readerType.GetProperty("Read", BindingFlags.Public | BindingFlags.Static);
            if (readerProp == null)
                readerProp = readerType.GetProperty("Read", BindingFlags.NonPublic | BindingFlags.Static);

            var readerField = readerType.GetField("<Read>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static);

            Func<NetworkReader, SmokeNetMessage> readFunc = (reader) =>
            {
                return new SmokeNetMessage
                {
                    netId = reader.ReadUInt32(),
                    active = reader.ReadBoolean(),
                    r = reader.ReadSingle(),
                    g = reader.ReadSingle(),
                    b = reader.ReadSingle(),
                    opacity = reader.ReadSingle(),
                    size = reader.ReadSingle(),
                    lifetime = reader.ReadSingle(),
                    rate = reader.ReadSingle()
                };
            };

            if (readerProp != null && readerProp.CanWrite)
                readerProp.SetValue(null, readFunc);
            else if (readerField != null)
                readerField.SetValue(null, readFunc);
            else
                Plugin.Log?.LogWarning("Could not register Reader<SmokeNetMessage>");
        }

        public static void TryFindNetwork()
        {
            if (cachedServer == null)
                cachedServer = UnityEngine.Object.FindObjectOfType<NetworkServer>();
            if (cachedClient == null)
                cachedClient = UnityEngine.Object.FindObjectOfType<NetworkClient>();
        }

        public static void RegisterClientHandler()
        {
            if (cachedClient == null) return;
            try
            {
                var handler = cachedClient.MessageHandler;
                if (handler == null) return;

                // RegisterHandler<SmokeNetMessage>(callback, allowUnauthenticated: true)
                var method = handler.GetType().GetMethod("RegisterHandler");
                if (method == null) return;

                var genericMethod = method.MakeGenericMethod(typeof(SmokeNetMessage));

                // Create the delegate: MessageDelegateWithPlayer<SmokeNetMessage>
                // which is Action<INetworkPlayer, SmokeNetMessage>
                var delegateType = typeof(MessageDelegateWithPlayer<>).MakeGenericType(typeof(SmokeNetMessage));

                var callbackMethod = typeof(SmokeNetwork).GetMethod(nameof(OnClientReceiveSmoke),
                    BindingFlags.Public | BindingFlags.Static);

                var del = Delegate.CreateDelegate(delegateType, callbackMethod);
                genericMethod.Invoke(handler, new object[] { del, true });

                Plugin.Log?.LogInfo("Client handler registered for SmokeNetMessage");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"RegisterClientHandler failed: {e}");
            }
        }

        public static void OnClientReceiveSmoke(INetworkPlayer player, SmokeNetMessage msg)
        {
            try
            {
                SmokeManager.ApplyNetMessage(msg);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"OnClientReceiveSmoke error: {e}");
            }
        }

        public static void SendToAll(SmokeNetMessage msg)
        {
            if (cachedServer == null || !cachedServer.Active) return;
            try
            {
                // NetworkServer.SendToAll<SmokeNetMessage>(msg, false, Channel.Reliable)
                var method = cachedServer.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "SendToAll" && m.IsGenericMethod && m.GetParameters().Length == 3)
                    .FirstOrDefault();

                if (method == null) return;

                var genericMethod = method.MakeGenericMethod(typeof(SmokeNetMessage));
                genericMethod.Invoke(cachedServer, new object[] { msg, false, Channel.Reliable });
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"SendToAll failed: {e}");
            }
        }

        public static void OnSceneChange()
        {
            cachedServer = null;
            cachedClient = null;
        }
    }

    // ========== UI ==========

    public class SmokeTrailUI
    {
        private bool visible;
        private Rect windowRect = new Rect(20, 20, 340, 520);
        private int windowId = 94712;
        private Vector2 scrollPos;

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

        private bool networkSetup;
        private string bindingTarget; // null, "UI", or "Smoke"
        private float bindingTimer;
        private float postBindCooldown;
        private float uiToggleCooldown;
        private float smokeToggleCooldown;
        private HashSet<KeyCode> keysHeldAtBindStart = new HashSet<KeyCode>();

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
            // Key binding capture mode
            if (bindingTarget != null)
            {
                bindingTimer += Time.deltaTime;
                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    bindingTarget = null;
                    bindingTimer = 0f;
                }
                else if (bindingTimer > 0.3f && Input.anyKeyDown)
                {
                    var pressed = GetPressedKey(keysHeldAtBindStart);
                    if (pressed != KeyCode.None)
                    {
                        if (bindingTarget == "UI")    Plugin.KeyToggleUI.Value    = pressed;
                        if (bindingTarget == "Smoke") Plugin.KeyToggleSmoke.Value = pressed;
                        bindingTarget = null;
                        bindingTimer = 0f;
                        postBindCooldown = 0.3f;
                        keysHeldAtBindStart.Clear();
                    }
                }
                return; // don't process normal input while binding
            }

            postBindCooldown -= Time.deltaTime;
            if (postBindCooldown > 0f) return;

            uiToggleCooldown -= Time.deltaTime;
            smokeToggleCooldown -= Time.deltaTime;

            if (Input.GetKeyDown(Plugin.KeyToggleUI.Value) && uiToggleCooldown <= 0f)
            {
                visible = !visible;
                uiToggleCooldown = 0.5f;
            }

            // Quick toggle smoke on current aircraft
            if (Input.GetKeyDown(Plugin.KeyToggleSmoke.Value) && smokeToggleCooldown <= 0f)
            {
                try
                {
                    var localAc = FindLocalAircraft();
                    if (localAc != null)
                    {
                        var state = SmokeManager.GetOrCreate(localAc);
                        SmokeManager.SetActive(state, !state.active);
                        BroadcastState(state);
                        smokeToggleCooldown = 0.5f;
                    }
                }
                catch { }
            }

            // Try to setup network once
            if (!networkSetup)
            {
                SmokeNetwork.TryFindNetwork();
                if (SmokeNetwork.IsMultiplayer)
                {
                    SmokeNetwork.RegisterClientHandler();
                    networkSetup = true;
                }
            }
        }

        private void SnapshotHeldKeys()
        {
            keysHeldAtBindStart.Clear();
            foreach (KeyCode kc in System.Enum.GetValues(typeof(KeyCode)))
            {
                if (kc == KeyCode.None) continue;
                if (kc >= KeyCode.Mouse0 && kc <= KeyCode.Mouse6) continue;
                try { if (Input.GetKey(kc)) keysHeldAtBindStart.Add(kc); } catch { }
            }
        }

        private static KeyCode GetPressedKey(HashSet<KeyCode> exclude)
        {
            foreach (KeyCode kc in System.Enum.GetValues(typeof(KeyCode)))
            {
                if (kc == KeyCode.None) continue;
                if (kc >= KeyCode.Mouse0 && kc <= KeyCode.Mouse6) continue;
                if (exclude.Contains(kc)) continue; // 바인딩 시작 시 이미 눌려있던 키 제외
                if (Input.GetKeyDown(kc)) return kc;
            }
            return KeyCode.None;
        }

        public void OnGUI()
        {
            if (!visible) return;
            InitStyles();
            windowRect = GUILayout.Window(windowId, windowRect, DrawWindow, "Smoke Trail v2.1.0");
        }

        private Aircraft selectedAircraft;

        private void DrawWindow(int id)
        {
            try
            {
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

            // Network status
            if (SmokeNetwork.IsMultiplayer)
            {
                string role = SmokeNetwork.IsServer ? "HOST" : "CLIENT";
                GUILayout.Label($"Multiplayer: {role}", wpLabelStyle);
            }
            else
            {
                GUILayout.Label("Singleplayer", wpLabelStyle);
            }
            GUILayout.Space(4);

            // Keybinds section
            GUILayout.Label("Keybinds", labelStyle);
            if (bindingTarget != null)
            {
                GUILayout.Label("Press any key or HOTAS button... (ESC to cancel)", wpLabelStyle);
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Toggle UI:", wpLabelStyle, GUILayout.Width(80));
                if (GUILayout.Button(Plugin.KeyToggleUI.Value.ToString(), smallButtonStyle, GUILayout.Width(120)))
                { bindingTarget = "UI"; bindingTimer = 0f; SnapshotHeldKeys(); }
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("Toggle Smoke:", wpLabelStyle, GUILayout.Width(80));
                if (GUILayout.Button(Plugin.KeyToggleSmoke.Value.ToString(), smallButtonStyle, GUILayout.Width(120)))
                { bindingTarget = "Smoke"; bindingTimer = 0f; SnapshotHeldKeys(); }
                GUILayout.EndHorizontal();
            }
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
                    BroadcastState(state);
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
                        BroadcastState(state);
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
                    BroadcastState(state);
                }

                // Color preview
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
                float newOpacity = GUILayout.HorizontalSlider(state.opacity, 0.1f, 1f);

                GUILayout.Label($"Size: {state.size:F0}", wpLabelStyle);
                float newSize = GUILayout.HorizontalSlider(state.size, 1f, 30f);

                GUILayout.Label($"Lifetime: {state.lifetime:F1}s", wpLabelStyle);
                float newLifetime = GUILayout.HorizontalSlider(state.lifetime, 1f, 20f);

                GUILayout.Label($"Rate: {state.rate:F0}/s", wpLabelStyle);
                float newRate = GUILayout.HorizontalSlider(state.rate, 10f, 200f);

                bool sliderChanged = false;
                if (newOpacity != state.opacity || newSize != state.size || newLifetime != state.lifetime || newRate != state.rate)
                {
                    state.opacity = newOpacity;
                    state.size = newSize;
                    state.lifetime = newLifetime;
                    state.rate = newRate;
                    sliderChanged = true;
                }

                if (sliderChanged || GUI.changed)
                {
                    SmokeManager.UpdateSettings(state);
                    BroadcastState(state);
                }

                GUILayout.Space(4);

                // Batch controls
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("All ON", smallButtonStyle))
                {
                    foreach (var item in aiList)
                    {
                        var s = SmokeManager.GetOrCreate(item.ac);
                        SmokeManager.SetActive(s, true);
                        BroadcastState(s);
                    }
                }
                if (GUILayout.Button("All OFF", smallButtonStyle))
                {
                    foreach (var kv in SmokeManager.states)
                    {
                        SmokeManager.SetActive(kv.Value, false);
                        BroadcastState(kv.Value);
                    }
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

        private void BroadcastState(SmokeTrailState state)
        {
            if (!SmokeNetwork.IsServer) return;
            if (state.aircraft == null) return;

            var msg = SmokeManager.StateToMessage(state);
            if (msg.netId == 0) return; // no network identity (singleplayer or not spawned)

            SmokeNetwork.SendToAll(msg);
        }

        private Aircraft FindLocalAircraft()
        {
            foreach (var ac in UnityEngine.Object.FindObjectsOfType<Aircraft>())
            {
                if (ac == null || ac.disabled) continue;
                if (GameManager.IsLocalAircraft(ac)) return ac;
            }
            return null;
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

    [BepInPlugin("com.noms.smoketrail", "Smoke Trail", "2.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static ConfigEntry<KeyCode> KeyToggleUI;
        internal static ConfigEntry<KeyCode> KeyToggleSmoke;

        private static bool helperCreated;

        private void Awake()
        {
            Log = Logger;
            Logger.LogInfo("Smoke Trail v2.1.0 loaded");

            KeyToggleUI = Config.Bind("Keybinds", "ToggleUI", KeyCode.F7,
                "Key to open/close the Smoke Trail UI. Supports keyboard keys and HOTAS joystick buttons (e.g. JoystickButton0).");
            KeyToggleSmoke = Config.Bind("Keybinds", "ToggleSmoke", KeyCode.F8,
                "Key to quickly toggle smoke on your aircraft. Supports keyboard keys and HOTAS joystick buttons (e.g. JoystickButton1).");

            // Initialize network serializers early
            SmokeNetwork.Initialize();

            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            SmokeNetwork.OnSceneChange();

            if (helperCreated && FrameHelper.Instance != null) return;
            var go = new GameObject("SmokeTrail_Helper");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<FrameHelper>();
            helperCreated = true;
            Log?.LogInfo("FrameHelper created on standalone GameObject");
        }
    }
}
