using System.Collections.Generic;
using Game.Collider;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Context interaction for the controller: on PC an NPC, a drop or a gathering spot is clicked with the mouse. Here
    /// the nearest one near the player (CP_ColliderBase, m_iType = enum_UnitType: Npc 2, Collection 5, DroppedItem 6,
    /// Stall 11, Thing 12) gets a prompt above the HUD ("✕ Talk"), and the Interact control sends the same click as the
    /// mouse would: MyInput.Obf_xj, the delegate Lua's LC_Actor_ControlMove.TouchRaycastPos(point, collider, isClick,
    /// normal) is bound to (MyInput.cs: click on a unit), which walks there and interacts.
    /// </summary>
    public static class Interact
    {
        private static readonly Dictionary<int, string> Verbs = new Dictionary<int, string>
        {
            { 2, "Talk" }, { 5, "Gather" }, { 6, "Pick up" }, { 11, "Browse stall" }, { 12, "Interact" },
        };

        private const float Range = 4.5f; // metres; the game walks the rest of the way

        private static CP_ColliderBase _target;
        private static int _frame;
        private static RectTransform _prompt;
        private static Image _glyph;
        private static TextMeshProUGUI _label;

        /// <summary>From Controller.Tick in the world (no menu, no cursor), every frame.</summary>
        internal static void Tick(Gamepad pad, bool active)
        {
            if (!active) { Show(false); return; }
            if (++_frame % 10 == 0) _target = Nearest();
            if (_target == null || !_target.isActiveAndEnabled) { Show(false); return; }
            string control = UiNav.Control("Interact", "buttonSouth");
            Show(true);
            _glyph.sprite = Glyphs.For(control);
            _label.text = Verbs.TryGetValue(_target.m_iType, out var verb) ? verb : "Interact";
            if (Controller.Edge(pad.TryGetChildControl(control.Substring(control.IndexOf('/') + 1)) as ButtonControl, true))
            {
                // Re-resolved on the press: colliders are pooled and re-initialised for other units (Obf_tCD), and a cached
                // one sent the player to an NPC 45 m away (scripted run 2026-10-15, Airship Passenger instead of Person Resting).
                _target = Nearest();
                if (_target == null) return;
                var point = _target.transform.position;
                MyInput.Obf_xj?.Invoke(point, _target, true, Vector3.up);
                Debug.Log("[Ro3Emu.Interact] " + _label.text + " unit " + _target.m_iUUID + " type " + _target.m_iType + " at " + point
                    + ", player " + WS_WorldStreamProxy.Obf_fbA());
            }
        }

        /// <summary>
        /// The player's position is the world-stream focus, WS_WorldStreamProxy.Obf_fbA(): the active Cinemachine camera's
        /// Follow target, i.e. the player. CP_ColliderBase.m_bIsLocalPlayer is never set on the player's collider (scripted
        /// run 2026-10-15: 100 colliders, none flagged; other players are m_iType 1, NPCs 2).
        /// </summary>
        private static CP_ColliderBase Nearest()
        {
            var from = WS_WorldStreamProxy.Obf_fbA();
            var all = Object.FindObjectsOfType<CP_ColliderBase>(); // ponytail: scene scan every 10 frames; a unit registry if it costs
            CP_ColliderBase best = null;
            float bestDist = Range;
            foreach (var c in all)
            {
                if (!c.isActiveAndEnabled || !Verbs.ContainsKey(c.m_iType)) continue;
                float d = Vector3.Distance(from, c.transform.position);
                if (d < bestDist) { best = c; bestDist = d; }
            }
            if (Diagnostics.Verbose && best != _target)
                Debug.Log("[Ro3Emu.Interact] nearest " + (best == null ? "none" : best.m_iUUID + " type " + best.m_iType + " at " + bestDist.ToString("0.0") + " m") + ", player " + from);
            return best;
        }

        private static void Show(bool on)
        {
            if (!on) { if (_prompt != null && _prompt.gameObject.activeSelf) _prompt.gameObject.SetActive(false); return; }
            if (_prompt == null)
            {
                _prompt = Overlay.Rect("Ro3emuInteract");
                _prompt.anchorMin = _prompt.anchorMax = _prompt.pivot = new Vector2(0.5f, 0f);
                var bg = _prompt.gameObject.AddComponent<Image>();
                bg.sprite = Shapes.RoundedRect;
                bg.color = new Color(0.05f, 0.04f, 0.035f, 0.8f);
                bg.raycastTarget = false;
                _glyph = Overlay.Image("Glyph", _prompt, Color.white);
                _glyph.preserveAspect = true;
                _glyph.rectTransform.anchoredPosition = new Vector2(12f, 8f);
                _glyph.rectTransform.sizeDelta = new Vector2(40f, 40f);
                _label = Overlay.Text("Label", _prompt, 28);
                _label.alignment = TextAlignmentOptions.MidlineLeft;
                _label.rectTransform.anchoredPosition = new Vector2(62f, 4f);
                _label.rectTransform.sizeDelta = new Vector2(200f, 48f);
                _prompt.sizeDelta = new Vector2(230f, 56f);
            }
            float s = Overlay.Scale;
            _prompt.localScale = new Vector3(s, s, 1f);
            _prompt.anchoredPosition = new Vector2(0f, 190f * s);
            if (!_prompt.gameObject.activeSelf) _prompt.gameObject.SetActive(true);
        }
    }
}
