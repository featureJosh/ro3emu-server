using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Diagnostics for click-to-attack, active only with RO3EMU_CLICK_PROBE=1 in the client's environment. Logs to
    /// Player.log, tagged [Ro3Emu.ClickProbe]:
    /// <list type="bullet">
    /// <item>LC_Actor_ControlMove.HandleColliderBase(self, hit, isClick) for clicks: the hit unit, the method's own
    /// gates (CheckUnitType, IsInBlackArea) and what its attack branch reads (ContinuousAttackAfterTargetSelected,
    /// ChaseLogic, the flat eSelectTargetNormalAttack asset, CurrentNormalAttackSkillId).
    /// Evidence: Logic/Actor/LC_Actor_ControlMove.lua:1530-1666.</item>
    /// <item>LC_Event.DispatchEvent for Battle.RJ_onClickSkill: whether the normal-attack event is sent.</item>
    /// </list>
    /// Every wrapper calls the original with the same arguments.
    /// </summary>
    internal static class ClickProbe
    {
        public delegate void Collider(object self, object hit, object click);
        public delegate object Dispatch(object self, object module, object notify, object data, object a5, object a6);

        // xLua keeps delegate references weakly; retain the wrappers for the lifetime of the process.
        private static readonly Collider ColliderWrapper = OnCollider;
        private static readonly Dispatch DispatchWrapper = OnDispatch;
        private static Obf_R.Obf_ff _global;
        private static Obf_R.Obf_Ef _collider, _dispatch;
        private static object _clickSkill, _battle;
        private static int _lines;

        internal static bool Enabled => Environment.GetEnvironmentVariable("RO3EMU_CLICK_PROBE") == "1";

        internal static void Install(Obf_R.Obf_ff global, Obf_R.Obf_ff controlMove)
        {
            if (!Enabled) return;
            _global = global;
            try
            {
                _collider = controlMove?.Obf_RuA<Obf_R.Obf_Ef>("HandleColliderBase");
                if (_collider != null) controlMove.Obf_luA<string, Collider>("HandleColliderBase", ColliderWrapper);
                var events = global.Obf_RuA<Obf_R.Obf_ff>("LC_Event");
                _clickSkill = global.Obf_LuA<object>("LC_NotifyId.Battle.RJ_onClickSkill");
                _battle = global.Obf_LuA<object>("LC_ModuleId.Battle");
                _dispatch = events?.Obf_RuA<Obf_R.Obf_Ef>("DispatchEvent");
                if (_dispatch != null && _clickSkill != null) events.Obf_luA<string, Dispatch>("DispatchEvent", DispatchWrapper);
                Log("installed: HandleColliderBase " + (_collider != null ? "wrapped" : "NOT FOUND")
                    + ", DispatchEvent " + (_dispatch != null && _clickSkill != null ? "wrapped (RJ_onClickSkill=" + _clickSkill + ")" : "NOT FOUND"));
            }
            catch (Exception e) { Log("install failed: " + e.GetType().Name + ": " + e.Message); }
        }

        private static void OnCollider(object self, object hit, object click)
        {
            if (Truthy(click))
            {
                try { Log("click " + Describe(self, hit)); }
                catch (Exception e) { Log("click (describe failed: " + e.GetType().Name + ": " + e.Message + ")"); }
            }
            _collider.Obf_HuA(self, hit, click);
        }

        private static object OnDispatch(object self, object module, object notify, object data, object a5, object a6)
        {
            // Notify ids are per module: only Battle's RJ_onClickSkill is the attack event.
            if (Equals(notify, _clickSkill) && Equals(module, _battle))
            {
                string detail = data is Obf_R.Obf_ff t
                    ? "iSkillId=" + t.Obf_RuA<object>("iSkillId") + " bOnlyClick=" + t.Obf_RuA<object>("bOnlyClick") : "data=" + data;
                Log("RJ_onClickSkill dispatched: " + detail);
            }
            var result = _dispatch.Obf_HuA(self, module, notify, data, a5, a6);
            return result != null && result.Length > 0 ? result[0] : null;
        }

        private static string Describe(object self, object hit)
        {
            var h = hit as Obf_R.Obf_ff;
            object uuid = h?.Obf_RuA<object>("m_iUUID"), type = h?.Obf_RuA<object>("m_iType");
            var me = self as Obf_R.Obf_ff;
            object interact = First(me?.Obf_RuA<Obf_R.Obf_Ef>("CheckUnitType")?.Obf_HuA(me, uuid));
            object blackArea = First(me?.Obf_RuA<Obf_R.Obf_Ef>("IsInBlackArea")?.Obf_HuA(me, uuid));
            object enemy = _global.Obf_LuA<object>("enum_InteractUnitType.Enemy");
            object dropped = _global.Obf_LuA<object>("enum_UnitType.UT_DroppedItem");

            var helper = _global.Obf_LuA<Obf_R.Obf_ff>("package.loaded.lc_settinghelper");
            var get = helper?.Obf_RuA<Obf_R.Obf_Ef>("GetSettingValue");
            object attack = First(get?.Obf_HuA(_global.Obf_LuA<object>("enum_SettingItemType.ContinuousAttackAfterTargetSelected")));
            object chase = First(get?.Obf_HuA(_global.Obf_LuA<object>("enum_SettingItemType.ChaseLogic")));

            var location = _global.Obf_RuA<Obf_R.Obf_ff>("ServerLocation");
            var assets = First(location?.Obf_RuA<Obf_R.Obf_Ef>("GetServer")?.Obf_HuA(location, "LC_AssetManager")) as Obf_R.Obf_ff;
            var data = assets?.Obf_RuA<Obf_R.Obf_Ef>("GetAssetData");
            object flat = First(data?.Obf_HuA(assets, "eSelectTargetNormalAttack"));
            object normal = First(data?.Obf_HuA(assets, "CurrentNormalAttackSkillId"));

            return "uuid=" + uuid + " unitType=" + type + " (dropped=" + dropped + ") interactType=" + interact
                + " (enemy=" + enemy + ") IsInBlackArea=" + blackArea
                + " | ContinuousAttack=" + attack + " ChaseLogic=" + chase + " flat=" + (flat ?? "nil")
                + " CurrentNormalAttackSkillId=" + (normal ?? "nil");
        }

        private static object First(object[] r) => r != null && r.Length > 0 ? r[0] : null;

        private static bool Truthy(object v) => v != null && !(v is bool b && !b);

        private static void Log(string message)
        {
            if (++_lines > 500) return; // a stuck loop must not flood the log
            Debug.Log("[Ro3Emu.ClickProbe] " + message);
        }
    }
}
