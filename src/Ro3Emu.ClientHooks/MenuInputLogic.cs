using System;

namespace Ro3Emu.ClientHooks
{
    /// <summary>Pure rules behind the menu-input extras (<see cref="MenuInput"/>): double-click timing, which large click
    /// cards become focus targets, and the model-swipe step. No Unity types, so the tests compile it.</summary>
    internal static class MenuInputLogic
    {
        /// <summary>Counts ✕ presses on the same target inside a window, like a mouse: the second quick press is clickCount 2
        /// (PointerEventData.clickCount), which is what the stock double-click wrappers act on (Lua_UIDragButton.lua:609-637
        /// SetOnDoubleClick -> UIDragButton.onPointerDoubleClick; inferred: the C# component derives it from clickCount).</summary>
        internal sealed class ClickCounter
        {
            private int _target = int.MinValue, _count;
            private float _at = float.NegativeInfinity;

            /// <summary>The clickCount for a press on <paramref name="target"/> at <paramref name="now"/>; window &lt;= 0 never double-clicks.</summary>
            public int Press(int target, float now, float window)
            {
                bool again = window > 0f && target == _target && now - _at <= window && _count == 1;
                _count = again ? 2 : 1;
                _target = target;
                _at = now;
                return _count;
            }

            public void Reset() { _target = int.MinValue; _count = 0; _at = float.NegativeInfinity; }
        }

        internal enum Size { Small, Large, Blocker }

        /// <summary>UiNav's Small filter is 40% of the screen in both directions; a card above that is Large (a fallback focus
        /// target); one that covers 90% in both is a Blocker (full-screen click catcher or backdrop) and never one.</summary>
        internal static Size Classify(float width, float height, float screenWidth, float screenHeight)
        {
            if (width >= screenWidth * 0.9f && height >= screenHeight * 0.9f) return Size.Blocker;
            return width <= screenWidth * 0.4f && height <= screenHeight * 0.4f ? Size.Small : Size.Large;
        }

        /// <summary>Large cards are focus targets only when the panel has nothing else to focus.</summary>
        internal static bool UseLarge(int focusableCount, int largeCount) => focusableCount == 0 && largeCount > 0;

        /// <summary>Pixels of horizontal drag for one frame of right-stick input past the dead zone (0 inside it).</summary>
        internal static float SwipeDelta(float stickX, float deadzone, float pixelsPerSecond, float dt)
        {
            float a = Math.Abs(stickX);
            if (a <= deadzone || deadzone >= 1f) return 0f;
            float scaled = (a - deadzone) / (1f - deadzone);
            return Math.Sign(stickX) * scaled * pixelsPerSecond * dt;
        }
    }
}
