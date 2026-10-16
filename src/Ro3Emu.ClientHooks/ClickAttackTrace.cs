using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>Bounded trace of the real click path. Every wrapper forwards the original arguments and results.</summary>
    internal static class ClickAttackTrace
    {
        public delegate void Raycast(Obf_R.Obf_ff self, object position, object collider, object click, object normal);
        public delegate void Collider(Obf_R.Obf_ff self, object collider, object click);
        public delegate object UnitCheck(Obf_R.Obf_ff self, object id);
        private static readonly Raycast RaycastWrapper = OnRaycast;
        private static readonly Collider ColliderWrapper = OnCollider;
        private static readonly UnitCheck TypeWrapper = CheckType, AreaWrapper = CheckArea;
        private static Obf_R.Obf_Ef _raycast, _collider, _type, _area;
        private static Obf_R.Obf_ff _global;
        private static int _clicks;
        private static bool _tracing;

        internal static void Install(Obf_R.Obf_ff global, Obf_R.Obf_ff control)
        {
            _raycast = control?.Obf_RuA<Obf_R.Obf_Ef>("TouchRaycastPos");
            _collider = control?.Obf_RuA<Obf_R.Obf_Ef>("HandleColliderBase");
            _type = control?.Obf_RuA<Obf_R.Obf_Ef>("CheckUnitType");
            _area = control?.Obf_RuA<Obf_R.Obf_Ef>("IsInBlackArea");
            if (_raycast == null || _collider == null || _type == null || _area == null)
            {
                Debug.LogWarning("[Ro3Emu.ClickAttackTrace] click functions missing; trace not installed");
                return;
            }
            _global = global;
            _clicks = 0;
            _tracing = false;
            control.Obf_luA<string, Raycast>("TouchRaycastPos", RaycastWrapper);
            control.Obf_luA<string, Collider>("HandleColliderBase", ColliderWrapper);
            control.Obf_luA<string, UnitCheck>("CheckUnitType", TypeWrapper);
            control.Obf_luA<string, UnitCheck>("IsInBlackArea", AreaWrapper);
            Debug.Log("[Ro3Emu.ClickAttackTrace] installed; tracing first 30 world clicks");
        }

        private static object First(object[] result) => result != null && result.Length > 0 ? result[0] : null;
        private static object Field(object target, string name) => target is Obf_R.Obf_ff table
            ? table.Obf_RuA<object>(name) : target?.GetType().GetField(name)?.GetValue(target);
        private static bool IsClick(object value) => value != null && (!(value is bool flag) || flag);

        private static void OnRaycast(Obf_R.Obf_ff self, object position, object collider, object click, object normal)
        {
            bool previous = _tracing;
            _tracing = IsClick(click) && ++_clicks <= 30;
            try
            {
                if (_tracing) Debug.Log("[Ro3Emu.ClickAttackTrace] raycast click=" + click
                    + " collider=" + (collider?.GetType().FullName ?? "nil") + " position=" + position);
                _raycast.Obf_HuA(self, position, collider, click, normal);
            }
            finally { _tracing = previous; }
        }

        private static void OnCollider(Obf_R.Obf_ff self, object collider, object click)
        {
            try
            {
                if (_tracing)
                {
                    var asset = _global.Obf_RuA<Obf_R.Obf_ff>("LC_AssetManagerIns");
                    var read = asset?.Obf_RuA<Obf_R.Obf_Ef>("GetAssetData");
                    var skill = read == null ? null : First(read.Obf_HuA(asset, "CurrentNormalAttackSkillId"));
                    Debug.Log("[Ro3Emu.ClickAttackTrace] HandleColliderBase click=" + click
                        + " uuid=" + Field(collider, "m_iUUID") + " unitType=" + Field(collider, "m_iType")
                        + " normalAttackSkill=" + (skill ?? "nil"));
                }
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.ClickAttackTrace] snapshot: " + e.Message); }
            _collider.Obf_HuA(self, collider, click);
        }

        private static object CheckType(Obf_R.Obf_ff self, object id)
        {
            object result = First(_type.Obf_HuA(self, id));
            if (_tracing) Debug.Log("[Ro3Emu.ClickAttackTrace] CheckUnitType=" + result
                + " enemyEnum=" + _global.Obf_RuA<Obf_R.Obf_ff>("enum_InteractUnitType")?.Obf_RuA<object>("Enemy"));
            return result;
        }

        private static object CheckArea(Obf_R.Obf_ff self, object id)
        {
            object result = First(_area.Obf_HuA(self, id));
            if (_tracing) Debug.Log("[Ro3Emu.ClickAttackTrace] IsInBlackArea (visibility allowed)=" + result);
            return result;
        }
    }
}
