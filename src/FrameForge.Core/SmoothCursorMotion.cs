using System.Numerics;

namespace FrameForge.Core;

/// <summary>Inertial cursor animation with a damped turn. Coordinates are capture pixels.</summary>
public sealed class SmoothCursorMotion(CursorMotionStyle style = CursorMotionStyle.Curve, int smoothnessMilliseconds = 700)
{
    readonly double _omega = 10d / (Math.Clamp(smoothnessMilliseconds, 250, 1500) / 1000d);
    readonly double _turn = style == CursorMotionStyle.Spiral ? 1.15 : 0.45;
    double _x, _y, _vx, _vy;
    bool _initialized;

    public Vector2 Position => new((float)_x, (float)_y);
    public Vector2 Velocity => new((float)_vx, (float)_vy);

    public void Reset(float x, float y)
    {
        _x = x;
        _y = y;
        _vx = _vy = 0;
        _initialized = true;
    }

    public void Advance(float targetX, float targetY, double seconds, bool precise = false)
    {
        if (!float.IsFinite(targetX) || !float.IsFinite(targetY) || !double.IsFinite(seconds) || seconds < 0)
            return;
        if (!_initialized)
        {
            Reset(targetX, targetY);
            return;
        }
        if (seconds == 0)
            return;

        // Small bounded steps keep the same feel at 15, 30, 60 and 120 FPS.
        double elapsed = Math.Min(seconds, 1d);
        int steps = Math.Max(1, (int)Math.Ceiling(elapsed * 480));
        double dt = elapsed / steps;
        double omega = precise ? Math.Max(26, _omega) : _omega;
        double turn = precise ? 0 : _turn;
        for (int i = 0; i < steps; i++)
        {
            double ax = omega * omega * (targetX - _x) - 2 * omega * _vx - turn * omega * _vy;
            double ay = omega * omega * (targetY - _y) - 2 * omega * _vy + turn * omega * _vx;
            _vx += ax * dt;
            _vy += ay * dt;
            _x += _vx * dt;
            _y += _vy * dt;
        }

        double dx = targetX - _x, dy = targetY - _y;
        if (dx * dx + dy * dy < 0.04 && _vx * _vx + _vy * _vy < 1)
            Reset(targetX, targetY);
    }
}
