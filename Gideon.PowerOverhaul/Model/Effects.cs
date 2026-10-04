using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using System;
using System.Collections.Generic;

namespace Gideon.PowerOverhaul.Model
{
    public static class Effects
    {
        public static void ApplyControlScaling(IMyCubeGrid grid, AssignmentStore assign, int controlSystemId, float r)
        {
            // Your locked curve:
            // Gyros: r^0.60
            // Rotors/Hinges/Pistons speed: r^1.30
            // Rotors/Hinges/Pistons torque: r^1.60
            float gyroScale = (float)Math.Pow(r, 0.60);
            float artSpeed = (float)Math.Pow(r, 1.30);
            float artTorque = (float)Math.Pow(r, 1.60);

            if (r < 0.08f)
            {
                artSpeed = 0f; // stall
                artTorque = 0f;
            }

            var blocks = new List<IMySlimBlock>();
            grid.GetBlocks(blocks);

            foreach (var slim in blocks)
            {
                var fat = slim.FatBlock as IMyCubeBlock;
                if (fat == null) continue;

                if (assign.Get(fat.EntityId) != controlSystemId) continue;

                if (fat is IMyGyro g)
                {
                    // NOTE: There is no direct "power multiplier" API; use GyroPower to simulate authority.
                    // This modifies responsiveness and is safe.
                    g.GyroPower = Math.Max(0.01f, gyroScale);
                }
                else if (fat is IMyMotorStator rotor)
                {
                    rotor.TargetVelocityRPM *= artSpeed;
                    rotor.Torque *= artTorque;
                }
                else if (fat is IMyMotorAdvancedStator hinge)
                {
                    hinge.TargetVelocityRPM *= artSpeed;
                    hinge.Torque *= artTorque;
                }
                else if (fat is IMyPistonBase piston)
                {
                    piston.Velocity *= artSpeed;
                    // TODO: piston impulse limiting not supported in this API version

                }
            }
        }

        public static void ApplyPropulsionScaling(IMyCubeGrid grid, AssignmentStore assign, int propulsionSystemId, float scale)
        {
            var blocks = new List<IMySlimBlock>();
            grid.GetBlocks(blocks);

            foreach (var slim in blocks)
            {
                var fat = slim.FatBlock as IMyCubeBlock;
                if (fat == null) continue;

                if (assign.Get(fat.EntityId) != propulsionSystemId) continue;

                if (fat is IMyThrust t)
                {
                    // Thrust limiter: use ThrustOverridePercentage if available, else MaxThrust override.
                    // Safer: clamp override multiplier by setting ThrustMultiplier isn't available; we emulate by limiting overrides.
                    // If player uses manual overrides, they get scaled down.
                    if (t.ThrustOverridePercentage > 0f)
                        t.ThrustOverridePercentage *= scale;
                }
            }
        }
    }
}
