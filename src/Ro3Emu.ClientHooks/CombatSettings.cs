using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Clicking an enemy attacks it only when ContinuousAttackAfterTargetSelected (setting 403001, the LOG tab's
    /// "attack after selecting a target") is 1 (LC_Actor_ControlMove.HandleColliderBase). A character's first mode
    /// pick, UI_Sys_SetModeWidgetCtrl._saveGameModeSettingReal while m_bPcPlatformRecord is false, sets it to 1 for the
    /// classic mode and 2 (off) for the action mode, so a new action-mode character could not click-attack. Official
    /// servers attacked on click in both modes (player report; inferred), so after that first pick the setting is set to
    /// 1 through the same lc_settinghelper.SetValue the LOG tab uses (it saves to the server too). Later mode changes and
    /// the LOG-tab toggle are left alone. Evidence: UI/Setup/Controllers/UI_Sys_SetModeWidgetCtrl.lua:448-470,
    /// docs/research/click-attack-setting.md.
    /// </summary>
    internal static class CombatSettings
    {
        /// <summary>A Lua method called as self:Method().</summary>
        public delegate void SaveMode(Obf_R.Obf_ff self);

        // xLua keeps delegate references weakly; retain the wrapper for the lifetime of the process.
        private static readonly SaveMode Wrapper = SaveGameModeSettingReal;
        private static Obf_R.Obf_Ef _original, _setValue;
        private static object _attackSetting;

        internal static void Install(Obf_R.Obf_ff global, Obf_R.Obf_ff modeWidget, Obf_R.Obf_ff helper)
        {
            var attack = global?.Obf_RuA<Obf_R.Obf_ff>("enum_SettingItemType")?.Obf_RuA<object>("ContinuousAttackAfterTargetSelected");
            var original = modeWidget?.Obf_RuA<Obf_R.Obf_Ef>("_saveGameModeSettingReal");
            var setValue = helper?.Obf_RuA<Obf_R.Obf_Ef>("SetValue");
            if (attack == null || original == null || setValue == null)
            {
                Debug.LogWarning("[Ro3Emu.CombatSettings] mode-pick functions not found; first mode pick keeps the client's click-to-attack default");
                return;
            }
            _original = original;
            _setValue = setValue;
            _attackSetting = attack;
            modeWidget.Obf_luA<string, SaveMode>("_saveGameModeSettingReal", Wrapper);
            Debug.Log("[Ro3Emu.CombatSettings] first mode pick turns click-to-attack on");
        }

        private static void SaveGameModeSettingReal(Obf_R.Obf_ff self)
        {
            bool firstPick = self?.Obf_RuA<object>("m_bPcPlatformRecord") is false;
            _original.Obf_HuA(self);
            if (!firstPick) return;
            _setValue.Obf_HuA(_attackSetting, 1L);
            Debug.Log("[Ro3Emu.CombatSettings] first mode pick: click-to-attack set to 1");
        }
    }
}
