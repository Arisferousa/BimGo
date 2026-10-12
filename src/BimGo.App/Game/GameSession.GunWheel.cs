using System.Numerics;
using BimGo.Audio;
using BimGo.Game.Guns;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// The gun wheel (input round B2, build B): the guns on a ring around the crosshair. Mouse (MMB by default): hold,
    /// move the mouse towards a gun, release to pick it (released near the centre: nothing changes). Gamepad (the move
    /// stick's click by default): click, point the look stick at a gun, then A or the click again picks it; B, Start or
    /// Esc closes it. The number keys still pick guns directly. Looking pauses while it is open; walking doesn't.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        private bool _wheelOpen;
        private bool _wheelByPad;
        private Vector2 _wheelPointer;
        private int _wheelHover = -1;

        /// <summary>The wheel's radius (unscaled px): the mouse pointer stops at its edge.</summary>
        private const float WHEEL_RADIUS = 150f;

        /// <summary>Within this share of the radius the mouse pointer picks nothing.</summary>
        private const float WHEEL_DEAD = 0.3f;

        #endregion

        /// <summary>
        /// Walking (no gun holding the keys): opens, steers and closes the wheel.
        /// </summary>
        /// <returns>True while it is open (the walking keys wait).</returns>
        private bool UpdateGunWheel(InputState input)
        {
            if (!_wheelOpen)
            {
                if (!input.Pressed(InputAction.GunWheel)) { return false; }
                _wheelOpen = true;
                _wheelByPad = input.PressedByPad(InputAction.GunWheel);
                _wheelPointer = Vector2.Zero;
                _wheelHover = -1;
                Sound.Play(SoundId.UiClick);
                return true;
            }

            if (_wheelByPad)
            {
                // The look stick points at a gun; the gun stays highlighted when the stick springs back
                Vector2 stick = Gamepad.LookStick(input);
                if (stick.LengthSquared() >= 0.25f) { _wheelHover = WheelSlot(new Vector2(stick.X, -stick.Y)); }
                if (input.Pressed(InputAction.GunWheel) || input.Pressed(InputAction.PadClick)) { CloseGunWheel(pick: true); }
                else if (input.Pressed(InputAction.PadMenuBack)) { CloseGunWheel(pick: false); }
                return true;
            }

            // Mouse: the pointer follows the raw movement (the cursor stays captured), clamped to the ring
            float radius = S(WHEEL_RADIUS);
            _wheelPointer += new Vector2(input.MouseDeltaX, input.MouseDeltaY) * 0.8f;
            if (_wheelPointer.Length() > radius) { _wheelPointer = Vector2.Normalize(_wheelPointer) * radius; }
            _wheelHover = _wheelPointer.Length() >= radius * WHEEL_DEAD ? WheelSlot(_wheelPointer) : -1;
            if (!input.Down(InputAction.GunWheel)) { CloseGunWheel(pick: true); }
            return true;
        }

        /// <summary>Closes the wheel, picking the highlighted gun (the same gun: nothing changes).</summary>
        private void CloseGunWheel(bool pick)
        {
            if (!_wheelOpen) { return; }
            _wheelOpen = false;
            if (pick && _wheelHover >= 0 && _wheelHover < _guns.Length && _wheelHover != _activeGun) { SelectGun(_wheelHover); }
            else if (!pick) { Sound.Play(SoundId.UiClick); }
            _wheelHover = -1;
        }

        /// <summary>The gun a direction points at (screen space, y down): gun 1 at the top, then clockwise.</summary>
        private int WheelSlot(Vector2 direction)
        {
            int count = _guns.Length;
            float angle = MathF.Atan2(direction.Y, direction.X) + MathF.PI * 0.5f; // 0 = up
            float step = MathF.Tau / count;
            int slot = (int)MathF.Round(angle / step);
            return ((slot % count) + count) % count;
        }

        /// <summary>
        /// Draws the wheel: a dark disc, a wedge per gun (the highlighted one in its colour) with its icon and key, the
        /// highlighted (or current) gun's name in the middle, and how to pick.
        /// </summary>
        private void BuildGunWheel(FontAtlas f, int width, int height)
        {
            if (!_wheelOpen) { return; }
            float cx = MathF.Round(width * 0.5f), cy = MathF.Round(height * 0.5f);
            float radius = S(WHEEL_RADIUS), inner = radius * 0.34f;
            int count = _guns.Length;
            float step = MathF.Tau / count;

            _ui.Circle(cx, cy, radius + S(10), Rgba.Hex(0x0C0E12, 0.78f), 48);
            for (int i = 0; i < count; i++)
            {
                Gun gun = _guns[i];
                float mid = -MathF.PI * 0.5f + i * step;
                bool hover = i == _wheelHover, current = i == _activeGun;
                if (hover) { _ui.Wedge(cx, cy, radius, mid - step * 0.5f, mid + step * 0.5f, Rgba.WithAlpha(gun.Colour, 0.35f), 10); }

                // Separator
                float edge = mid - step * 0.5f;
                _ui.Line(cx + MathF.Cos(edge) * inner, cy + MathF.Sin(edge) * inner, cx + MathF.Cos(edge) * radius, cy + MathF.Sin(edge) * radius,
                    MathF.Max(1f, UiScale), Rgba.Hex(0xFFFFFF, 0.12f));

                float ix = cx + MathF.Cos(mid) * radius * 0.68f, iy = cy + MathF.Sin(mid) * radius * 0.68f;
                gun.DrawIcon(_ui, ix, iy, S(hover ? 34 : 28), hover || current ? gun.Colour : UiTheme.TEXT_MUTED);
                _ui.TextCentred(f.Small, ix, iy + S(18), gun.Key, hover ? gun.Colour : UiTheme.TEXT_FAINT);
            }
            _ui.Ring(cx, cy, radius, MathF.Max(1f, UiScale), Rgba.Hex(0xFFFFFF, 0.18f), 48);
            _ui.Circle(cx, cy, inner, Rgba.Hex(0x0C0E12, 0.9f), 32);

            Gun shown = _wheelHover >= 0 ? _guns[_wheelHover] : _guns[_activeGun];
            _ui.TextCentred(f.Bold, cx, cy - f.Bold.LineHeight * 0.5f, shown.Name, _wheelHover >= 0 ? shown.Colour : UiTheme.TEXT_MUTED, S(1f));
            _ui.TextCentred(f.Small, cx, cy + radius + S(18), _wheelByPad ? HINT_WHEEL_PAD.Text : HINT_WHEEL_MOUSE.Text, UiTheme.TEXT_SOFT);

            // The mouse pointer
            if (!_wheelByPad) { _ui.Circle(cx + _wheelPointer.X, cy + _wheelPointer.Y, S(4), UiTheme.TEXT, 12); }
        }

        private static readonly KeyHint HINT_WHEEL_MOUSE = new(() => "Move towards a gun and let go · let go in the middle to keep this one");
        private static readonly KeyHint HINT_WHEEL_PAD = new(() =>
            $"Point at a gun · {Hotkeys.Map.PadLabel(InputAction.PadClick)} or {Hotkeys.Map.PadLabel(InputAction.GunWheel)} picks · {Hotkeys.Map.PadLabel(InputAction.PadMenuBack)} closes");
    }
}
