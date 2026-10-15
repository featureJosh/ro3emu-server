using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The controller HUD (Diablo-style), drawn on our overlay while controller mode is on: HP orb (left), SP orb (right),
    /// the bound skills between them (icon, radial cooldown with seconds, unusable dimming, button glyph), and the two
    /// potion shortcuts by the HP orb. Sized in 1080p units times Overlay.Scale, so it keeps its size on a 4K screen.
    /// <para>Data comes from the stock PC HUD, which keeps running hidden (CanvasGroup alpha 0, no raycasts): its slots are
    /// InputBinders (m_inputKey = the action), so pressing a bound button still casts through the game's own code.</para>
    /// <list type="bullet">
    /// <item>Skill slot (UI_Sys_Main_JoyStickSkillWidget_PC/.../UI_Com_JoyStickSkillItemWidgetN): skillIcon_Img (icon),
    /// Mask_Img active while cooling down with BarTime_Img fill and Time_TTxt seconds, Disable_Img when unusable.</item>
    /// <item>Potion slot (UI_Sys_Main_PropsWidget_PC/PropsItem0N): ItemIcon_Img, PropNum_TTxt (count); the item cell's
    /// UI_Com_NullNew_MaskWidget shows the cooldown: MaskItem_Img fill (shared by both potions, ~30 s) and
    /// MaskItemTip_TTxt seconds. Stock cooldown fills are Radial360 and mirrored (x scale -1).</item>
    /// <item>HP / SP: UI_Sys_Main_AvatarInfoWidget_PC HPBar_Slider / BlueBar_Slider (0..1).</item>
    /// </list>
    /// </summary>
    public static class ControllerHud
    {
        private const string SkillWidget = "UI_Sys_Main_JoyStickSkillWidget_PC(Clone)";
        private const string PropsWidget = "UI_Sys_Main_PropsWidget_PC(Clone)";
        private const string AvatarWidget = "UI_Sys_Main_AvatarInfoWidget_PC(Clone)";

        // Skill slots in button order (the HUD's left-to-right order follows the bound control).
        private static readonly string[] ButtonOrder =
        {
            "buttonWest", "buttonNorth", "buttonEast", "buttonSouth", "leftShoulder", "leftTrigger", "rightTrigger", "rightShoulder",
        };

        private static readonly FieldInfo KeyField = typeof(InputBinderBase).GetField("m_inputKey", BindingFlags.NonPublic | BindingFlags.Instance);

        private sealed class Source
        {
            public string Action;
            public Transform Root;
            public Image Icon, CdFill, Disabled;
            public GameObject CdOn;
            public TMP_Text CdText, Count;
        }

        private sealed class Slot
        {
            public RectTransform Rect;
            public Image Icon, Cd, Glyph, Frame, Flash;
            public TextMeshProUGUI CdText, Count;
        }

        private static RectTransform _root;
        private static Image _hpFill, _spFill, _plate, _plateEdge;
        private static TextMeshProUGUI _hpText, _spText;
        private static readonly List<Slot> Skills = new List<Slot>(), Potions = new List<Slot>();
        private static readonly List<Source> SkillSources = new List<Source>(), PotionSources = new List<Source>();
        private static Slider _hp, _sp;
        private static readonly List<CanvasGroup> Hidden = new List<CanvasGroup>();
        private static int _frame;
        private static bool _shown;

        /// <summary>Our HUD is on screen (the prompt bar moves above it).</summary>
        public static bool Visible => _shown;

        /// <summary>HUD base size: 1080p design units x this x Overlay.Scale.</summary>
        private const float Size = 0.85f;
        private static float Scale => Overlay.Scale * Size;

        /// <summary>Top of the HUD (orbs) in screen pixels above the bottom edge.</summary>
        public static float Top => (OrbSize + 6f) * Scale;

        /// <summary>From Controller.Tick every frame.</summary>
        internal static void Tick(bool on)
        {
            if (!on) { if (_shown) Show(false); return; }
            if (++_frame % 30 == 1) Bind();
            bool world = _hp != null && _hp.isActiveAndEnabled;
            if (world != _shown) Show(world);
            if (!world) return;
            Layout();
            _hpFill.fillAmount = _hp.value;
            _hpText.text = Mathf.RoundToInt(_hp.value * 100f) + "%";
            if (_sp != null) { _spFill.fillAmount = _sp.value; _spText.text = Mathf.RoundToInt(_sp.value * 100f) + "%"; }
            for (int i = 0; i < Skills.Count; i++) Fill(Skills[i], i < SkillSources.Count ? SkillSources[i] : null);
            for (int i = 0; i < Potions.Count; i++) Fill(Potions[i], i < PotionSources.Count ? PotionSources[i] : null);
        }

        // ---- data ----

        /// <summary>Finds the stock widgets and the slots of the bound actions (they are pooled and rebuilt, so again
        /// every 30 frames).</summary>
        private static void Bind()
        {
            var avatar = GameObject.Find(AvatarWidget);
            _hp = _sp = null;
            if (avatar != null)
                foreach (var s in avatar.GetComponentsInChildren<Slider>(false))
                {
                    if (s.name == "HPBar_Slider") _hp = s;
                    else if (s.name == "BlueBar_Slider") _sp = s;
                }
            SkillSources.Clear();
            var skills = GameObject.Find(SkillWidget);
            if (skills != null)
            {
                var byAction = new Dictionary<string, Source>();
                foreach (var binder in skills.GetComponentsInChildren<InputBinder>(false))
                {
                    string action = KeyField.GetValue(binder) as string;
                    if (string.IsNullOrEmpty(action) || byAction.ContainsKey(action)) continue;
                    var slot = binder.transform.parent; // Root_CanvasG_GraphicSwitchG: icon, cooldown and binder live under it
                    var src = new Source { Action = action, Root = slot };
                    foreach (var img in slot.GetComponentsInChildren<Image>(true))
                        switch (img.name)
                        {
                            case "skillIcon_Img": src.Icon = img; break;
                            case "BarTime_Img": src.CdFill = img; break;
                            case "Disable_Img": src.Disabled = img; break;
                            case "Mask_Img": src.CdOn = img.gameObject; break;
                        }
                    foreach (var t in slot.GetComponentsInChildren<TMP_Text>(true))
                        if (t.name == "Time_TTxt") src.CdText = t;
                    byAction[action] = src;
                }
                // Bound actions in button order.
                foreach (var control in ButtonOrder)
                    foreach (var pair in byAction)
                        if (Controller.FirstBinding(pair.Key) == "<Gamepad>/" + control) SkillSources.Add(pair.Value);
            }
            PotionSources.Clear();
            var props = GameObject.Find(PropsWidget);
            if (props != null)
                foreach (var binder in props.GetComponentsInChildren<InputBinder>(false))
                {
                    string action = KeyField.GetValue(binder) as string;
                    if (Controller.FirstBinding(action) == null) continue;
                    var slot = binder.transform.parent.parent; // PropsItem0N_RTransform
                    var src = new Source { Action = action, Root = slot };
                    foreach (var img in slot.GetComponentsInChildren<Image>(true))
                        switch (img.name)
                        {
                            case "ItemIcon_Img": src.Icon = img; break;
                            // The item cell's own cooldown mask (CD_RTransform / CD_Img stay off on the PC bar).
                            case "MaskItem_Img": src.CdFill = img; src.CdOn = img.gameObject; break;
                        }
                    foreach (var t in slot.GetComponentsInChildren<TMP_Text>(true))
                        if (t.name == "MaskItemTip_TTxt") src.CdText = t;
                        else if (t.name == "PropNum_TTxt") src.Count = t;
                    PotionSources.Add(src);
                }
            HideStock(skills, props, GameObject.Find("Pattern_GraphicSwitchG"));
            Lift();
            string summary = string.Join(" ", SkillSources.ConvertAll(Describe)) + " | " + string.Join(" ", PotionSources.ConvertAll(Describe));
            if (summary != _summary) { _summary = summary; Debug.Log("[Ro3Emu.Hud] slots: " + summary); }
        }

        private static string _summary;

        private static string Describe(Source s) =>
            s.Action + "(" + (s.Icon != null ? "icon" : "-") + (s.CdOn != null ? ",cd" : "") + (s.CdFill != null ? ",fill" : "")
            + (s.CdText != null ? ",secs" : "") + (s.Disabled != null ? ",dis" : "") + (s.Count != null ? ",count" : "") + ")";

        // Contextual stock widgets at the bottom (buffs, auto-potion, auto-battle, situational skills, AFK) move up
        // above our HUD instead of being hidden: they are game features.
        private static readonly string[] Lifted =
        {
            "BuffGroup_RTransform", "DBuffGroup_RTransform", "Main_DrugWidget_RTransform", "AutoFight_RTransform",
            "Main_PlaySkill_RTransform", "Main_DerivedSkill_RTransform", "BottomFunction_RTransform",
            "BasicsMapFatiguePointsDetail_RTransform",
        };
        private static readonly Dictionary<RectTransform, Vector2> LiftedFrom = new Dictionary<RectTransform, Vector2>();
        private const float HudTop = 150f; // 1080p units: everything of ours is below this

        private static void Lift()
        {
            var main = GameObject.Find("UI_Sys_MainWidget_PC(Clone)");
            if (main == null) return;
            var canvas = main.GetComponentInParent<Canvas>();
            float perPixel = canvas != null && canvas.scaleFactor > 0 ? 1f / canvas.scaleFactor : 1f;
            foreach (var rt in main.GetComponentsInChildren<RectTransform>(false))
            {
                if (Array.IndexOf(Lifted, rt.name) < 0) continue;
                if (!LiftedFrom.TryGetValue(rt, out var from)) LiftedFrom[rt] = from = rt.anchoredPosition;
                var target = from + new Vector2(0f, HudTop * Scale * perPixel * 0.85f);
                if (rt.anchoredPosition != target) rt.anchoredPosition = target;
            }
        }

        private static void Unlift()
        {
            foreach (var pair in LiftedFrom) if (pair.Key != null) pair.Key.anchoredPosition = pair.Value;
            LiftedFrom.Clear();
        }

        /// <summary>The stock bars stay alive (data, key handling) but invisible and unclickable.</summary>
        private static void HideStock(params GameObject[] widgets)
        {
            foreach (var w in widgets)
            {
                if (w == null) continue;
                var group = w.GetComponent<CanvasGroup>() ?? w.AddComponent<CanvasGroup>();
                group.alpha = 0f;
                group.blocksRaycasts = false;
                if (!Hidden.Contains(group)) Hidden.Add(group);
            }
        }

        private static void RestoreStock()
        {
            foreach (var g in Hidden)
                if (g != null) { g.alpha = 1f; g.blocksRaycasts = true; }
            Hidden.Clear();
        }

        private static void Fill(Slot slot, Source src)
        {
            bool has = src != null && src.Root != null && src.Icon != null && src.Icon.sprite != null && src.Icon.isActiveAndEnabled;
            slot.Icon.enabled = has;
            slot.Glyph.sprite = src != null ? Glyphs.For(Controller.FirstBinding(src.Action)) : null;
            slot.Glyph.enabled = slot.Glyph.sprite != null;
            if (!has) { slot.Cd.enabled = false; slot.CdText.text = ""; if (slot.Count != null) slot.Count.text = ""; return; }
            if (Pressed(src.Action)) Punch.Play(slot.Rect, 1.18f, slot.Flash);
            if (slot.Icon.sprite != src.Icon.sprite) slot.Icon.sprite = src.Icon.sprite;
            bool disabled = src.Disabled != null && src.Disabled.gameObject.activeInHierarchy;
            slot.Icon.color = disabled ? new Color(0.45f, 0.45f, 0.45f) : Color.white;
            bool cd = src.CdOn != null && src.CdOn.activeInHierarchy && (src.CdFill == null || src.CdFill.fillAmount > 0f);
            slot.Cd.enabled = cd;
            if (cd && src.CdFill != null)
            {
                // Same sweep as the stock mask, which is mirrored (x scale -1): mirrored means the other direction.
                slot.Cd.fillAmount = src.CdFill.fillAmount;
                slot.Cd.fillMethod = src.CdFill.fillMethod;
                slot.Cd.fillOrigin = src.CdFill.fillOrigin;
                slot.Cd.fillClockwise = src.CdFill.fillClockwise != src.CdFill.transform.lossyScale.x < 0f;
            }
            else if (cd) slot.Cd.fillAmount = 1f;
            string seconds = cd && src.CdText != null ? src.CdText.text : "";
            if (string.IsNullOrEmpty(seconds) || !char.IsDigit(seconds[0])) seconds = ""; // idle labels ("获取") are not seconds
            else if (char.IsDigit(seconds[seconds.Length - 1])) seconds += "s"; // potions show bare numbers, skills "4s"
            slot.CdText.text = seconds;
            if (slot.Count != null) slot.Count.text = src.Count != null ? src.Count.text : "";
        }

        /// <summary>The slot's button went down this frame while the game has it (not in menus, the wheel or cursor
        /// mode, where the binding is taken away).</summary>
        private static bool Pressed(string action)
        {
            var path = Controller.FirstBinding(action);
            var pad = UnityEngine.InputSystem.Gamepad.current;
            if (path == null || pad == null || UiNav.Active || Wheel.Open || Controller.CursorOn) return false;
            return Controller.Edge(pad.TryGetChildControl(path.Substring(path.IndexOf('/') + 1)) as UnityEngine.InputSystem.Controls.ButtonControl, true);
        }

        // ---- view ----

        private const float OrbSize = 150f, SkillSize = 78f, PotionSize = 58f, Gap = 12f;
        private static readonly Color Gold = new Color(0.86f, 0.7f, 0.4f, 1f);

        private static void Show(bool on)
        {
            _shown = on;
            if (on) Build();
            if (_root != null) _root.gameObject.SetActive(on);
            if (!on) { RestoreStock(); Unlift(); }
        }

        private static void Build()
        {
            if (_root != null) return;
            _root = Overlay.Rect("Ro3emuHud");
            _root.anchorMin = _root.anchorMax = _root.pivot = new Vector2(0.5f, 0f);
            _plate = Overlay.Image("Plate", _root, new Color(0.05f, 0.04f, 0.035f, 0.78f));
            _plateEdge = Overlay.Image("PlateEdge", _root, new Color(0.86f, 0.7f, 0.4f, 0.7f));
            _hpFill = Orb(-1, new Color(0.78f, 0.1f, 0.12f), out _hpText);
            _spFill = Orb(1, new Color(0.18f, 0.38f, 0.9f), out _spText);
            for (int i = 0; i < 8; i++) Skills.Add(NewSlot("Skill" + i, SkillSize, false));
            for (int i = 0; i < 2; i++) Potions.Add(NewSlot("Potion" + i, PotionSize, true));
        }

        /// <summary>One row centred on the screen: [HP orb] [potions] [skills] [SP orb], on a plate.</summary>
        private static void Layout()
        {
            float s = Scale;
            _root.localScale = new Vector3(s, s, 1f);
            _root.anchoredPosition = new Vector2(0f, 6f * s);
            int skills = SkillSources.Count, potions = PotionSources.Count;
            float row = skills * SkillSize + Math.Max(0, skills - 1) * Gap + potions * (PotionSize + Gap) + (potions > 0 ? Gap : 0f);
            float x = -row / 2f, y = 36f;
            for (int i = 0; i < Potions.Count; i++)
            {
                bool on = i < potions;
                Potions[i].Rect.gameObject.SetActive(on);
                if (!on) continue;
                Potions[i].Rect.anchoredPosition = new Vector2(x + PotionSize / 2f, y + SkillSize / 2f); // pivot: centre
                x += PotionSize + Gap;
            }
            if (potions > 0) x += Gap;
            for (int i = 0; i < Skills.Count; i++)
            {
                bool on = i < skills;
                Skills[i].Rect.gameObject.SetActive(on);
                if (!on) continue;
                Skills[i].Rect.anchoredPosition = new Vector2(x + SkillSize / 2f, y + SkillSize / 2f);
                x += SkillSize + Gap;
            }
            float orbX = row / 2f + 18f + OrbSize / 2f;
            ((RectTransform)_hpFill.transform.parent).anchoredPosition = new Vector2(-orbX - OrbSize / 2f, 0f);
            ((RectTransform)_spFill.transform.parent).anchoredPosition = new Vector2(orbX - OrbSize / 2f, 0f);
            Place(_plate.rectTransform, -orbX, y - 22f, 2f * orbX, SkillSize + 44f);
            Place(_plateEdge.rectTransform, -orbX, y + SkillSize + 20f, 2f * orbX, 2f);
        }

        private static Image Orb(int side, Color color, out TextMeshProUGUI label)
        {
            var frame = Overlay.Rect(side < 0 ? "HpOrb" : "SpOrb", _root);
            frame.sizeDelta = new Vector2(OrbSize, OrbSize);
            var back = Img("Back", frame, Shapes.Circle, new Color(0.04f, 0.03f, 0.03f, 0.85f), 0, 0, OrbSize);
            var fill = Img("Fill", frame, Shapes.Sphere, color, 6, 6, OrbSize - 12);
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Vertical;
            fill.fillOrigin = (int)Image.OriginVertical.Bottom;
            Img("Shine", frame, Shapes.Circle, new Color(1f, 1f, 1f, 0.12f), OrbSize * 0.26f, OrbSize * 0.52f, OrbSize * 0.3f);
            Img("Ring", frame, Shapes.Ring, Gold, 0, 0, OrbSize);
            label = Overlay.Text("Value", frame, 24);
            label.alignment = TextAlignmentOptions.Center;
            Place(label.rectTransform, 0, 0, OrbSize, OrbSize);
            back.raycastTarget = false;
            return fill;
        }

        private static Slot NewSlot(string name, float size, bool potion)
        {
            var slot = new Slot { Rect = Overlay.Rect(name, _root) };
            slot.Rect.sizeDelta = new Vector2(size, size);
            slot.Rect.pivot = new Vector2(0.5f, 0.5f); // the press punch scales around the centre
            Img("Back", slot.Rect, Shapes.RoundedRect, new Color(0.04f, 0.03f, 0.03f, 0.85f), 0, 0, size);
            slot.Icon = Img("Icon", slot.Rect, null, Color.white, 5, 5, size - 10);
            slot.Icon.preserveAspect = true;
            slot.Cd = Img("Cooldown", slot.Rect, Shapes.RoundedRect, new Color(0f, 0f, 0f, 0.65f), 5, 5, size - 10);
            slot.Cd.type = Image.Type.Filled;
            slot.Cd.fillMethod = Image.FillMethod.Radial360;
            slot.Cd.fillOrigin = (int)Image.Origin360.Top;
            slot.Cd.fillClockwise = false;
            slot.Frame = Img("Frame", slot.Rect, Shapes.RoundedRing, Gold, 0, 0, size);
            slot.Flash = Img("Flash", slot.Rect, Shapes.RoundedRect, new Color(1f, 0.93f, 0.75f, 0f), 0, 0, size);
            slot.Flash.enabled = false;
            slot.CdText = Overlay.Text("Seconds", slot.Rect, size * 0.38f);
            slot.CdText.alignment = TextAlignmentOptions.Center;
            Place(slot.CdText.rectTransform, 0, 0, size, size);
            float glyph = potion ? 28f : 32f;
            slot.Glyph = Img("Glyph", slot.Rect, null, Color.white, (size - glyph) / 2f, -glyph * 0.4f, glyph); // on the slot's bottom edge, inside the plate
            slot.Glyph.preserveAspect = true;
            if (potion)
            {
                slot.Count = Overlay.Text("Count", slot.Rect, 18);
                slot.Count.alignment = TextAlignmentOptions.TopRight; // the glyph is at the bottom
                Place(slot.Count.rectTransform, 0, 0, size - 4, size - 1);
            }
            return slot;
        }

        private static Image Img(string name, Transform parent, Sprite sprite, Color color, float x, float y, float size)
        {
            var image = Overlay.Image(name, parent, color);
            image.sprite = sprite;
            Place(image.rectTransform, x, y, size, size);
            return image;
        }

        private static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
        }
    }

    /// <summary>Procedural white shapes (anti-aliased alpha) for our UI, tinted by Image.color.</summary>
    internal static class Shapes
    {
        private static Sprite _circle, _ring, _rounded, _roundedRing, _sphere;

        public static Sprite Circle => _circle ??= Make(128, (x, y) => Disc(x, y, 64f));
        public static Sprite Sphere => _sphere ??= Make(128, (x, y) => Disc(x, y, 64f), (x, y) =>
        {
            float r = Mathf.Min(1f, Mathf.Sqrt(x * x + y * y) / 64f), z = Mathf.Sqrt(1f - r * r);
            float light = Mathf.Max(0f, (-0.35f * x / 64f + 0.45f * y / 64f + 0.8f * z)); // lit from the upper left
            return Mathf.Clamp01(0.35f + 0.75f * light);
        });
        public static Sprite Ring => _ring ??= Make(128, (x, y) => Disc(x, y, 64f) - Disc(x, y, 58f));
        public static Sprite RoundedRect => _rounded ??= Make(64, (x, y) => Round(x, y, 32f, 10f));
        public static Sprite RoundedRing => _roundedRing ??= Make(64, (x, y) => Round(x, y, 32f, 10f) - Round(x, y, 29f, 8f));

        /// <summary>Coverage of a centred disc of radius r at the pixel centre (x, y), 1 px soft edge.</summary>
        private static float Disc(float x, float y, float r) => Mathf.Clamp01(r - Mathf.Sqrt(x * x + y * y));

        /// <summary>Coverage of a centred rounded square with half-size h and corner radius c.</summary>
        private static float Round(float x, float y, float h, float c)
        {
            float qx = Math.Abs(x) - (h - c), qy = Math.Abs(y) - (h - c);
            float outside = new Vector2(Math.Max(qx, 0f), Math.Max(qy, 0f)).magnitude + Math.Min(Math.Max(qx, qy), 0f) - c;
            return Mathf.Clamp01(0.5f - outside);
        }

        private static Sprite Make(int size, Func<float, float, float> alpha, Func<float, float, float> shade = null)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "ro3emu_shape" };
            var pixels = new Color32[size * size];
            float half = size / 2f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float px = x + 0.5f - half, py = y + 0.5f - half;
                    byte v = (byte)(255 * (shade != null ? shade(px, py) : 1f));
                    pixels[y * size + x] = new Color32(v, v, v, (byte)(Mathf.Clamp01(alpha(px, py)) * 255));
                }
            tex.SetPixels32(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }
    }
}
