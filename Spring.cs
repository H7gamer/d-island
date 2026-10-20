// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

namespace DIsland;

/// <summary>Damped harmonic spring, integrated in small fixed sub-steps for stability.</summary>
public sealed class Spring
{
    private const double SubStep = 1.0 / 240.0;

    public Spring(double value, double stiffness = 260, double damping = 22)
    {
        Value = Target = value;
        Stiffness = stiffness;
        Damping = damping;
    }

    public double Value { get; private set; }
    public double Velocity { get; private set; }
    public double Target { get; set; }
    public double Stiffness { get; set; }
    public double Damping { get; set; }

    public bool IsSettled => Math.Abs(Target - Value) < 0.05 && Math.Abs(Velocity) < 0.05;

    public void Step(double dt)
    {
        while (dt > 0)
        {
            var h = Math.Min(SubStep, dt);
            var accel = Stiffness * (Target - Value) - Damping * Velocity;
            Velocity += accel * h;
            Value += Velocity * h;
            dt -= h;
        }
    }

    public void Snap()
    {
        Value = Target;
        Velocity = 0;
    }
}
