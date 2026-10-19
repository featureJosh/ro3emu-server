using System;
using System.Text;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Pure cooldown logic for the controller HUD (no Unity types, so it is unit tested): turns what the stock slot widget
    /// shows into what our slot draws. The stock widgets are driven by Lua every frame; we only mirror them.
    /// <list type="bullet">
    /// <item>Skill slot (UI_Com_StrandedCountDownChargeWidgetCtrl.lua:_evaluateBaseUpdate, ~930-975): while the remaining time
    /// is above zero Mask_Img and Time_TTxt are active, BarTime_Img.fillAmount = remaining / total, Time_TTxt = FormatCountDownText
    /// (UI_Com_CountDownComponentBase.lua:11-55: "4s", "1.5s" under 3 s, "2m" from 120 s, "1h", "1d"). At zero the text goes off;
    /// charge / storage skills may keep the mask with a partial fill (EvaluateCDEnd), which is a sweep without seconds.</item>
    /// <item>Potion slot (UI_Sys_Main_PropsItemWidgetCtrl.lua:Update, ~690-750): CD_RTransform active while the item cooldown runs,
    /// CD_Img.fillAmount = remaining / duration, CD_TTxt = ceil(remaining) as a bare number. The item cell's MaskItem_Img
    /// (GetMaskProgress, same file ~330-350) is the fallback: remaining / total, or 1 when the item cannot be used in this map.</item>
    /// </list>
    /// </summary>
    internal static class CooldownMirror
    {
        internal struct Reading
        {
            public bool Sweep;      // draw the radial dark sweep
            public float Fill;      // 0..1, the share of the sweep still dark (remaining / total)
            public string Seconds;  // "" when the stock slot shows no number
            public bool Active => Sweep || Seconds.Length > 0;
        }

        /// <summary>One stock indicator: its mask / container is active, its fill and its text (with the text object's own
        /// active state: a hidden Time_TTxt keeps its stale text).</summary>
        internal static Reading Read(bool maskActive, float fill, bool textActive, string text)
        {
            float f = float.IsNaN(fill) ? 0f : Math.Max(0f, Math.Min(1f, fill));
            return new Reading { Sweep = maskActive && f > 0.001f, Fill = f, Seconds = textActive ? Seconds(text) : "" };
        }

        /// <summary>The number the slot shows: stock labels as they are ("4s", "1.5s", "2m"), bare potion numbers get an "s".
        /// Rich-text tags are dropped; idle placeholder text (anything not starting with a digit) is no number.</summary>
        internal static string Seconds(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            bool tag = false;
            foreach (char c in text)
            {
                if (c == '<') tag = true;
                else if (c == '>') tag = false;
                else if (!tag && !char.IsWhiteSpace(c)) sb.Append(c);
            }
            if (sb.Length == 0 || !char.IsDigit(sb[0])) return "";
            if (char.IsDigit(sb[sb.Length - 1])) sb.Append('s');
            return sb.ToString();
        }

        /// <summary>Fires once when a slot goes from cooling to ready.</summary>
        internal struct ReadyEdge
        {
            private bool _was;
            /// <summary>True on the frame the cooldown ended. <paramref name="reset"/>: the slot is empty or just rebound, so no flash.</summary>
            public bool Update(bool active, bool reset = false)
            {
                bool ended = _was && !active && !reset;
                _was = active && !reset;
                return ended;
            }
        }
    }
}
