using Point = OpenCvSharp.Point;

namespace StoryPilot.Engine;

/// <summary>
/// Which way to walk, in degrees relative to where the camera looks (0 = straight ahead, + = right),
/// blended from the quest trail and the quest marker.
/// </summary>
public static class Heading
{
    /// <summary>Rough horizontal focal length at 1080p (the game's ~90° wide view).</summary>
    private const double FocalPx = Vision.Width / 2.0;

    /// <summary>Trail and marker this close together agree: favour the marker and cut the corners.</summary>
    public const double AgreeDegrees = 25;

    /// <summary>Trail and marker this far apart: the trail is going round something (a wall, up stairs), follow it.</summary>
    public const double DivergeDegrees = 45;

    /// <summary>The marker counts more the nearer the quest is: mostly marker under this…</summary>
    public const int NearMetres = 30;

    /// <summary>…mostly trail beyond this.</summary>
    public const int FarMetres = 120;

    public static double Of(Point screen) => Math.Atan((screen.X - Vision.Width / 2.0) / FocalPx) * 180 / Math.PI;

    /// <summary>Horizontal screen offset (px from the middle) of a heading.</summary>
    public static double ToOffset(double degrees) => Math.Tan(Math.Clamp(degrees, -80, 80) * Math.PI / 180) * FocalPx;

    /// <summary>Share (0..1) of the marker in the blend, from how far the quest is and how much trail and marker agree.</summary>
    public static double MarkerWeight(double trail, double marker, int? metres)
    {
        var byDistance = metres is { } m ? Lerp(0.85, 0.25, (m - NearMetres) / (double)(FarMetres - NearMetres)) : 0.5;
        var agreeing = Math.Max(byDistance, 0.75);
        var diverging = byDistance * 0.2;
        return Lerp(agreeing, diverging, (Math.Abs(trail - marker) - AgreeDegrees) / (DivergeDegrees - AgreeDegrees));
    }

    /// <summary>Blended heading from whichever of trail and marker are on screen; null when neither is.</summary>
    public static double? Blend(double? trail, double? marker, int? metres) => (trail, marker) switch
    {
        ({ } t, { } m) => t + (m - t) * MarkerWeight(t, m, metres),
        ({ } t, null) => t,
        (null, { } m) => m,
        _ => null,
    };

    private static double Lerp(double a, double b, double t) => a + (b - a) * Math.Clamp(t, 0, 1);
}

/// <summary>
/// Smooths the heading over time so small zigzags and frame-to-frame jitter don't make the walk weave.
/// A big change that holds for two readings (a real new direction) is taken at once.
/// Headings are camera-relative, so turning the camera moves them: <see cref="Turned"/> shifts the smoothed
/// value by the expected amount (otherwise it lags behind our own turns and the walk swings), using a turn rate
/// (degrees per mouse px, which depends on the game's camera sensitivity) learned from how readings respond.
/// </summary>
public sealed class HeadingFilter
{
    /// <summary>Starting guess for the turn rate (a common camera sensitivity).</summary>
    public const double DefaultDegreesPerPx = 0.2;

    /// <summary>Only turns at least this big (px) are used to learn the turn rate: smaller ones are lost in jitter.</summary>
    private const int LearnFromPx = 20;

    private double _degreesPerPx = DefaultDegreesPerPx;
    private int _turnedPx;
    private double? _lastRaw;

    public double DegreesPerPx => _degreesPerPx;

    /// <summary>The camera was just turned by <paramref name="px"/> mouse px.</summary>
    public void Turned(int px)
    {
        _turnedPx = px;
        if (_value is { } v)
        {
            _value = v - px * _degreesPerPx;
        }
    }

    /// <summary>Share of each new reading.</summary>
    public const double Smoothing = 0.45;

    public const double JumpDegrees = 35;

    /// <summary>The last few readings within this many degrees of each other: steady enough to sprint.</summary>
    public const double SteadyDegrees = 12;

    private const int SteadyReadings = 4;

    private readonly Queue<double> _recent = new();
    private double? _value;
    private int _jumpSide;

    public double Update(double raw)
    {
        if (Math.Abs(_turnedPx) >= LearnFromPx && _lastRaw is { } last)
        {
            var rate = (last - raw) / _turnedPx;
            if (rate is > 0.03 and < 1.5)
            {
                _degreesPerPx = 0.6 * _degreesPerPx + 0.4 * rate;
            }
        }

        _turnedPx = 0;
        _lastRaw = raw;
        _recent.Enqueue(raw);
        while (_recent.Count > SteadyReadings)
        {
            _recent.Dequeue();
        }

        if (_value is not { } value)
        {
            _value = raw;
            return raw;
        }

        var change = raw - value;
        if (Math.Abs(change) > JumpDegrees)
        {
            var side = Math.Sign(change);
            if (_jumpSide == side)
            {
                _jumpSide = 0;
                _value = raw;
                return raw;
            }

            // One odd reading: note it, but only nudge toward it.
            _jumpSide = side;
            change = side * JumpDegrees;
        }
        else
        {
            _jumpSide = 0;
        }

        _value = value + Smoothing * change;
        return _value.Value;
    }

    public bool Steady => _recent.Count >= SteadyReadings && _recent.Max() - _recent.Min() < SteadyDegrees;

    /// <summary>Lost sight of trail and marker (or turned round to look): start afresh, keeping the learned turn rate.</summary>
    public void Reset()
    {
        _recent.Clear();
        _value = null;
        _jumpSide = 0;
        _turnedPx = 0;
        _lastRaw = null;
    }
}
