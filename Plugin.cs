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
        public bool wingtip;    // wingtip mode
        public float r, g, b;   // color
        public float opacity;
        public float size;
        public float lifetime;
        public float rate;
        public float wingtipX;  // half-wingspan offset
    }

    // ========== SMOKE STATE ==========

    public class SmokeTrailState
    {
        public Aircraft aircraft;
        public GameObject centerObj;
        public ParticleSystem centerPs;
        public GameObject leftObj;
        public ParticleSystem leftPs;
        public GameObject rightObj;
        public ParticleSystem rightPs;
        public bool wingtipsBuilt;
        public bool active;
        public bool wingtipMode;
        public float wingtipOffsetX = 5f; // half-wingspan (meters) used to place left/right emitters
        public float wingtipOffsetZ = -1f; // Z (forward/back) offset for wingtip emitters
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

        // Half-wingspan defaults (meters) keyed by aircraft definition.name substring.
        // Sourced from nuclearoption.wiki.gg — see WingspanDefaults below.
        public static readonly Dictionary<string, float> WingspanHalfDefaults =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            // wingspan / 2
            { "Cricket",   5.80f  },  // CI-22, 11.6m
            { "Compass",   6.05f  },  // T/A-30, 12.1m
            { "Ibis",      3.20f  },  // UH-90, 6.4m
            { "Chicane",   3.70f  },  // SAH-46, 7.4m (width)
            { "Brawler",   9.50f  },  // A-19, 19.0m
            { "Revoker",   5.50f  },  // FS-12, 11.0m
            { "Vortex",    4.65f  },  // FS-20, 9.3m
            { "Tarantula", 11.35f },  // VL-49, 22.7m
            { "Ifrit",     7.15f  },  // KR-67, 14.3m
            { "Medusa",    9.05f  },  // EW-25, 18.1m
            { "Darkreach", 18.25f },  // SFB-81, 36.5m
            { "Alkyon",    12.40f },  // AB-4 swing-wing 18.0-31.6m — midpoint, gets retuned live
        };

        public static float LookupHalfSpan(Aircraft ac)
        {
            try
            {
                var name = ac?.definition?.name ?? "";
                foreach (var kv in WingspanHalfDefaults)
                    if (name.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                        return kv.Value;
            }
            catch { }
            return 5f;
        }

        public static bool IsAlkyon(Aircraft ac)
        {
            try { return (ac?.definition?.name ?? "").IndexOf("Alkyon", StringComparison.OrdinalIgnoreCase) >= 0; }
            catch { return false; }
        }

        public static SmokeTrailState GetOrCreate(Aircraft ac)
        {
            int id = ac.GetInstanceID();
            if (states.TryGetValue(id, out var s) && s.centerObj != null) return s;

            s = new SmokeTrailState { aircraft = ac };
            // Per-aircraft default half-wingspan (overrides the 5m default)
            s.wingtipOffsetX = LookupHalfSpan(ac);

            (s.centerObj, s.centerPs) = BuildEmitter(ac.transform, "SmokeTrail_Center", new Vector3(0f, 0f, -5f));
            // Build wingtips upfront too — avoids lazy timing issues and lets us
            // simply route emission to whichever set is active.
            (s.leftObj,  s.leftPs)  = BuildEmitter(ac.transform, "SmokeTrail_WingL",
                new Vector3(-s.wingtipOffsetX, 0f, s.wingtipOffsetZ));
            (s.rightObj, s.rightPs) = BuildEmitter(ac.transform, "SmokeTrail_WingR",
                new Vector3( s.wingtipOffsetX, 0f, s.wingtipOffsetZ));
            s.wingtipsBuilt = true;
            states[id] = s;
            return s;
        }

        private static (GameObject, ParticleSystem) BuildEmitter(Transform parent, string name, Vector3 localPos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;

            var ps = go.AddComponent<ParticleSystem>();
            var emission = ps.emission;
            emission.enabled = false;

            var main = ps.main;
            main.startLifetime = 6f;
            main.startSpeed = 0f;
            main.startSize = 8f;
            main.startColor = Color.white;
            main.simulationSpace = ParticleSystemSimulationSpace.Custom;
            main.customSimulationSpace = Datum.origin;
            main.maxParticles = 5000;
            main.gravityModifier = 0f;

            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 0.5f), new Keyframe(0.3f, 1f), new Keyframe(1f, 1.5f)));

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new GradientColorKey[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new GradientAlphaKey[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.6f, 0.5f), new GradientAlphaKey(0f, 1f) });
            col.color = gradient;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.material = CreateSmokeMaterial();

            ps.Stop();
            return (go, ps);
        }

        private static void EnsureWingtips(SmokeTrailState s)
        {
            if (s.aircraft == null) return;

            // Wingtip emitters are now built upfront in GetOrCreate. If they got
            // destroyed (aircraft death cleanup), rebuild them.
            if (s.leftObj == null)
                (s.leftObj,  s.leftPs)  = BuildEmitter(s.aircraft.transform, "SmokeTrail_WingL",
                    new Vector3(-s.wingtipOffsetX, 0f, s.wingtipOffsetZ));
            if (s.rightObj == null)
                (s.rightObj, s.rightPs) = BuildEmitter(s.aircraft.transform, "SmokeTrail_WingR",
                    new Vector3( s.wingtipOffsetX, 0f, s.wingtipOffsetZ));
            s.wingtipsBuilt = (s.leftObj != null && s.rightObj != null);
            UpdateWingtipPositions(s);
        }

        public static void UpdateWingtipPositions(SmokeTrailState s)
        {
            if (s.leftObj  != null) s.leftObj.transform.localPosition  = new Vector3(-s.wingtipOffsetX, 0f, s.wingtipOffsetZ);
            if (s.rightObj != null) s.rightObj.transform.localPosition = new Vector3( s.wingtipOffsetX, 0f, s.wingtipOffsetZ);
        }

        // (Auto-fit removed — both mesh-local and world-bounds approaches were
        // unreliable. Per-aircraft wiki-sourced defaults in WingspanHalfDefaults
        // are accurate; user can fine-tune via the slider.)

        public static void SetActive(SmokeTrailState s, bool on)
        {
            s.active = on;

            if (on && s.wingtipMode) EnsureWingtips(s);

            // Center emitter — only emits when not in wingtip mode
            ApplyEmission(s.centerPs, on && !s.wingtipMode, s.rate);

            // Wingtip emitters — only emit in wingtip mode
            if (s.wingtipsBuilt)
            {
                ApplyEmission(s.leftPs,  on && s.wingtipMode, s.rate);
                ApplyEmission(s.rightPs, on && s.wingtipMode, s.rate);
            }

            // Make sure visual settings on freshly-built emitters reflect state
            if (on && s.wingtipMode && s.wingtipsBuilt)
            {
                ApplyMain(s.leftPs, s);
                ApplyMain(s.rightPs, s);
            }

            Plugin.Log?.LogInfo(
                $"[Smoke] SetActive on={on} wingtip={s.wingtipMode} built={s.wingtipsBuilt} " +
                $"centerPs={(s.centerPs != null ? "ok" : "null")} " +
                $"leftPs={(s.leftPs != null ? "ok" : "null")} " +
                $"rightPs={(s.rightPs != null ? "ok" : "null")} " +
                $"offsetX={s.wingtipOffsetX:F2}");
        }

        private static void ApplyEmission(ParticleSystem ps, bool on, float rate)
        {
            if (ps == null) return;
            var emission = ps.emission;
            emission.rateOverTime = on ? rate : 0f;
            emission.enabled = on;
            if (on) ps.Play();
            else ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }

        public static void UpdateSettings(SmokeTrailState s)
        {
            ApplyMain(s.centerPs, s);
            if (s.wingtipsBuilt)
            {
                ApplyMain(s.leftPs, s);
                ApplyMain(s.rightPs, s);
                UpdateWingtipPositions(s);
            }
            // emission rates need refresh too if active
            if (s.active) SetActive(s, true);
        }

        private static void ApplyMain(ParticleSystem ps, SmokeTrailState s)
        {
            if (ps == null) return;
            var main = ps.main;
            main.startLifetime = s.lifetime;
            main.startSize = s.size;
            var c = s.color;
            c.a = s.opacity;
            main.startColor = c;
        }

        public static void ApplyNetMessage(SmokeNetMessage msg)
        {
            var aircraft = FindAircraftByNetId(msg.netId);
            if (aircraft == null) return;

            var state = GetOrCreate(aircraft);
            state.color = new Color(msg.r, msg.g, msg.b);
            state.opacity = msg.opacity;
            state.size = msg.size;
            state.lifetime = msg.lifetime;
            state.rate = msg.rate;
            state.wingtipMode = msg.wingtip;
            if (msg.wingtipX > 0f) state.wingtipOffsetX = msg.wingtipX;
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
                wingtip = s.wingtipMode,
                r = s.color.r,
                g = s.color.g,
                b = s.color.b,
                opacity = s.opacity,
                size = s.size,
                lifetime = s.lifetime,
                rate = s.rate,
                wingtipX = s.wingtipOffsetX,
            };
        }

        // ---- profile (persisted across respawns) ----

        public static void ApplyProfile(SmokeTrailState s)
        {
            s.color = new Color(
                Plugin.ProfileColorR.Value,
                Plugin.ProfileColorG.Value,
                Plugin.ProfileColorB.Value);
            s.opacity     = Plugin.ProfileOpacity.Value;
            s.size        = Plugin.ProfileSize.Value;
            s.lifetime    = Plugin.ProfileLifetime.Value;
            s.rate        = Plugin.ProfileRate.Value;
            s.wingtipMode = Plugin.ProfileWingtip.Value;
            // wingtipOffsetX intentionally NOT restored from profile — each aircraft
            // gets its own per-type default from WingspanHalfDefaults.
        }

        public static void SaveProfile(SmokeTrailState s)
        {
            Plugin.ProfileColorR.Value     = s.color.r;
            Plugin.ProfileColorG.Value     = s.color.g;
            Plugin.ProfileColorB.Value     = s.color.b;
            Plugin.ProfileOpacity.Value    = s.opacity;
            Plugin.ProfileSize.Value       = s.size;
            Plugin.ProfileLifetime.Value   = s.lifetime;
            Plugin.ProfileRate.Value       = s.rate;
            Plugin.ProfileWingtip.Value    = s.wingtipMode;
            Plugin.ProfileWasActive.Value  = s.active;
            // wingtipOffsetX is per-aircraft; not saved globally.
        }

        // ---- material ----

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
                    if (kv.Value.centerObj != null) UnityEngine.Object.Destroy(kv.Value.centerObj);
                    if (kv.Value.leftObj   != null) UnityEngine.Object.Destroy(kv.Value.leftObj);
                    if (kv.Value.rightObj  != null) UnityEngine.Object.Destroy(kv.Value.rightObj);
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
            var writerType = typeof(Writer<>).MakeGenericType(typeof(SmokeNetMessage));
            var writerProp = writerType.GetProperty("Write", BindingFlags.Public | BindingFlags.Static)
                          ?? writerType.GetProperty("Write", BindingFlags.NonPublic | BindingFlags.Static);
            var writerField = writerType.GetField("<Write>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static);

            Action<NetworkWriter, SmokeNetMessage> writeFunc = (writer, msg) =>
            {
                writer.WriteUInt32(msg.netId);
                writer.WriteBoolean(msg.active);
                writer.WriteBoolean(msg.wingtip);
                writer.WriteSingle(msg.r);
                writer.WriteSingle(msg.g);
                writer.WriteSingle(msg.b);
                writer.WriteSingle(msg.opacity);
                writer.WriteSingle(msg.size);
                writer.WriteSingle(msg.lifetime);
                writer.WriteSingle(msg.rate);
                writer.WriteSingle(msg.wingtipX);
            };

            if (writerProp != null && writerProp.CanWrite)
                writerProp.SetValue(null, writeFunc);
            else if (writerField != null)
                writerField.SetValue(null, writeFunc);
            else
                Plugin.Log?.LogWarning("Could not register Writer<SmokeNetMessage>");

            var readerType = typeof(Reader<>).MakeGenericType(typeof(SmokeNetMessage));
            var readerProp = readerType.GetProperty("Read", BindingFlags.Public | BindingFlags.Static)
                          ?? readerType.GetProperty("Read", BindingFlags.NonPublic | BindingFlags.Static);
            var readerField = readerType.GetField("<Read>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static);

            Func<NetworkReader, SmokeNetMessage> readFunc = (reader) =>
            {
                return new SmokeNetMessage
                {
                    netId    = reader.ReadUInt32(),
                    active   = reader.ReadBoolean(),
                    wingtip  = reader.ReadBoolean(),
                    r        = reader.ReadSingle(),
                    g        = reader.ReadSingle(),
                    b        = reader.ReadSingle(),
                    opacity  = reader.ReadSingle(),
                    size     = reader.ReadSingle(),
                    lifetime = reader.ReadSingle(),
                    rate     = reader.ReadSingle(),
                    wingtipX = reader.ReadSingle(),
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

                var method = handler.GetType().GetMethod("RegisterHandler");
                if (method == null) return;

                var genericMethod = method.MakeGenericMethod(typeof(SmokeNetMessage));
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
            try { SmokeManager.ApplyNetMessage(msg); }
            catch (Exception e) { Plugin.Log?.LogError($"OnClientReceiveSmoke error: {e}"); }
        }

        public static void SendToAll(SmokeNetMessage msg)
        {
            if (cachedServer == null || !cachedServer.Active) return;
            try
            {
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
        private Rect windowRect = new Rect(20, 20, 360, 600);
        private int windowId = 94712;
        private Vector2 scrollPos;

        private static readonly (string name, Color color)[] presets = new[]
        {
            ("White",   Color.white),
            ("Red",     Color.red),
            ("Blue",    new Color(0.2f, 0.4f, 1f)),
            ("Green",   new Color(0.2f, 1f, 0.2f)),
            ("Yellow",  Color.yellow),
            ("Orange",  new Color(1f, 0.5f, 0f)),
            ("Magenta", Color.magenta),
            ("Cyan",    Color.cyan),
            ("Black",   new Color(0.2f, 0.2f, 0.2f)),
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

        // Cursor lock state we override while the window is visible — we restore
        // it the frame after the window hides so the game's locked-cursor flight
        // mode resumes cleanly.
        private bool cursorOverridden;
        private CursorLockMode savedLockState;
        private bool savedCursorVisible;

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
                    if (pressed != KeyCode.None && !IsModifier(pressed))
                    {
                        var modifiers = CurrentModifiers();
                        var combo = new KeyboardShortcut(pressed, modifiers);
                        if (bindingTarget == "UI")    Plugin.KeyToggleUI.Value    = combo;
                        if (bindingTarget == "Smoke") Plugin.KeyToggleSmoke.Value = combo;
                        bindingTarget = null;
                        bindingTimer = 0f;
                        postBindCooldown = 0.3f;
                        keysHeldAtBindStart.Clear();
                    }
                }
                return;
            }

            postBindCooldown -= Time.deltaTime;
            if (postBindCooldown > 0f) return;

            uiToggleCooldown -= Time.deltaTime;
            smokeToggleCooldown -= Time.deltaTime;

            if (Plugin.KeyToggleUI.Value.IsDown() && uiToggleCooldown <= 0f)
            {
                visible = !visible;
                uiToggleCooldown = 0.5f;
            }

            UpdateCursorOverride();

            if (Plugin.KeyToggleSmoke.Value.IsDown() && smokeToggleCooldown <= 0f)
            {
                try
                {
                    var localAc = FindLocalAircraft();
                    if (localAc != null)
                    {
                        var state = SmokeManager.GetOrCreate(localAc);
                        SmokeManager.SetActive(state, !state.active);
                        SmokeManager.SaveProfile(state);
                        BroadcastState(state);
                        smokeToggleCooldown = 0.5f;
                    }
                }
                catch { }
            }

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

        private void UpdateCursorOverride()
        {
            if (visible)
            {
                if (!cursorOverridden)
                {
                    savedLockState = Cursor.lockState;
                    savedCursorVisible = Cursor.visible;
                    cursorOverridden = true;
                }
                if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
                if (!Cursor.visible) Cursor.visible = true;
            }
            else if (cursorOverridden)
            {
                Cursor.lockState = savedLockState;
                Cursor.visible = savedCursorVisible;
                cursorOverridden = false;
            }
        }

        private static bool IsModifier(KeyCode k) =>
            k == KeyCode.LeftShift || k == KeyCode.RightShift ||
            k == KeyCode.LeftControl || k == KeyCode.RightControl ||
            k == KeyCode.LeftAlt || k == KeyCode.RightAlt ||
            k == KeyCode.LeftCommand || k == KeyCode.RightCommand ||
            k == KeyCode.LeftWindows || k == KeyCode.RightWindows;

        private static KeyCode[] CurrentModifiers()
        {
            var list = new List<KeyCode>();
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) list.Add(KeyCode.LeftShift);
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) list.Add(KeyCode.LeftControl);
            if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) list.Add(KeyCode.LeftAlt);
            return list.ToArray();
        }

        private void SnapshotHeldKeys()
        {
            keysHeldAtBindStart.Clear();
            foreach (KeyCode kc in System.Enum.GetValues(typeof(KeyCode)))
            {
                if (kc == KeyCode.None) continue;
                if (kc >= KeyCode.Mouse0 && kc <= KeyCode.Mouse6) continue;
                if (IsModifier(kc)) continue; // we DO want modifiers held as the combo prefix
                try { if (Input.GetKey(kc)) keysHeldAtBindStart.Add(kc); } catch { }
            }
        }

        private static KeyCode GetPressedKey(HashSet<KeyCode> exclude)
        {
            foreach (KeyCode kc in System.Enum.GetValues(typeof(KeyCode)))
            {
                if (kc == KeyCode.None) continue;
                if (kc >= KeyCode.Mouse0 && kc <= KeyCode.Mouse6) continue;
                if (exclude.Contains(kc)) continue;
                if (Input.GetKeyDown(kc)) return kc;
            }
            return KeyCode.None;
        }

        public void OnGUI()
        {
            if (!visible) return;
            InitStyles();
            windowRect = GUILayout.Window(windowId, windowRect, DrawWindow, "Smoke Trail v2.4.2");
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

            // Keybinds
            GUILayout.Label("Keybinds (supports Shift/Ctrl/Alt combos)", labelStyle);
            if (bindingTarget != null)
            {
                GUILayout.Label("Hold modifier(s) + press main key... (ESC to cancel)", wpLabelStyle);
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Toggle UI:", wpLabelStyle, GUILayout.Width(80));
                if (GUILayout.Button(Plugin.KeyToggleUI.Value.ToString(), smallButtonStyle, GUILayout.Width(180)))
                { bindingTarget = "UI"; bindingTimer = 0f; SnapshotHeldKeys(); }
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("Toggle Smoke:", wpLabelStyle, GUILayout.Width(80));
                if (GUILayout.Button(Plugin.KeyToggleSmoke.Value.ToString(), smallButtonStyle, GUILayout.Width(180)))
                { bindingTarget = "Smoke"; bindingTimer = 0f; SnapshotHeldKeys(); }
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(4);

            // Persistence
            GUILayout.BeginHorizontal();
            bool autoApply = GUILayout.Toggle(Plugin.AutoApplyOnRespawn.Value, " Re-apply settings on respawn");
            if (autoApply != Plugin.AutoApplyOnRespawn.Value) Plugin.AutoApplyOnRespawn.Value = autoApply;
            GUILayout.EndHorizontal();
            GUILayout.Space(4);

            // Aircraft list
            GUILayout.Label($"Aircraft ({aiList.Count})", labelStyle);
            scrollPos = GUILayout.BeginScrollView(scrollPos, GUILayout.Height(140));
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
                bool isLocal = GameManager.IsLocalAircraft(selectedAircraft);

                string selName = "Aircraft";
                try { if (selectedAircraft.definition != null) selName = selectedAircraft.definition.name; } catch { }

                GUILayout.Label($"Selected: {selName}{(isLocal ? "  [YOU]" : "")}", labelStyle);
                GUILayout.Space(4);

                // Toggle
                if (GUILayout.Button(state.active ? "SMOKE ON" : "SMOKE OFF",
                    state.active ? activeButtonStyle : buttonStyle, GUILayout.Height(30)))
                {
                    SmokeManager.SetActive(state, !state.active);
                    if (isLocal) SmokeManager.SaveProfile(state);
                    BroadcastState(state);
                }

                // Wingtip mode toggle
                bool newWingtip = GUILayout.Toggle(state.wingtipMode, " Wingtip smoke (left + right)");
                if (newWingtip != state.wingtipMode)
                {
                    state.wingtipMode = newWingtip;
                    if (state.active) SmokeManager.SetActive(state, true); // re-route emitters
                    if (isLocal) SmokeManager.SaveProfile(state);
                    BroadcastState(state);
                }

                if (state.wingtipMode)
                {
                    GUILayout.Label($"Wingtip X offset: {state.wingtipOffsetX:F2}m (half-wingspan)", wpLabelStyle);
                    float newOffset = GUILayout.HorizontalSlider(state.wingtipOffsetX, 0.5f, 25f);
                    if (Mathf.Abs(newOffset - state.wingtipOffsetX) > 0.01f)
                    {
                        state.wingtipOffsetX = newOffset;
                        SmokeManager.UpdateWingtipPositions(state);
                        if (isLocal) SmokeManager.SaveProfile(state);
                        BroadcastState(state);
                    }
                    if (GUILayout.Button("Reset to default", smallButtonStyle))
                    {
                        state.wingtipOffsetX = SmokeManager.LookupHalfSpan(selectedAircraft);
                        SmokeManager.UpdateWingtipPositions(state);
                        BroadcastState(state);
                    }
                }

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
                        if (isLocal) SmokeManager.SaveProfile(state);
                        BroadcastState(state);
                    }
                    count++;
                    if (count % 5 == 0) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
                }
                GUILayout.EndHorizontal();

                GUILayout.Space(4);

                // RGB sliders
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
                    if (isLocal) SmokeManager.SaveProfile(state);
                    BroadcastState(state);
                }

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

                if (sliderChanged)
                {
                    SmokeManager.UpdateSettings(state);
                    if (isLocal) SmokeManager.SaveProfile(state);
                    BroadcastState(state);
                }

                GUILayout.Space(4);

                // Profile save/load buttons
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Save as Default", smallButtonStyle))
                {
                    SmokeManager.SaveProfile(state);
                }
                if (GUILayout.Button("Load Default", smallButtonStyle))
                {
                    SmokeManager.ApplyProfile(state);
                    SmokeManager.UpdateSettings(state);
                    if (state.active) SmokeManager.SetActive(state, true);
                    BroadcastState(state);
                }
                GUILayout.EndHorizontal();

                GUILayout.Space(4);

                // Batch
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

        public void BroadcastState(SmokeTrailState state)
        {
            if (!SmokeNetwork.IsServer) return;
            if (state.aircraft == null) return;

            var msg = SmokeManager.StateToMessage(state);
            if (msg.netId == 0) return;

            SmokeNetwork.SendToAll(msg);
        }

        public Aircraft FindLocalAircraft()
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
        private int lastLocalAircraftId;

        void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            Plugin.Log?.LogInfo("FrameHelper Awake OK");
        }

        void Update()
        {
            ui.HandleInput();
            TrackLocalAircraftRespawn();

            cleanupTimer += Time.deltaTime;
            if (cleanupTimer > 5f)
            {
                cleanupTimer = 0f;
                SmokeManager.Cleanup();
            }
        }

        // When the local player gets a new aircraft (instance id changes), if the
        // user opted in we re-apply their persisted profile and turn smoke back on
        // automatically. This is what survives the respawn.
        private void TrackLocalAircraftRespawn()
        {
            var local = ui.FindLocalAircraft();
            int curId = local != null ? local.GetInstanceID() : 0;
            if (curId == lastLocalAircraftId) return;

            lastLocalAircraftId = curId;
            if (local == null) return;
            if (!Plugin.AutoApplyOnRespawn.Value) return;

            try
            {
                var state = SmokeManager.GetOrCreate(local);
                SmokeManager.ApplyProfile(state);
                SmokeManager.UpdateSettings(state);
                // Keep smoke on across respawns when the persisted profile had it on.
                bool wasOn = Plugin.ProfileWasActive.Value;
                if (wasOn) SmokeManager.SetActive(state, true);
                ui.BroadcastState(state);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"Respawn re-apply failed: {e}");
            }
        }

        void OnGUI()
        {
            ui.OnGUI();
        }
    }

    // ========== PLUGIN ==========

    [BepInPlugin("com.noms.smoketrail", "Smoke Trail", "2.4.2")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        // Keybinds — KeyboardShortcut so users can bind Shift+F7 etc.
        internal static ConfigEntry<KeyboardShortcut> KeyToggleUI;
        internal static ConfigEntry<KeyboardShortcut> KeyToggleSmoke;

        // Persistent profile
        internal static ConfigEntry<bool>  AutoApplyOnRespawn;
        internal static ConfigEntry<bool>  ProfileWasActive;
        internal static ConfigEntry<bool>  ProfileWingtip;
        internal static ConfigEntry<float> ProfileColorR;
        internal static ConfigEntry<float> ProfileColorG;
        internal static ConfigEntry<float> ProfileColorB;
        internal static ConfigEntry<float> ProfileOpacity;
        internal static ConfigEntry<float> ProfileSize;
        internal static ConfigEntry<float> ProfileLifetime;
        internal static ConfigEntry<float> ProfileRate;

        private static bool helperCreated;

        private void Awake()
        {
            Log = Logger;
            Logger.LogInfo("Smoke Trail v2.4.2 loaded");

            KeyToggleUI = Config.Bind("Keybinds", "ToggleUI",
                new KeyboardShortcut(KeyCode.F7),
                "Open/close the Smoke Trail UI. Combo-capable (e.g. LeftShift + F7).");
            KeyToggleSmoke = Config.Bind("Keybinds", "ToggleSmoke",
                new KeyboardShortcut(KeyCode.F8),
                "Quick-toggle smoke on your aircraft. Combo-capable.");

            AutoApplyOnRespawn = Config.Bind("Persistence", "ReapplyOnRespawn", true,
                "Re-apply your last smoke settings (and re-enable smoke if it was on) when you respawn or get a new aircraft.");
            ProfileWasActive = Config.Bind("Persistence", "ProfileActive", false,
                "Whether smoke was on the last time you used the UI on your local aircraft.");
            ProfileWingtip = Config.Bind("Persistence", "ProfileWingtip", false,
                "Wingtip mode in the saved profile.");
            ProfileColorR = Config.Bind("Persistence", "ProfileColorR", 1f, "Saved profile color R.");
            ProfileColorG = Config.Bind("Persistence", "ProfileColorG", 1f, "Saved profile color G.");
            ProfileColorB = Config.Bind("Persistence", "ProfileColorB", 1f, "Saved profile color B.");
            ProfileOpacity = Config.Bind("Persistence", "ProfileOpacity", 0.8f, "Saved profile opacity.");
            ProfileSize = Config.Bind("Persistence", "ProfileSize", 8f, "Saved profile particle size.");
            ProfileLifetime = Config.Bind("Persistence", "ProfileLifetime", 6f, "Saved profile particle lifetime (s).");
            ProfileRate = Config.Bind("Persistence", "ProfileRate", 60f, "Saved profile emission rate (per second).");

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
