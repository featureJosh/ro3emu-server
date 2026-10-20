using System;
using System.Collections.Generic;
using Game.Collider;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Controller targeting. Every selection goes through the game's own path, LC_SelectorManager:SelectRoleUnit(unitType, uuid,
    /// nil, nil, nil, optType): the call the mouse click makes (LC_Actor_ControlMove.HandleColliderBase, lua:1545-1562, optType
    /// Manual) and the stock SwitchTarget makes (LC_SelectorManager.ChooseTarget, lua:1927-1962, optType Tab). That sends the
    /// game's own PLAYER_LOCK_ENEMY (LC_SelectorManager.lua ~line 950), updates the target frame
    /// (UI_Sys_Main_TargetAvatarInfoWidget_PC, which is not among the widgets ControllerHud hides or lifts) and is what skills read
    /// as the target (LC_Skill_ActionPreCheck.CheckPreTargetValidNew, lua:2160-2346: the selected unit, then camp relation by the
    /// skill's select info, then casting range). No packet is built here.
    /// <list type="bullet">
    /// <item>Soft lock: with no target, a skill press (NormalSkill_01..06, ExpandSkill_01..02) selects the nearest hostile monster in
    /// a wide cone of the left stick (camera forward when the stick is idle; any direction if the cone is empty). With the stick
    /// pushed and no target, the nearest hostile in a narrow cone is selected after a short dwell. SoftLock = 0 turns both off.</item>
    /// <item>TargetCycle (R1): next hostile monster in <see cref="TargetMath.Cycle"/> order (distance plus angle penalty from the
    /// camera); falls back to the stock ChooseTarget when none of our candidates qualifies. It replaces the stock SwitchTarget binding.</item>
    /// <item>TargetLock (R3 tap): hard lock on the selected monster (or the soft-lock pick). The lock re-selects it when the game
    /// changes the target, and ends when the unit dies, disappears or is further than TargetLockRange metres.</item>
    /// <item>AllySelect (default L2 + R1, a layer slot; press): selects the nearest party member, again for the next one (LC_CampRelationUtils.
    /// IsSameTeamByUnit, Utility/LC_CampRelationUtils.lua:736). A skill pressed while an ally is selected is checked against that
    /// unit by CheckPreTargetValidNew, so a heal or buff whose select info allows friends lands on them (inferred; in game). A hard
    /// lock yields for AllyHold seconds after the last L1 hold, then comes back.</item>
    /// </list>
    /// Candidates come from the scene's CP_ColliderBase (m_iType = enum_UnitType, m_iUUID = the unit uuid, as the click path
    /// passes it); the game then vets each one (IsEnemyByUnit, IsDead, IsDying, IsHide) and refuses what it cannot select.
    /// </summary>
    internal static class Targeting
    {
        private const int Monster = 1, Player = 3; // enum_UnitType UT_Monster, UT_Player (Config/Define/Lua_GameDefine.lua:238-250)
        private static readonly string[] Attacks =
            { "NormalSkill_01", "NormalSkill_02", "NormalSkill_03", "NormalSkill_04", "NormalSkill_05", "NormalSkill_06", "ExpandSkill_01", "ExpandSkill_02" };

        private static long? _lock;
        private static CP_ColliderBase _lockCollider;
        private static float _lockDown = -1f, _lastReselect, _stickSince = -1f, _lastSoft, _allyUntil;
        private static int _frame;
        private static bool _frameWarned, _noTeamWarned;
        private static RectTransform _tag;

        /// <summary>From Controller.Tick in the world (no menu, no cursor).</summary>
        internal static void Tick(Gamepad pad, bool active)
        {
            try
            {
                if (!active || pad == null) { _lockDown = -1f; _stickSince = -1f; ShowTag(false); return; }
                _frame++;
                float now = Time.unscaledTime;
                if (Controller.Pressed(pad, "TargetLock")) _lockDown = now;
                if (Controller.Released(pad, "TargetLock") && _lockDown >= 0f)
                {
                    if (now - _lockDown < 0.3f) ToggleLock(pad); // a longer hold is CameraZoom on the shared control
                    _lockDown = -1f;
                }
                if (Controller.Pressed(pad, "TargetCycle")) CycleTarget(pad);
                if (Controller.Held(pad, "AllySelect")) _allyUntil = now + Controller.Setting("AllyHold", 1.5f);
                if (Controller.Pressed(pad, "AllySelect")) SelectAlly();
                if (Controller.Setting("SoftLock", 1f) > 0f) SoftLock(pad, now);
                if (_frame % 10 == 0) { EnforceLock(now); CheckFrame(); }
                ShowTag(_lock.HasValue);
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.Targeting] " + e.GetType().Name + ": " + e.Message); }
        }

        // ---- soft lock ----

        private static void SoftLock(Gamepad pad, float now)
        {
            if (_lock.HasValue || now < _allyUntil) { _stickSince = -1f; return; }
            bool attack = false;
            foreach (var a in Attacks) if (Controller.Pressed(pad, a)) { attack = true; break; }
            var stick = pad.leftStick.ReadValue();
            bool tilted = stick.magnitude > Math.Max(0.5f, Controller.Setting("Deadzone", 0.2f));
            if (!tilted) _stickSince = -1f;
            else if (_stickSince < 0f) _stickSince = now;
            bool walking = tilted && now - _stickSince > 0.3f && now - _lastSoft > 0.6f;
            if (!attack && !walking) return;
            if (Selected().HasValue) return;
            _lastSoft = now;
            Facing(stick, tilted, out float fx, out float fz);
            float range = Controller.Setting("SoftRange", 15f);
            // Walking: a narrow cone, so the pad does not grab whatever is nearby. Attacking: a wide cone, then any direction.
            var found = Pick(Monster, fx, fz, attack ? 70f : 40f, range);
            if (found == null && attack) found = Pick(Monster, 0f, 0f, 180f, range);
            if (found.HasValue) Debug.Log("[Ro3Emu.Targeting] soft lock " + found.Value + (attack ? " (attack)" : " (stick)"));
        }

        // ---- cycle ----

        private static void CycleTarget(Gamepad pad)
        {
            if (!Ready(out var g)) return;
            float fx = 0f, fz = 0f;
            if (Camera.main != null) { var f = Camera.main.transform.forward; fx = f.x; fz = f.z; } // cycling walks outwards from the camera
            var from = WS_WorldStreamProxy.Obf_fbA();
            var order = TargetMath.Cycle(from.x, from.z, fx, fz, Points(Scan(Monster)), Controller.Setting("CycleRange", 25f));
            var cursor = Selected();
            for (int tries = 0; tries < order.Count; tries++)
            {
                var next = TargetMath.Next(order, cursor);
                if (!next.HasValue) break;
                if (Hostile(next.Value) && Choose(Monster, next.Value, "Tab"))
                {
                    if (_lock.HasValue) Lock(next.Value);
                    Debug.Log("[Ro3Emu.Targeting] cycle -> " + next.Value);
                    return;
                }
                cursor = next; // dead, hidden or not selectable: try the one after it
            }
            // Nothing of ours qualified: the game's own tab-target (what the stock SwitchTarget action runs).
            var mgr = g.Obf_RuA<Obf_R.Obf_ff>("LC_SelectorManager");
            mgr.Obf_RuA<Obf_R.Obf_Ef>("ChooseTarget")?.Obf_HuA(mgr);
            Debug.Log("[Ro3Emu.Targeting] cycle: stock ChooseTarget");
        }

        // ---- hard lock ----

        private static void ToggleLock(Gamepad pad)
        {
            if (_lock.HasValue) { Unlock("toggled off"); return; }
            var sel = Selected();
            if (!sel.HasValue)
            {
                var stick = pad.leftStick.ReadValue();
                Facing(stick, stick.magnitude > 0.5f, out float fx, out float fz);
                sel = Pick(Monster, fx, fz, 70f, Controller.Setting("SoftRange", 15f));
            }
            if (!sel.HasValue) { Debug.Log("[Ro3Emu.Targeting] lock: no target"); return; }
            Lock(sel.Value);
        }

        private static void Lock(long id)
        {
            _lock = id;
            _lockCollider = null;
            Debug.Log("[Ro3Emu.Targeting] hard lock on " + id);
        }

        private static void Unlock(string why)
        {
            if (!_lock.HasValue) return;
            Debug.Log("[Ro3Emu.Targeting] lock on " + _lock.Value + " ended: " + why);
            _lock = null;
            _lockCollider = null;
        }

        private static void EnforceLock(float now)
        {
            if (!_lock.HasValue) return;
            long id = _lock.Value;
            var unit = Unit(id);
            if (unit == null || IsTrue(Call(unit, "IsDead")) || IsTrue(Call(unit, "IsDying"))) { Unlock("gone or dead"); return; }
            if (_lockCollider == null || !_lockCollider.isActiveAndEnabled || Id(_lockCollider) != id)
            {
                _lockCollider = null;
                foreach (var c in Scan(Monster)) if (c.id == id) { _lockCollider = c.collider; break; }
            }
            if (_lockCollider == null) { Unlock("out of view"); return; }
            if (Vector3.Distance(WS_WorldStreamProxy.Obf_fbA(), _lockCollider.transform.position) > Controller.Setting("TargetLockRange", 30f))
            { Unlock("out of range"); return; }
            if (now < _allyUntil || now - _lastReselect < 0.5f) return;
            if (Selected() != id) { _lastReselect = now; Choose(Monster, id, "Manual"); }
        }

        // ---- allies ----

        private static void SelectAlly()
        {
            if (!Ready(out var g)) return;
            var util = g.Obf_RuA<Obf_R.Obf_ff>("LC_CampRelationUtils");
            var sameTeam = util?.Obf_RuA<Obf_R.Obf_Ef>("IsSameTeamByUnit");
            if (sameTeam == null)
            {
                if (!_noTeamWarned) { _noTeamWarned = true; Debug.LogWarning("[Ro3Emu.Targeting] LC_CampRelationUtils.IsSameTeamByUnit missing: no ally select"); }
                return;
            }
            var from = WS_WorldStreamProxy.Obf_fbA();
            var mates = new List<(long id, CP_ColliderBase collider)>();
            foreach (var c in Scan(Player))
            {
                var unit = Unit(c.id);
                if (unit == null || IsTrue(Call(unit, "IsLocalPlayer")) || IsTrue(Call(unit, "IsDead"))) continue;
                if (IsTrue(First(sameTeam.Obf_HuA(unit)))) mates.Add(c);
            }
            var order = TargetMath.Cycle(from.x, from.z, 0f, 0f, Points(mates), Controller.Setting("AllyRange", 30f));
            var next = TargetMath.Next(order, Selected());
            if (!next.HasValue) { Debug.Log("[Ro3Emu.Targeting] ally select: no party member in range"); return; }
            if (Choose(Player, next.Value, "Manual")) Debug.Log("[Ro3Emu.Targeting] ally -> " + next.Value);
        }

        // ---- shared helpers ----

        private static bool Ready(out Obf_R.Obf_ff g)
        {
            g = LuaPatches.Global;
            return g?.Obf_RuA<Obf_R.Obf_ff>("LC_SelectorManager") != null;
        }

        private static void Facing(Vector2 stick, bool tilted, out float fx, out float fz)
        {
            var cam = Camera.main;
            Vector3 fwd = cam ? cam.transform.forward : Vector3.forward, right = cam ? cam.transform.right : Vector3.right;
            fwd.y = right.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) { fwd = Vector3.forward; right = Vector3.right; }
            fwd.Normalize();
            right.Normalize();
            var dir = tilted ? fwd * stick.y + right * stick.x : fwd;
            fx = dir.x;
            fz = dir.z;
        }

        private static long Id(CP_ColliderBase c) => unchecked((long)c.m_iUUID);

        private static List<(long id, CP_ColliderBase collider)> Scan(int type)
        {
            var list = new List<(long, CP_ColliderBase)>();
            foreach (var c in UnityEngine.Object.FindObjectsOfType<CP_ColliderBase>()) // ponytail: scene scan per press / 10 frames, like Interact
                if (c.isActiveAndEnabled && c.m_iType == type) list.Add((Id(c), c));
            return list;
        }

        private static List<TargetPoint> Points(List<(long id, CP_ColliderBase collider)> scan)
        {
            var points = new List<TargetPoint>(scan.Count);
            foreach (var s in scan) { var p = s.collider.transform.position; points.Add(new TargetPoint(s.id, p.x, p.z)); }
            return points;
        }

        /// <summary>Selects the nearest hostile monster in the cone that the game accepts; its id, or null.</summary>
        private static long? Pick(int type, float fx, float fz, float halfAngle, float range)
        {
            if (!Ready(out _)) return null;
            var from = WS_WorldStreamProxy.Obf_fbA();
            var order = TargetMath.Cone(from.x, from.z, fx, fz, Points(Scan(type)), halfAngle, range);
            int tries = 0;
            foreach (var id in order)
            {
                if (++tries > 5) break;
                if (Hostile(id) && Choose(type, id, "Tab")) return id;
            }
            return null;
        }

        private static bool Choose(int type, long id, string option)
        {
            var g = LuaPatches.Global;
            var mgr = g?.Obf_RuA<Obf_R.Obf_ff>("LC_SelectorManager");
            var select = mgr?.Obf_RuA<Obf_R.Obf_Ef>("SelectRoleUnit");
            if (select == null) return false;
            object unitType = g.Obf_LuA<object>(type == Monster ? "enum_UnitType.UT_Monster" : "enum_UnitType.UT_Player");
            object opt = g.Obf_LuA<object>("enum_SelectOptType." + option);
            select.Obf_HuA(mgr, unitType, id, null, null, null, opt);
            return Selected() == id; // the game refuses what it cannot select (dying, hidden, no-select NPCs)
        }

        /// <summary>The selected unit's uuid (LC_SelectorManager.GetSelectUnit exists, GetSelectUnitUUID), or null.</summary>
        private static long? Selected()
        {
            var mgr = LuaPatches.Global?.Obf_RuA<Obf_R.Obf_ff>("LC_SelectorManager");
            if (mgr == null || Call(mgr, "GetSelectUnit") == null) return null;
            var uuid = Call(mgr, "GetSelectUnitUUID");
            try { return uuid == null ? (long?)null : Convert.ToInt64(uuid); } catch (Exception) { return null; }
        }

        private static Obf_R.Obf_ff Unit(long id)
        {
            var finder = LuaPatches.Global?.Obf_RuA<Obf_R.Obf_ff>("LC_UnitFinder");
            return First(finder?.Obf_RuA<Obf_R.Obf_Ef>("FindUnitWithSimpleUnit")?.Obf_HuA(id)) as Obf_R.Obf_ff;
        }

        private static bool Hostile(long id)
        {
            var unit = Unit(id);
            if (unit == null) return false;
            var enemy = LuaPatches.Global?.Obf_RuA<Obf_R.Obf_ff>("LC_CampRelationUtils")?.Obf_RuA<Obf_R.Obf_Ef>("IsEnemyByUnit");
            return enemy != null && IsTrue(First(enemy.Obf_HuA(unit)))
                && !IsTrue(Call(unit, "IsDead")) && !IsTrue(Call(unit, "IsDying")) && !IsTrue(Call(unit, "IsHide"));
        }

        private static object Call(Obf_R.Obf_ff self, string method)
        {
            var f = self?.Obf_RuA<Obf_R.Obf_Ef>(method);
            return f == null ? null : First(f.Obf_HuA(self));
        }

        private static object First(object[] result) => result != null && result.Length > 0 ? result[0] : null;
        private static bool IsTrue(object value) => value is bool b && b;

        // ---- target frame + lock tag ----

        /// <summary>The stock target frame must show in controller mode. ControllerHud only hides the skill bar, the item bar and
        /// Pattern_GraphicSwitchG, so it should; this warns once if something hides it while a target is selected.</summary>
        private static void CheckFrame()
        {
            if (_frameWarned || !Selected().HasValue) return;
            var widget = GameObject.Find("UI_Sys_Main_TargetAvatarInfoWidget_PC(Clone)");
            if (widget == null) return;
            var group = widget.GetComponent<CanvasGroup>();
            if (!widget.activeInHierarchy || group != null && group.alpha <= 0f)
            { _frameWarned = true; Debug.LogWarning("[Ro3Emu.Targeting] stock target frame is hidden while a target is selected"); }
        }

        private static void ShowTag(bool on)
        {
            if (!on) { if (_tag != null && _tag.gameObject.activeSelf) _tag.gameObject.SetActive(false); return; }
            if (_tag == null)
            {
                _tag = Overlay.Rect("Ro3emuLockTag");
                _tag.anchorMin = _tag.anchorMax = _tag.pivot = new Vector2(0.5f, 1f);
                var label = Overlay.Text("Label", _tag, 26);
                label.alignment = TextAlignmentOptions.Midline;
                label.text = "LOCKED";
                label.color = new Color(0.86f, 0.7f, 0.4f, 1f);
                label.rectTransform.sizeDelta = new Vector2(200f, 36f);
                _tag.sizeDelta = new Vector2(200f, 36f);
            }
            float s = Overlay.Scale;
            _tag.localScale = new Vector3(s, s, 1f);
            _tag.anchoredPosition = new Vector2(0f, -170f * s);
            if (!_tag.gameObject.activeSelf) _tag.gameObject.SetActive(true);
        }
    }
}
