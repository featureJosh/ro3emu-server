using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// LC_DAL_Setting._processBaseBattleInfo loads BaseBattleSetting, but the reconstructed client's
    /// LC_SettingProcessFunc.ContinuousAttackAfterTargetSelected.GetValue reads a separate flat asset.
    /// Use the loaded platform preference until the player's settings setter supplies the flat value.
    /// Evidence: Logic/Asset/LC_DAL_Setting.lua and Logic/Setting/LC_SettingProcessFunc.lua:942-1005.
    /// </summary>
    internal static class CombatSettings
    {
        // xLua keeps delegate references weakly; retain the wrapper for the lifetime of the process.
        public delegate long ReadSetting();
        private static readonly ReadSetting Wrapper = Read;
        private static Obf_R.Obf_Ef _original, _getValue, _getBattleSetting;
        private static object _chaseLogic;
        private static bool _reportedFallback;
        private static int _readTrace;

        internal static void Install(Obf_R.Obf_ff global, Obf_R.Obf_ff process, Obf_R.Obf_ff helper)
        {
            var types = global?.Obf_RuA<Obf_R.Obf_ff>("enum_SettingItemType");
            var attack = types?.Obf_RuA<object>("ContinuousAttackAfterTargetSelected");
            var chase = types?.Obf_RuA<object>("ChaseLogic");
            var definition = attack == null ? null : process?.Obf_ruA<object, Obf_R.Obf_ff>(attack);
            var setting = definition?.Obf_RuA<Obf_R.Obf_ff>("ValueSetting");
            var original = setting?.Obf_RuA<Obf_R.Obf_Ef>("GetValue");
            var getValue = helper?.Obf_RuA<Obf_R.Obf_Ef>("GetValue");
            var battle = global?.Obf_RuA<Obf_R.Obf_ff>("G_ClientCfg")?.Obf_RuA<Obf_R.Obf_Ef>("getBattleSetting");
            if (original == null || getValue == null || battle == null || chase == null)
            {
                Debug.LogWarning("[Ro3Emu.CombatSettings] setting functions not found; click-to-attack fallback not installed");
                return;
            }
            _original = original;
            _getValue = getValue;
            _getBattleSetting = battle;
            _chaseLogic = chase;
            _reportedFallback = false;
            _readTrace = 0;
            setting.Obf_luA<string, ReadSetting>("GetValue", Wrapper);
            Debug.Log("[Ro3Emu.CombatSettings] click-to-attack setting fallback installed");
        }

        private static long First(object[] result)
            => result != null && result.Length > 0 ? LuaPatches.ToLong(result[0]) : long.MinValue;

        private static long Read()
        {
            long value = First(_original.Obf_HuA());
            // 1 = enabled, 2 = disabled. Preserve an explicit value supplied by the client's setter.
            if (value == 1 || value == 2) return Trace(value, value, "explicit flat preference");
            long chase = First(_getValue.Obf_HuA(_chaseLogic));
            if (chase != 1) return Trace(value, 0, "chase=" + chase);
            // Resolve each read, rather than caching: settings and PC/mobile mode can change after login.
            long loaded = First(_getBattleSetting.Obf_HuA("eSelectTargetNormalAttack"));
            if (loaded != 1 && loaded != 2) return Trace(value, 0, "invalid loaded preference=" + loaded);
            if (!_reportedFallback)
            {
                _reportedFallback = true;
                Debug.Log("[Ro3Emu.CombatSettings] missing flat attack setting; using loaded battle preference " + loaded);
            }
            return Trace(value, loaded, "loaded battle preference");
        }

        private static long Trace(long original, long effective, string reason)
        {
            if (_readTrace++ < 20)
                Debug.Log("[Ro3Emu.CombatSettings] read original=" + original + " effective=" + effective + " " + reason);
            return effective;
        }
    }
}
