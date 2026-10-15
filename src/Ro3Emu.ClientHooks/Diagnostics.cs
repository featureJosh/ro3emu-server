using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Server-triggered client inspection (game message 77012, UTF-8 command line; GM "!client ..."), written to
    /// Player.log. Runs on the main thread from the chat adapter's pump.
    /// </summary>
    public static class Diagnostics
    {
        public const uint MsgId = 77012;

        public static void Run(string line)
        {
            var parts = line.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;
            try
            {
                switch (parts[0])
                {
                    case "find":
                        Find(parts.Length > 1 ? parts[1] : "weapon");
                        break;
                    case "panels":
                        Panels();
                        break;
                    case "tree":
                        Tree(parts.Length > 1 ? parts[1] : "UI_Sys_MainWidget_PC(Clone)");
                        break;
                    case "lua":
                    case "luacall":
                        Lua(parts[0] == "luacall", parts.Length > 1 ? parts[1] : "");
                        break;
                    case "focus":
                        // "focus ignore": input (ours and the device's) keeps working while the window is in the background,
                        // so cmd.txt test runs work while the developer is in another window.
                        IgnoreFocus = parts.Length < 2 || parts[1] != "normal";
                        Application.runInBackground = IgnoreFocus || Application.runInBackground;
                        UnityEngine.InputSystem.InputSystem.settings.backgroundBehavior = IgnoreFocus
                            ? UnityEngine.InputSystem.InputSettings.BackgroundBehavior.IgnoreFocus
                            : UnityEngine.InputSystem.InputSettings.BackgroundBehavior.ResetAndDisableNonBackgroundDevices;
                        Debug.Log("[Ro3Emu.Diag] focus " + (IgnoreFocus ? "ignored" : "normal"));
                        break;
                    case "actions":
                        Debug.Log("[Ro3Emu.Diag] actions: " + Controller.DescribeActions());
                        break;
                    case "select":
                        if (parts.Length > 1) UiNav.Select(parts[1]);
                        break;
                    case "verbose":
                        Verbose = parts.Length < 2 || parts[1] != "off";
                        break;
                    case "waitmenu":
                        // "waitmenu <part of the menu name> [timeout seconds]": pause the queue until that menu has the pad.
                        var w = parts.Length > 1 ? parts[1].Split(' ') : new[] { "" };
                        _waitMenu = w[0];
                        _waitUntil = Time.realtimeSinceStartup + (w.Length > 1 && float.TryParse(w[1], out float timeout) ? timeout : 60f);
                        break;
                    case "pick":
                        Pick(parts.Length > 1 ? parts[1] : "");
                        break;
                    case "wait":
                        _wait = parts.Length > 1 && int.TryParse(parts[1], out int frames) ? frames : 30;
                        break;
                    case "stick":
                        // "stick <x> <y> <frames>": the virtual pad's left stick held there (walk), then centred.
                        var v = (parts.Length > 1 ? parts[1] : "").Split(' ');
                        if (v.Length >= 2 && float.TryParse(v[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sx)
                            && float.TryParse(v[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sy))
                        {
                            if (_testPad == null) _testPad = UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Gamepad>("Ro3emuTestPad");
                            var device = _testPad;
                            device.MakeCurrent();
                            UnityEngine.InputSystem.InputSystem.QueueStateEvent(device, new UnityEngine.InputSystem.LowLevel.GamepadState { leftStick = new Vector2(sx, sy) });
                            _release = () => UnityEngine.InputSystem.InputSystem.QueueStateEvent(device, new UnityEngine.InputSystem.LowLevel.GamepadState());
                            _releaseIn = v.Length > 2 && int.TryParse(v[2], out int hold) ? hold : 60;
                            Debug.Log("[Ro3Emu.Diag] stick " + sx + "," + sy + " for " + _releaseIn + " frames");
                        }
                        break;
                    case "pad":
                    case "key":
                        if (parts.Length > 1) Press(parts[0] == "pad", parts[1]);
                        break;
                    case "shot":
                        string shot = System.IO.Path.Combine(Dir(), "shot.png");
                        if (parts.Length > 1) shot = System.IO.Path.Combine(Dir(), "shot-" + parts[1] + ".png");
                        ScreenCapture.CaptureScreenshot(shot);
                        Debug.Log("[Ro3Emu.Diag] screenshot -> " + shot);
                        break;
                    case "dumptex":
                        DumpTextures(parts.Length > 1 ? parts[1] : "Txt");
                        break;
                    default:
                        Debug.Log("[Ro3Emu.Diag] unknown command " + line);
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.Diag] " + line + " failed: " + e);
            }
        }

        /// <summary>"pick x y" (screenshot pixels, origin top-left): every visible UI graphic under that point.</summary>
        private static void Pick(string args)
        {
            var xy = args.Split(' ');
            if (xy.Length < 2 || !float.TryParse(xy[0], out float x) || !float.TryParse(xy[1], out float y)) return;
            var point = new Vector2(x, Screen.height - y);
            var sb = new StringBuilder();
            foreach (var g in UnityEngine.Object.FindObjectsOfType<Graphic>())
            {
                if (!g.isActiveAndEnabled || g.canvasRenderer.GetInheritedAlpha() < 0.02f) continue;
                var canvas = g.canvas;
                var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.rootCanvas.worldCamera : null;
                if (!RectTransformUtility.RectangleContainsScreenPoint(g.rectTransform, point, cam)) continue;
                var r = g.rectTransform.rect;
                if (r.width * r.height > 600 * 600) continue; // full-screen backgrounds
                sb.Append("\n  ").Append(Path(g.transform)).Append(" (").Append(g.GetType().Name).Append(')');
            }
            Debug.Log("[Ro3Emu.Diag] pick " + args + ":" + sb);
        }

        /// <summary>Every transform whose name contains <paramref name="filter"/>: path, state, renderers.</summary>
        private static void Find(string filter)
        {
            var all = Resources.FindObjectsOfTypeAll<Transform>();
            var sb = new StringBuilder();
            int hits = 0;
            foreach (var t in all)
            {
                if (t.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (t.gameObject.scene.name == null) continue; // prefab assets, not scene objects
                if (++hits > 60) break;
                sb.Append("\n  ").Append(Path(t))
                  .Append(" active=").Append(t.gameObject.activeInHierarchy)
                  .Append(" layer=").Append(t.gameObject.layer)
                  .Append(" pos=").Append(t.position.ToString("F2"))
                  .Append(" scale=").Append(t.lossyScale.ToString("F3"));
                foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                {
                    sb.Append("\n      renderer ").Append(r.GetType().Name).Append(' ').Append(r.name)
                      .Append(" enabled=").Append(r.enabled)
                      .Append(" visible=").Append(r.isVisible)
                      .Append(" layer=").Append(r.gameObject.layer)
                      .Append(" bounds=").Append(r.bounds.size.ToString("F2"))
                      .Append(" mat=").Append(r.sharedMaterial != null ? r.sharedMaterial.name + "/" + (r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "-") : "null");
                }
            }
            Debug.Log("[Ro3Emu.Diag] find '" + filter + "': " + hits + " hit(s)" + sb);
        }

        /// <summary>
        /// Developer commands without chat: each line of &lt;client&gt;/ro3emu/cmd.txt is run like "!client &lt;line&gt;", then the
        /// file is deleted. Polled about once a second from the frame pump; also works before world entry.
        /// </summary>
        /// <summary>Developer "focus ignore": controller input is handled while the window is in the background.</summary>
        public static bool IgnoreFocus;

        /// <summary>Developer "verbose": controller UI logs every move and press (for scripted test runs).</summary>
        public static bool Verbose;

        public static void PollCommandFile()
        {
            if (_release != null && --_releaseIn <= 0) { var r = _release; _release = null; r(); }
            if (_waitMenu != null)
            {
                bool found = UiNav.MenuName.IndexOf(_waitMenu, StringComparison.OrdinalIgnoreCase) >= 0;
                if (found || Time.realtimeSinceStartup > _waitUntil)
                {
                    Debug.Log("[Ro3Emu.Diag] waitmenu " + _waitMenu + (found ? ": " + UiNav.MenuName : ": timed out"));
                    _waitMenu = null;
                    _wait = 30; // let it finish opening
                }
            }
            else if (_wait > 0) _wait--;
            else if (Queue.Count > 0) Run(Queue.Dequeue());
            if (++_pollFrame % 60 != 0) return;
            string file = System.IO.Path.Combine(Dir(), "cmd.txt");
            if (!File.Exists(file)) return;
            string[] lines;
            try { lines = File.ReadAllLines(file); File.Delete(file); }
            catch (IOException) { return; } // still being written
            foreach (var line in lines) if (line.Trim().Length > 0) Queue.Enqueue(line.Trim());
        }

        // cmd.txt lines run one per frame; "wait <frames>" pauses the queue.
        private static readonly System.Collections.Generic.Queue<string> Queue = new System.Collections.Generic.Queue<string>();
        private static int _pollFrame, _wait, _releaseIn;
        private static string _waitMenu;
        private static float _waitUntil;
        private static Action _release;
        private static UnityEngine.InputSystem.Gamepad _testPad;

        /// <summary>"pad &lt;GamepadButton&gt;" (South, East, DpadUp, LeftShoulder, Start...) / "key &lt;Key&gt;" (Escape, B...):
        /// pressed for 8 frames through the Input System, as if from the device.</summary>
        private static void Press(bool pad, string name)
        {
            if (pad)
            {
                // A virtual pad of our own: a real DualSense streams full reports that would overwrite an injected press.
                if (_testPad == null) _testPad = UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Gamepad>("Ro3emuTestPad");
                var device = _testPad;
                if (!Enum.TryParse(name, true, out UnityEngine.InputSystem.LowLevel.GamepadButton button)) { Debug.Log("[Ro3Emu.Diag] pad " + name + ": unknown button"); return; }
                device.MakeCurrent();
                UnityEngine.InputSystem.InputSystem.QueueStateEvent(device, new UnityEngine.InputSystem.LowLevel.GamepadState().WithButton(button));
                _release = () => UnityEngine.InputSystem.InputSystem.QueueStateEvent(device, new UnityEngine.InputSystem.LowLevel.GamepadState());
            }
            else
            {
                var device = UnityEngine.InputSystem.Keyboard.current;
                if (device == null || !Enum.TryParse(name, true, out UnityEngine.InputSystem.Key key)) { Debug.Log("[Ro3Emu.Diag] key " + name + ": no keyboard or unknown key"); return; }
                UnityEngine.InputSystem.InputSystem.QueueStateEvent(device, new UnityEngine.InputSystem.LowLevel.KeyboardState(key));
                _release = () => UnityEngine.InputSystem.InputSystem.QueueStateEvent(device, new UnityEngine.InputSystem.LowLevel.KeyboardState());
            }
            _releaseIn = 8;
            Debug.Log("[Ro3Emu.Diag] " + (pad ? "pad " : "key ") + name);
        }

        /// <summary>"lua a.b.c": a Lua global path's value; "luacall a.b.c [self]": calls it, with the table a.b as self
        /// when the word self follows (a:c()).</summary>
        private static void Lua(bool call, string args)
        {
            var global = LuaPatches.Global;
            if (global == null) { Debug.Log("[Ro3Emu.Diag] lua: game Lua not ready"); return; }
            var parts = args.Split(' ');
            string path = parts[0];
            object value;
            if (!call) value = global.Obf_LuA<object>(path);
            else
            {
                var fn = global.Obf_LuA<Obf_R.Obf_Ef>(path);
                int dot = path.LastIndexOfAny(new[] { '.', ':' });
                object self = parts.Length > 1 && parts[1] == "self" && dot > 0 ? global.Obf_LuA<object>(path.Substring(0, dot)) : null;
                var results = fn == null ? null : (self != null ? fn.Obf_HuA(self) : fn.Obf_HuA());
                value = results == null ? "(no function)" : string.Join(", ", Array.ConvertAll(results, r => r == null ? "nil" : r + ":" + r.GetType().Name));
            }
            Debug.Log("[Ro3Emu.Diag] " + (call ? "luacall " : "lua ") + path + " = " + (value == null ? "nil" : value + " (" + value.GetType().Name + ")"));
        }

        private static string Dir()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), "ro3emu");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// "tree &lt;name&gt; [depth]": the hierarchy under the first scene object named &lt;name&gt; (also inactive): active flag,
        /// rect size and screen position, components, sprite / fill / text. Written to &lt;client&gt;/ro3emu/tree.txt.
        /// </summary>
        private static void Tree(string args)
        {
            // "name [depth]"; names of UI objects have no spaces.
            var parts = args.Split(' ');
            int depth = parts.Length > 1 && int.TryParse(parts[1], out int d) ? d : 6;
            string name = parts[0];
            Transform root = null;
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
                if (t.gameObject.scene.name != null && t.name == name) { root = t; if (t.gameObject.activeInHierarchy) break; }
            var sb = new StringBuilder("tree '" + name + "' screen=" + Screen.width + "x" + Screen.height);
            if (root == null) sb.Append(": not found");
            else { sb.Append(" path=").Append(Path(root)); TreeNode(root, 0, depth, sb); }
            File.WriteAllText(System.IO.Path.Combine(Dir(), "tree.txt"), sb.ToString());
            Debug.Log("[Ro3Emu.Diag] " + (root == null ? sb.ToString() : "tree '" + name + "' -> ro3emu/tree.txt"));
        }

        private static readonly Vector3[] Corners = new Vector3[4];

        private static void TreeNode(Transform t, int level, int depth, StringBuilder sb)
        {
            sb.Append('\n').Append(' ', level * 2).Append(t.name).Append(t.gameObject.activeSelf ? "" : " [off]");
            if (t is RectTransform rt)
            {
                rt.GetWorldCorners(Corners);
                var cam = rt.GetComponentInParent<Canvas>()?.worldCamera;
                Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, Corners[0]), b = RectTransformUtility.WorldToScreenPoint(cam, Corners[2]);
                sb.Append(" size=").Append(rt.rect.size.ToString("F0")).Append(" screen=").Append(a.ToString("F0")).Append('-').Append(b.ToString("F0"));
            }
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null || c is Transform) continue;
                sb.Append(' ').Append(c.GetType().Name);
                if (c is Image img) sb.Append("(").Append(img.sprite != null ? img.sprite.name : "-").Append(img.type == Image.Type.Filled ? " fill=" + img.fillAmount.ToString("F2") : "").Append(")");
                else if (c is RawImage raw) sb.Append("(").Append(raw.texture != null ? raw.texture.name : "-").Append(")");
                else if (c is TMPro.TMP_Text tmp) sb.Append("(\"").Append(tmp.text.Replace("\n", " ")).Append("\")");
                else if (c is Slider sl) sb.Append("(").Append(sl.value.ToString("F2")).Append(")");
                else if (c is CanvasGroup cg) sb.Append("(a=").Append(cg.alpha.ToString("F2")).Append(")");
            }
            if (level >= depth) { if (t.childCount > 0) sb.Append(" +").Append(t.childCount); return; }
            for (int i = 0; i < t.childCount; i++) TreeNode(t.GetChild(i), level + 1, depth, sb);
        }

        /// <summary>Open UI views: active objects named UI_Sys_* / UI_Com_* (the view prefabs' roots), outermost only.</summary>
        private static void Panels()
        {
            var sb = new StringBuilder();
            int hits = 0;
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t.gameObject.scene.name == null || !t.gameObject.activeInHierarchy) continue;
                if (!t.name.StartsWith("UI_Sys_", StringComparison.Ordinal) && !t.name.StartsWith("UI_Com_", StringComparison.Ordinal)) continue;
                bool nested = false;
                for (var p = t.parent; p != null && !nested; p = p.parent)
                    nested = p.name.StartsWith("UI_Sys_", StringComparison.Ordinal) || p.name.StartsWith("UI_Com_", StringComparison.Ordinal);
                if (nested) continue;
                if (++hits > 120) break;
                sb.Append("\n  ").Append(Path(t)).Append(" children=").Append(t.childCount);
            }
            Debug.Log("[Ro3Emu.Diag] panels: " + hits + sb);
        }

        /// <summary>
        /// The textures of RawImage / Image components under transforms whose name contains <paramref name="filter"/>
        /// (also inactive ones): name and size in the log, a PNG copy in &lt;client&gt;/ro3emu/dump/.
        /// </summary>
        private static void DumpTextures(string filter)
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), "ro3emu", "dump");
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            int hits = 0;
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t.gameObject.scene.name == null || t.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                Texture tex = null;
                var raw = t.GetComponent<RawImage>(); // Unity objects: no ?. (destroyed objects compare equal to null)
                if (raw != null) tex = raw.texture;
                if (tex == null) { var img = t.GetComponent<Image>(); if (img != null && img.sprite != null) tex = img.sprite.texture; }
                if (tex == null || ++hits > 40) continue;
                string safe = tex.name;
                foreach (char c in System.IO.Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
                string file = System.IO.Path.Combine(dir, (safe.Length > 0 ? safe : "tex" + hits) + ".png");
                try { SavePng(tex, file); }
                catch (Exception e) { file = "save failed: " + e.Message; }
                sb.Append("\n  ").Append(Path(t)).Append(" tex=").Append(tex.name).Append(' ').Append(tex.width).Append('x').Append(tex.height)
                  .Append(" -> ").Append(file);
                var sprite = t.GetComponent<Image>();
                if (sprite != null && sprite.sprite != null)
                    sb.Append(" sprite=").Append(sprite.sprite.name).Append(" rect=").Append(sprite.sprite.textureRect.ToString("F0"))
                      .Append(" size=").Append(((RectTransform)t).rect.size.ToString("F0")).Append(" color=").Append(sprite.color);
            }
            Debug.Log("[Ro3Emu.Diag] dumptex '" + filter + "': " + hits + sb);
        }

        private static readonly string AutoDumpFilter = Environment.GetEnvironmentVariable("RO3EMU_DUMPTEX");
        private static int _autoDumpFrame;

        /// <summary>
        /// With RO3EMU_DUMPTEX=&lt;filter&gt; in the client's environment: dumptex every ~5 s from the frame pump, for screens
        /// before world entry (login, role select) where the GM "!client" command cannot reach.
        /// </summary>
        public static void AutoDump()
        {
            if (string.IsNullOrEmpty(AutoDumpFilter) || ++_autoDumpFrame % 300 != 0) return;
            try { DumpTextures(AutoDumpFilter); } catch (Exception e) { Debug.LogError("[Ro3Emu.Diag] autodump: " + e); }
        }

        /// <summary>Any texture (also non-readable / compressed) through a render texture into a PNG.</summary>
        internal static void SavePng(Texture tex, string file)
        {
            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var copy = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
            copy.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            File.WriteAllBytes(file, ImageConversion.EncodeToPNG(copy));
            UnityEngine.Object.Destroy(copy);
        }

        private static string Path(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }
    }
}
