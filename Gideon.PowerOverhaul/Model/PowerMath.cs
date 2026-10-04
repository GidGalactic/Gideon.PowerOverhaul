using Sandbox.ModAPI;
using VRage.Game.ModAPI;

namespace Gideon.PowerOverhaul.Model
{
    public static class PowerMath
    {
        public const float ROUTE_EFF = 0.50f;

        public const float EMERGENCY_THROTTLE_CAP = 0.20f;
        public const float EMERGENCY_CONTROL_R_CAP = 0.25f;
        public const float EMERGENCY_OUTPUT_CAP = 0.10f;

        // Keep required MW as heuristics for now (your system uses rough values anyway)
        public static float GetProducerMW(IMyCubeBlock b)
        {
            if (b is Sandbox.ModAPI.IMyBatteryBlock bat)
                return (float)bat.CurrentOutput;

            if (b is Sandbox.ModAPI.IMyReactor rx)
                return (float)rx.CurrentOutput;

            // Solar/Wind interfaces differ between SE versions/mod API. Skip for now to avoid compile issues.
            // You can add them later once we confirm the exact types available in your API.
            return 0f;
        }
    }
}
