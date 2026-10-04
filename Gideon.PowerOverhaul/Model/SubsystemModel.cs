using Gideon.PowerOverhaul.Net;
using Gideon.PowerOverhaul.Util;
using System;
using VRage.Game;

namespace Gideon.PowerOverhaul.Model
{
    public class SubsystemModel
    {
        public readonly GridPowerModel Grid;
        public readonly int Id;
        public readonly SystemKind Kind;

        public string Name;
        public bool PowerOn = true;

        public float LocalMW;
        public float RequiredMW;
        public float IncomingMW;

        // Routing (live)
        public int RouteTargetId;
        public float RoutePercent;

        // Routing (draft until Apply)
        public int DraftRouteTargetId;
        public float DraftRoutePercent;

        // Burst
        public float BurstPercent = 1.10f; // 110%
        public float BurstDuration = 10f;
        public bool IsBursting;
        public float BurstRemaining;

        // Stress
        public float Stress;

        // Flags
        public bool IsSacrificed;

        public bool EmergencyFed;

        public SubsystemModel(GridPowerModel grid, int id, SystemKind kind, string name)
        {
            Grid = grid;
            Id = id;
            Kind = kind;
            Name = name ?? kind.ToString();
        }

        public void TickServer()
        {
            float dt = MyEngineConstants.UPDATE_STEP_SIZE_IN_SECONDS;

            // Burst timer
            if (IsBursting)
            {
                BurstRemaining -= dt;
                if (BurstRemaining <= 0f)
                {
                    IsBursting = false;
                    BurstRemaining = 0f;
                }

                // Stress from burst
                var over = Math.Max(0f, BurstPercent - 1f);
                Stress += over * 12f * dt;
            }
            else
            {
                // stress decay
                Stress -= 1.8f * dt;
            }

            // Routing stress (donor)
            if (RoutePercent > 0f && RouteTargetId != 0)
                Stress += (RoutePercent / 100f) * 2.5f * dt;

            Stress = Util.MathUtil.Clamp(Stress, 0f, 100f);

            // TODO: implement failure rolls per spec at time intervals
        }

        public void StartBurst()
        {
            if (!PowerOn) return;
            IsBursting = true;
            BurstRemaining = BurstDuration;
        }

        public SystemSnapshot BuildSnapshot(bool debugEnabled)
        {
            uint flags = 0;
            if (IsSacrificed) flags |= (uint)SystemFlags.Degraded;
            if (EmergencyFed) flags |= (uint)SystemFlags.EmergencyFed;

            return new SystemSnapshot
            {
                SystemId = Id,
                Kind = (byte)Kind,
                Name = Name,
                PowerOn = PowerOn,

                LocalMW = LocalMW,
                RequiredMW = RequiredMW,
                IncomingMW = IncomingMW,

                RouteTargetId = RouteTargetId,
                RoutePercent = RoutePercent,

                IsBursting = IsBursting,
                BurstRemaining = BurstRemaining,

                Stress = debugEnabled ? Stress : 0f,
                Flags = (uint)(IsSacrificed ? (SystemFlags.Degraded) : 0)
            };
        }
    }
}
