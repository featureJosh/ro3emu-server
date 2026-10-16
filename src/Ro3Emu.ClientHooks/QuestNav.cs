using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Auto-path to the tracked quest with the pad. On PC the player clicks the quest in the main UI tracker; that click is
    /// UI_Sys_Main_TeamUp_Task_ListWidgetCtrl._onClickFrame (re/clientB/lua-src/UI/Main/Controllers/
    /// UI_Sys_Main_TeamUp_Task_ListWidgetCtrl.lua:1568-1672). This repeats its logic for the quest the tracker shows first
    /// (or the one it last selected, asset "MainSelectTaskID"), from LC_TaskHelper.GetCurrentMainTrackTask
    /// (LC_TaskHelper.lua:1113; finished quests come first), calling the same game functions:
    /// <list type="bullet">
    /// <item>Running quest (lua:1585-1620): LC_TaskHelper.IsRunningTaskOpen(info) (Logic/Task/LC_TaskHelper.lua:2816; call at lua:1587),
    /// then LC_TaskHelper.DealFollow(info) (lua:1596; LC_TaskHelper.lua:1365-1427; runs the quest's track steps, which start the
    /// auto-path), then MainSelectTaskID = id (lua:1608); if it is not open, the tracker's tip (UIWords 35029, lua:1617).</item>
    /// <item>Finished quest (lua:1622-1651): lc_interactactions.PathFindingNormal(map, npc, 9, ...) (lua:1633;
    /// Logic/Interact/LC_InteractActions.lua:1501) to the hand-in NPC
    /// (config GetDeliverTask), or LC_TaskHelper.SendTaskGetward (lua:1645) for hand-in by UI.</item>
    /// </list>
    /// Not repeated: the tutorial-guide event (_onTryDispatchGuideTaskRunningEvent) and the failed-quest branch (lua:1652-1668, opens the
    /// quest panel). Cancelling: not added here. The game's joystick move start cancels an auto-path
    /// (LC_Actor_ControlMove.lua:548-557, UserCancel); that the pad's MoveForward/... actions reach it is an in-game check.
    /// </summary>
    internal static class QuestNav
    {
        private const long TipWord = 35029;

        /// <summary>Starts the auto-path; false (logged) when there is no quest to go to or the game Lua is not ready.</summary>
        internal static bool Go()
        {
            try { return Run(); }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.QuestNav] " + e.GetType().Name + ": " + e.Message); return false; }
        }

        private static bool Run()
        {
            var g = LuaPatches.Global;
            var helper = g?.Obf_RuA<Obf_R.Obf_ff>("LC_TaskHelper");
            if (helper == null) { Debug.Log("[Ro3Emu.QuestNav] game Lua not ready"); return false; }
            var location = g.Obf_RuA<Obf_R.Obf_ff>("ServerLocation");
            var assets = First(location?.Obf_RuA<Obf_R.Obf_Ef>("GetServer")?.Obf_HuA(location, "LC_AssetManager")) as Obf_R.Obf_ff;
            var tasks = First(helper.Obf_RuA<Obf_R.Obf_Ef>("GetCurrentMainTrackTask")?.Obf_HuA()) as Obf_R.Obf_ff;
            var selected = First(assets?.Obf_RuA<Obf_R.Obf_Ef>("GetAssetData")?.Obf_HuA(assets, "MainSelectTaskID"));
            Obf_R.Obf_ff info = null;
            for (long i = 1; tasks?.Obf_ruA<long, object>(i) is Obf_R.Obf_ff t; i++)
            {
                if (info == null) info = t;
                if (selected != null && Same(t.Obf_RuA<object>("id"), selected)) { info = t; break; }
            }
            if (info == null) { Debug.Log("[Ro3Emu.QuestNav] no tracked quest"); return false; }
            object id = info.Obf_RuA<object>("id"), status = info.Obf_RuA<object>("status");
            var dbm = g.Obf_RuA<Obf_R.Obf_ff>("Lua_DBManager");
            dbm = First(dbm?.Obf_RuA<Obf_R.Obf_Ef>("GetInstance")?.Obf_HuA(dbm)) as Obf_R.Obf_ff;
            if (Same(status, g.Obf_LuA<object>("PBTaskState.TaskState_Running")))
            {
                if (!(First(helper.Obf_RuA<Obf_R.Obf_Ef>("IsRunningTaskOpen")?.Obf_HuA(info)) is true))
                {
                    var words = dbm?.Obf_RuA<Obf_R.Obf_ff>("m_kCFG_UIWords");
                    object tip = First(words?.Obf_RuA<Obf_R.Obf_Ef>("GetWordNew")?.Obf_HuA(words, TipWord));
                    g.Obf_LuA<Obf_R.Obf_ff>("LC_TipHelper")?.Obf_RuA<Obf_R.Obf_Ef>("PopCommonTips")?.Obf_HuA(tip);
                    Debug.Log("[Ro3Emu.QuestNav] quest " + id + " is not open yet");
                    return false;
                }
                helper.Obf_RuA<Obf_R.Obf_Ef>("DealFollow").Obf_HuA(info);
                assets?.Obf_RuA<Obf_R.Obf_Ef>("ExecCmd")?.Obf_HuA(assets, g.Obf_LuA<object>("enum_AssetManager_Cmd.Replace"), "MainSelectTaskID", id);
                Debug.Log("[Ro3Emu.QuestNav] following quest " + id);
                return true;
            }
            if (Same(status, g.Obf_LuA<object>("PBTaskState.TaskState_Finish")))
            {
                var config = dbm?.Obf_RuA<Obf_R.Obf_ff>("m_kCFG_TaskConfig");
                var deliver = First(config?.Obf_RuA<Obf_R.Obf_Ef>("GetDeliverTask")?.Obf_HuA(config, id)) as Obf_R.Obf_ff;
                object type = deliver?.Obf_ruA<long, object>(1);
                if (Same(type, g.Obf_LuA<object>("enum_TaskRewardType.TaskAutoFinish_Npc")))
                {
                    var actions = g.Obf_LuA<Obf_R.Obf_ff>("package.loaded.lc_interactactions");
                    actions?.Obf_RuA<Obf_R.Obf_Ef>("PathFindingNormal")?.Obf_HuA(deliver.Obf_ruA<long, object>(3), deliver.Obf_ruA<long, object>(4), 9L, deliver.Obf_ruA<long, object>(5));
                    Debug.Log("[Ro3Emu.QuestNav] quest " + id + " done: walking to the hand-in NPC");
                    return actions != null;
                }
                if (Same(type, g.Obf_LuA<object>("enum_TaskRewardType.TaskAutoFinish_UI")))
                {
                    helper.Obf_RuA<Obf_R.Obf_Ef>("SendTaskGetward")?.Obf_HuA(id);
                    Debug.Log("[Ro3Emu.QuestNav] quest " + id + " done: reward requested");
                    return true;
                }
            }
            Debug.Log("[Ro3Emu.QuestNav] quest " + id + " status " + status + ": nothing to follow");
            return false;
        }

        private static object First(object[] results) => results != null && results.Length > 0 ? results[0] : null;

        private static bool Same(object a, object b) => a != null && b != null && Convert.ToInt64(a) == Convert.ToInt64(b);
    }
}
