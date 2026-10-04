using Gideon.PowerOverhaul.Net;
using Gideon.PowerOverhaul.Util;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage.Game;
using VRage.Game.ModAPI;
using System.Linq;
using VRage.ModAPI;

namespace Gideon.PowerOverhaul.Model
{
    public class GridPowerModel
    {
        public readonly long GridId;

        public readonly AssignmentStore Assignments = new AssignmentStore();

        // subscribers (steam ids) that have UI open
        readonly HashSet<ulong> _uiSubs = new HashSet<ulong>();

        // Subsystems registry: id -> subsystem
        readonly Dictionary<int, SubsystemModel> _systems = new Dictionary<int, SubsystemModel>(32);
        int _nextSystemId = 1;

        // Main multi-routing: targetId -> percent (0..100); total < 100
        readonly Dictionary<int, float> _mainRoutes = new Dictionary<int, float>(16);
        readonly Dictionary<int, float> _mainRoutesDraft = new Dictionary<int, float>(16);

        // Harmonic load
        bool _harmonicActive;
        float _mainDrawMW;

        // Main burst settings/state
        float _mainBurstPercent = 1.10f; // 110% default, as per your latest
        float r = 10f;
        bool _mainBursting;
        float _mainBurstRemaining;
        float _mainStress;
        float _mainBurstDuration = 5f; // default, you can tune


        // Aux/Emergency stresses (per system)
        float _auxStress;
        float _emeStress;

        bool _emergencyActive;
        float _emergencyAvailableMW;

        // Settings
        const float HARMONIC_BASE_BONUS = 0.25f;

        // Main power presence tracking (for blackout trigger)
        bool _mainHasPower;
        bool _mainHadPowerPrev;

        // Consolidation flag (if you already track it elsewhere, use that)
        bool _consolidationActive;

        // Control loss
        bool _controlLoss;
        float _controlLossTime;
        readonly Dictionary<long, bool> _controlLossDisabledBlocks = new Dictionary<long, bool>(512);
        // stores blockEntityId -> previousEnabledState (usually true)

        // Saved “last-commanded” state capture
        readonly Dictionary<long, float> _lossThrusterOverride = new Dictionary<long, float>(256);
        readonly Dictionary<long, bool> _lossGyroOverride = new Dictionary<long, bool>(128);
        readonly Dictionary<long, float> _lossGyroPower = new Dictionary<long, float>(128);
        readonly HashSet<long> _disabledControllers = new HashSet<long>();

        // Blackout
        float _blackoutRemaining;

        public GridPowerModel(long gridId)
        {
            GridId = gridId;

            // Create default templates (player can add more later)
            EnsureDefaultSystems();
        }

        void EnsureDefaultSystems()
        {
            // Keep at least one of each template
            if (!HasAnyKind(SystemKind.Propulsion)) AddSubsystem(SystemKind.Propulsion, "Propulsion");
            if (!HasAnyKind(SystemKind.Control)) AddSubsystem(SystemKind.Control, "Control");
            if (!HasAnyKind(SystemKind.Weapons)) AddSubsystem(SystemKind.Weapons, "Weapons");

            // Jump unique: create if missing
            if (!HasAnyKind(SystemKind.Jump)) AddSubsystem(SystemKind.Jump, "Jump (Unique)", forcedId: SystemIds.JumpUnique);
        }

        bool HasAnyKind(SystemKind kind)
        {
            foreach (var s in _systems.Values)
                if (s.Kind == kind) return true;
            return false;
        }

        bool ShouldBeInControlLoss()
        {
            // Per spec: trigger if no main power OR emergency state entered
            if (!_mainHasPower) return true;
            if (_emergencyActive) return true;
            return false;
        }

        void EnterControlLoss()
        {
            if (_controlLoss) return;

            var grid = MyAPIGateway.Entities.GetEntityById(GridId) as IMyCubeGrid;
            if (grid == null) return;

            _controlLoss = true;
            _controlLossTime = 0f;

            _lossThrusterOverride.Clear();
            _lossGyroOverride.Clear();
            _lossGyroPower.Clear();
            _disabledControllers.Clear();
            _controlLossDisabledBlocks.Clear();

            var blocks = new List<IMySlimBlock>();
            grid.GetBlocks(blocks);

            foreach (var slim in blocks)
            {
                var fat = slim.FatBlock as IMyCubeBlock;
                if (fat == null) continue;

                // Disable ship controllers so player loses active input
                if (fat is Sandbox.ModAPI.IMyShipController sc)
                {
                    var fb = sc as Sandbox.ModAPI.IMyFunctionalBlock;
                    if (fb != null && fb.Enabled)
                    {
                        fb.Enabled = false;
                        _disabledControllers.Add(sc.EntityId);
                    }
                    continue;
                }


                // Freeze thrust “as-is” by converting current thrust output into override percentage
                if (fat is IMyThrust thr)
                {
                    float max = thr.MaxEffectiveThrust;
                    float cur = thr.CurrentThrust;

                    float pct = 0f;
                    if (max > 0.0001f) pct = (cur / max) * 100f;
                    pct = Util.MathUtil.Clamp(pct, 0f, 100f);

                    _lossThrusterOverride[thr.EntityId] = thr.ThrustOverridePercentage;
                    thr.ThrustOverridePercentage = pct;
                    continue;
                }

                // Gyros: preserve gyro power setting, then stop “new” input torque (let inertia continue)
                if (fat is IMyGyro g)
                {
                    _lossGyroOverride[g.EntityId] = g.GyroOverride;
                    _lossGyroPower[g.EntityId] = g.GyroPower;

                    // Turn override ON but with 0 yaw/pitch/roll to stop corrective input
                    g.GyroOverride = true;
                    g.Yaw = 0f; g.Pitch = 0f; g.Roll = 0f;
                    continue;
                }
            }
        }

        void DisableBlockForControlLoss(IMyCubeBlock fat)
        {
            var func = fat as IMyFunctionalBlock;
            if (func == null) return;

            // only record the first time we touch it
            if (!_controlLossDisabledBlocks.ContainsKey(fat.EntityId))
                _controlLossDisabledBlocks[fat.EntityId] = func.Enabled;

            func.Enabled = false;
        }

        void ExitControlLoss(bool restoreState)
        {
            if (!_controlLoss) return;

            var grid = MyAPIGateway.Entities.GetEntityById(GridId) as IMyCubeGrid;
            if (grid != null && restoreState)
            {
                var blocks = new List<IMySlimBlock>();
                grid.GetBlocks(blocks);

                foreach (var slim in blocks)
                {
                    var fat = slim.FatBlock as IMyCubeBlock;
                    if (fat == null) continue;

                    if (fat is Sandbox.ModAPI.IMyShipController sc)
                    {
                        var fb = sc as Sandbox.ModAPI.IMyFunctionalBlock;
                        if (fb != null && _disabledControllers.Contains(sc.EntityId))
                            fb.Enabled = true;
                        continue;
                    }


                    if (fat is IMyThrust thr)
                    {
                        if (_lossThrusterOverride.TryGetValue(thr.EntityId, out var old))
                            thr.ThrustOverridePercentage = old;
                        continue;
                    }

                    if (fat is IMyGyro g)
                    {
                        if (_lossGyroOverride.TryGetValue(g.EntityId, out var ov))
                            g.GyroOverride = ov;
                        if (_lossGyroPower.TryGetValue(g.EntityId, out var gp))
                            g.GyroPower = gp;

                        // Clear override axis commands
                        g.Yaw = 0f; g.Pitch = 0f; g.Roll = 0f;
                        continue;
                    }
                }
                // Re-enable blocks disabled by control loss timeline
                foreach (var kv in _controlLossDisabledBlocks)
                {
                    long blockId = kv.Key;
                    bool previousEnabled = kv.Value;

                    var ent = MyAPIGateway.Entities.GetEntityById(blockId) as IMyEntity;
                    var block = ent as IMyCubeBlock;
                    var func = block as IMyFunctionalBlock;

                    if (func == null) continue;

                    // restore to what it was before we disabled it (usually true)
                    func.Enabled = previousEnabled;
                }

            }

            _controlLoss = false;
            _controlLossTime = 0f;

            _lossThrusterOverride.Clear();
            _lossGyroOverride.Clear();
            _lossGyroPower.Clear();
            _disabledControllers.Clear();
            _controlLossDisabledBlocks.Clear();

        }

        public bool HasAnyUiSubscriber() => _uiSubs.Count > 0;
        public void AddUiSubscriber(ulong steamId) => _uiSubs.Add(steamId);
        public void RemoveUiSubscriber(ulong steamId) => _uiSubs.Remove(steamId);

        public void TickServer()
        {
            var grid = MyAPIGateway.Entities.GetEntityById(GridId) as IMyCubeGrid;
            if (grid == null) return;

            // Update timers
            float dt = MyEngineConstants.UPDATE_STEP_SIZE_IN_SECONDS;

            // Recompute power model first (updates _mainHasPower, _emergencyActive, subsystem MWs, etc.)
            RecomputeSubsystemPower();

            // Trigger blackout ONLY on main power loss transition
            CheckAndTriggerBlackout();

            // Blackout countdown
            if (_blackoutRemaining > 0f)
            {
                _blackoutRemaining = Math.Max(0f, _blackoutRemaining - dt);

                // No stacking: do not run control loss while blackout is active
                return;
            }

            // Control Loss entry/exit
            if (ShouldBeInControlLoss())
            {
                if (!_controlLoss)
                    EnterControlLoss();

                _controlLossTime += dt;
                ApplyControlLossTimeline();
            }
            else
            {
                if (_controlLoss)
                    ExitControlLoss(restoreState: true);
            }

            // Existing subsystem timers (burst/stress) can run regardless
            foreach (var s in _systems.Values)
                s.TickServer();

            // Apply your normal effects (control/thrust scaling based on power) AFTER power math
            ApplyCoreEffects();
        }


        void ApplyControlLossTimeline()
        {
            var grid = MyAPIGateway.Entities.GetEntityById(GridId) as IMyCubeGrid;
            if (grid == null) return;

            float t = _controlLossTime;

            var blocks = new List<IMySlimBlock>();
            grid.GetBlocks(blocks);

            // T+3: Control begins degrading (gyros weaken)
            if (t >= 3f)
            {
                foreach (var slim in blocks)
                {
                    var fat = slim.FatBlock as IMyCubeBlock;
                    if (fat is IMyGyro g)
                    {
                        // Gradually weaken gyro power between t=3..6 down to 0.2
                        float a = Util.MathUtil.Clamp((t - 3f) / 3f, 0f, 1f);
                        float target = 0.2f;
                        float basePower = _lossGyroPower.TryGetValue(g.EntityId, out var gp) ? gp : 1f;
                        g.GyroPower = Util.MathUtil.Clamp(basePower * (1f - a) + target * a, 0.01f, 1f);
                    }
                }
            }

            // T+6: Propulsion begins throttling down toward 0 (over 6..15)
            if (t >= 6f)
            {
                float a = Util.MathUtil.Clamp((t - 6f) / 9f, 0f, 1f); // 6→15
                float scale = 1f - a;

                foreach (var slim in blocks)
                {
                    var fat = slim.FatBlock as IMyCubeBlock;
                    if (fat is IMyThrust thr)
                    {
                        // Scale whatever override we set on entry
                        thr.ThrustOverridePercentage *= scale;
                    }
                }
            }

            // T+10: Non-essential subsystems power off (weapons/jump/industry/etc.)
            if (t >= 10f)
            {
                foreach (var slim in blocks)
                {
                    var fat = slim.FatBlock as IMyCubeBlock;
                    if (fat == null) continue;

                    int sysId = Assignments.Get(fat.EntityId);

                    // Keep Aux always (life support/utilities). Everything else becomes eligible to shut down.
                    if (sysId == SystemIds.Aux) continue;

                    // Turn off weapons + jump always
                    if (fat is IMyUserControllableGun || fat is IMyLargeTurretBase || fat is IMyJumpDrive)
                    {
                        DisableBlockForControlLoss(fat);

                        continue;
                    }

                    // Also turn off blocks assigned to Weapons or Jump subsystems
                    if (sysId != 0 && _systems.TryGetValue(sysId, out var s))
                    {
                        if (s.Kind == SystemKind.Weapons || s.Kind == SystemKind.Jump)
                        {
                            DisableBlockForControlLoss(fat);

                        }
                    }
                }
            }

            // T+15: AUX-only remains
            if (t >= 15f)
            {
                foreach (var slim in blocks)
                {
                    var fat = slim.FatBlock as IMyCubeBlock;
                    if (fat == null) continue;

                    int sysId = Assignments.Get(fat.EntityId);

                    // Aux stays on
                    if (sysId == SystemIds.Aux) continue;

                    // Everything else off (except power producers you might want to keep for recovery—your choice).
                    // This matches “blocks slowly start shutting down… AUX-only remains.”
                    DisableBlockForControlLoss(fat);

                }
            }
        }

        void ApplyCoreEffects()
        {
            var grid = MyAPIGateway.Entities.GetEntityById(GridId) as IMyCubeGrid;
            if (grid == null) return;

            // Find one control + one propulsion system to apply (players can have multiple; apply each)
            foreach (var sys in _systems.Values)
            {
                // Effective available vs required
                float available = sys.LocalMW + sys.IncomingMW;
                float required = Math.Max(1f, sys.RequiredMW);
                float r = Util.MathUtil.Clamp(available / required, 0f, 1f);

                // If emergency-fed, cap effectiveness
                if (sys.EmergencyFed)
                {
                    if (sys.Kind == SystemKind.Control)
                        r = Math.Min(r, PowerMath.EMERGENCY_CONTROL_R_CAP);

                    if (sys.Kind == SystemKind.Propulsion)
                        r = Math.Min(r, PowerMath.EMERGENCY_THROTTLE_CAP);
                }

            }
        }

        void CheckAndTriggerBlackout()
        {
            // Only trigger when main transitions from having power -> no power
            if (_mainHadPowerPrev && !_mainHasPower)
            {
                // Per spec: blackout durations set; only scaling is consolidation
                _blackoutRemaining = _consolidationActive ? 3f : 1f;

                // No stacking: blackout wins, exit control loss if active
                if (_controlLoss)
                    ExitControlLoss(restoreState: true);
            }
        }

        void RecomputeSubsystemPower()
        {
            var grid = MyAPIGateway.Entities.GetEntityById(GridId) as IMyCubeGrid;
            if (grid == null) return;

            var blocks = new List<IMySlimBlock>();
            grid.GetBlocks(blocks);

            // Reset subsystem values
            foreach (var s in _systems.Values)
            {
                s.LocalMW = 0f;
                s.RequiredMW = 0f;
                s.IncomingMW = 0f;
                s.EmergencyFed = false;
            }

            float mainProducerMW = 0f;
            float auxProducerMW = 0f;
            float emeProducerMW = 0f;

            float auxLoadApprox = 0f; // used for recharge heuristic

            // Track emergency batteries so we can set charge mode for recharge
            var emergencyBatteries = new List<IMyBatteryBlock>(32);

            // Also track “non-reactor input sources” (solar/wind/battery) that are not emergency group.
            bool hasNonReactorInputOutsideEmergency = false;

            // 1) Scan blocks: producers + approximate consumers
            foreach (var slim in blocks)
            {
                var fat = slim.FatBlock as IMyCubeBlock;
                if (fat == null) continue;

                int sysId = Assignments.Get(fat.EntityId);

                // Producer MW
                float prod = PowerMath.GetProducerMW(fat);

                // Count whether we have any non-reactor producers outside emergency
                if (prod > 0f && !(fat is IMyReactor) && sysId != SystemIds.Emergency)
                    hasNonReactorInputOutsideEmergency = true;

                // Emergency source restriction: battery only
                if (sysId == SystemIds.Emergency)
                {
                    if (fat is IMyBatteryBlock bat)
                    {
                        emergencyBatteries.Add(bat);
                        emeProducerMW += (float)bat.CurrentOutput;
                    }
                    else
                    {
                        // reactors/solar/wind assigned to emergency are ignored as sources
                    }
                }
                else if (sysId == SystemIds.Main)
                {
                    mainProducerMW += prod;
                }
                else if (sysId == SystemIds.Aux)
                {
                    auxProducerMW += prod;
                }
                else if (sysId != 0 && _systems.TryGetValue(sysId, out var sProd))
                {
                    sProd.LocalMW += prod;
                }

                // Approximate “required MW” per key consumer type (coarse but usable)
                float req = 0f;
                if (fat is IMyThrust) req = 1.0f;
                else if (fat is IMyGyro) req = 0.5f;
                else if (fat is IMyMotorStator || fat is IMyMotorAdvancedStator || fat is IMyPistonBase) req = 0.25f;
                else if (fat is IMyUserControllableGun || fat is IMyLargeTurretBase) req = 1.5f;
                else if (fat is IMyJumpDrive) req = 2.0f;

                // Aux load approx (for recharge heuristic): utilities/life support-ish blocks are hard to detect generically,
                // so we just sum small loads for blocks assigned to Aux.
                if (sysId == SystemIds.Aux)
                    auxLoadApprox += req;

                if (sysId != 0 && _systems.TryGetValue(sysId, out var sReq))
                    sReq.RequiredMW += req;
            }

            _mainHadPowerPrev = _mainHasPower;
            _mainHasPower = mainProducerMW > 0.01f;

            // 2) Apply subsystem routing deliveries (single-target, lossy)
            foreach (var donor in _systems.Values)
            {
                if (!donor.PowerOn) continue;
                if (donor.RouteTargetId == 0 || donor.RoutePercent <= 0f) continue;
                if (!_systems.TryGetValue(donor.RouteTargetId, out var recv)) continue;

                float donateMW = donor.LocalMW * (donor.RoutePercent / 100f);
                donor.LocalMW -= donateMW;

                float delivered = donateMW * PowerMath.ROUTE_EFF;
                recv.IncomingMW += delivered;
            }

            // 3) Main multi-routing (existing logic)
            float mainAvailable = mainProducerMW;
            float totalAlloc = 0f;
            foreach (var kv in _mainRoutes) totalAlloc += kv.Value;

            if (totalAlloc > 0.01f)
            {
                foreach (var kv in _mainRoutes)
                {
                    if (!_systems.TryGetValue(kv.Key, out var recv)) continue;
                    float pct = kv.Value / totalAlloc;
                    float delivered = mainAvailable * pct * 0.90f; // Main base efficiency 90%
                    recv.IncomingMW += delivered;
                }
            }

            // 4) Determine Harmonic state and apply Harmonic bonus (local only)
            RecomputeMainDraw();
            _harmonicActive = (_mainDrawMW <= 0.0001f);

            if (_harmonicActive)
            {
                float harmonicExtra = HARMONIC_BASE_BONUS;

                // Add burst percent to harmonic bonus if Main is bursting and no main routing
                if (_mainBursting && _mainRoutes.Count == 0)
                    harmonicExtra += Math.Max(0f, _mainBurstPercent - 1f);

                foreach (var s in _systems.Values)
                    s.LocalMW *= (1f + harmonicExtra);
            }

            // 5) Burst affects local only (subsystems)
            foreach (var s in _systems.Values)
            {
                if (s.IsBursting)
                {
                    float over = Math.Max(0f, s.BurstPercent - 1f);
                    s.LocalMW *= (1f + over);
                }
            }

            // 6) Emergency system behavior (FULL SPEC)

            // Emergency “available” MW = battery output * 10% cap * stress-based decay
            // Stress-based decay factor: 1 at 0 stress, down toward 0 as stress rises.
            // (You set stress per system; emergency uses _emeStress.)
            float decay = Util.MathUtil.Clamp(1f - (_emeStress / 120f), 0f, 1f);
            _emergencyAvailableMW = emeProducerMW * PowerMath.EMERGENCY_OUTPUT_CAP * decay;

            // Emergency active when it is actually providing power (battery output exists and available > ~0)
            _emergencyActive = (_emergencyAvailableMW > 0.01f);

            // Emergency cannot power: weapons/jump; only weak propulsion/control (and aux fallback)
            // Emergency feed ONLY when local power is inaccessible (destroyed/offline): interpret as "no local+incoming power".
            var eligible = new List<SubsystemModel>(16);
            foreach (var s in _systems.Values)
            {
                // never feed Jump with emergency
                if (s.Kind == SystemKind.Jump) continue;

                // never feed Weapons with emergency
                if (s.Kind == SystemKind.Weapons) continue;

                // only propulsion/control are “eligible for emergency feed”
                if (s.Kind != SystemKind.Propulsion && s.Kind != SystemKind.Control)
                    continue;

                // “inaccessible” = essentially 0 power available
                float available = s.LocalMW + s.IncomingMW;
                if (available <= 0.001f && s.PowerOn)
                    eligible.Add(s);
            }

            if (_emergencyActive && eligible.Count > 0)
            {
                // no priorities: equal split
                float per = _emergencyAvailableMW / eligible.Count;

                foreach (var s in eligible)
                {
                    s.IncomingMW += per;
                    s.EmergencyFed = true;
                }

                // Emergency stress rises when used
                _emeStress += 2.0f * MyEngineConstants.UPDATE_STEP_SIZE_IN_SECONDS;
            }
            else
            {
                // Emergency stress decays when not actively feeding
                _emeStress -= 2.6f * MyEngineConstants.UPDATE_STEP_SIZE_IN_SECONDS;
            }

            _emeStress = Util.MathUtil.Clamp(_emeStress, 0f, 100f);

            // 7) Emergency recharge rule:
            // Rechargeable from Aux power or non-reactor input sources (minus reactors).
            // Implementation: if Aux producers exist OR non-reactor input exists, set emergency batteries to Recharge when NOT actively feeding.
            bool canRecharge = (auxProducerMW > 0.01f) || hasNonReactorInputOutsideEmergency;

            foreach (var bat in emergencyBatteries)
            {
                if (bat == null) continue;

                // If emergency is actively feeding, keep Auto so it can discharge.
                if (_emergencyActive)
                {
                    bat.ChargeMode = Sandbox.ModAPI.Ingame.ChargeMode.Auto;
                }
                else
                {
                    // If recharge sources exist, recharge; else leave auto.
                    bat.ChargeMode = canRecharge ? Sandbox.ModAPI.Ingame.ChargeMode.Recharge : Sandbox.ModAPI.Ingame.ChargeMode.Auto;
                }
            }
        }


        void RecomputeMainDraw()
        {
            // Main draw = any power pulled from main due to main routing allocations or “consolidation mode” usage.
            // In this spec: any main routing means draw > 0.
            _mainDrawMW = (_mainRoutes.Count > 0) ? 1f : 0f;
        }

        public bool CanEngineerNow(out string reason)
        {
            reason = null;
            var grid = MyAPIGateway.Entities.GetEntityById(GridId) as IMyCubeGrid;
            if (grid == null) return false;

            // Ship immobile requirement
            var vel = grid.Physics?.LinearVelocity.Length() ?? 0f;
            if (vel > 0.5f)
            {
                reason = "Grid moving";
                return false;
            }
            return true;
        }

        public bool IsAnyBurstActiveFor(int systemId)
        {
            if (systemId == SystemIds.Main) return _mainBursting;
            if (_systems.TryGetValue(systemId, out var s)) return s.IsBursting;
            return false;
        }

        // --- Commands ---
        public void ApplyCommand(PowerCmd cmd)
        {
            switch (cmd.Type)
            {
                case CmdType.SetPowerOn:
                    SetPowerOn(cmd.SystemId, cmd.ArgB);
                    break;

                case CmdType.SetBurstSettings:
                    SetBurstSettings(cmd.SystemId, cmd.ArgF1, cmd.ArgF2);
                    break;

                case CmdType.TriggerBurst:
                    TriggerBurst(cmd.SystemId);
                    break;

                case CmdType.SetRouteTarget:
                    SetRouteTarget(cmd.SystemId, cmd.ArgI);
                    break;

                case CmdType.SetRoutePercent:
                    SetRoutePercent(cmd.SystemId, cmd.ArgF1);
                    break;

                case CmdType.ResetRoute:
                    ResetRoute(cmd.SystemId);
                    break;

                case CmdType.ApplyRouting:
                    ApplyRouting();
                    break;

                case CmdType.TriggerSacrifice:
                    TriggerSacrifice(cmd.SystemId);
                    break;

                case CmdType.SetConsolidation:
                    SetConsolidation(cmd.ArgB);
                    break;

                case CmdType.ApplyProfile:
                    ApplyProfile(cmd.ArgI);
                    break;

                case CmdType.AddSubsystem:
                    AddSubsystem((SystemKind)cmd.ArgI, cmd.ArgS);
                    break;

                case CmdType.DeleteSubsystem:
                    DeleteSubsystem(cmd.SystemId);
                    break;

                case CmdType.RenameSubsystem:
                    RenameSubsystem(cmd.SystemId, cmd.ArgS);
                    break;

                case CmdType.SetMainRoutePercent:
                    SetMainRoutePercent(cmd.ArgI, cmd.ArgF1); // ArgI = targetId, ArgF1 = percent
                    break;

                case CmdType.ClearMainRoutes:
                    _mainRoutesDraft.Clear();
                    _mainRoutes.Clear();
                    break;

                case CmdType.AssignBlock:
                    AssignBlock(cmd.ArgEntityId, cmd.SystemId);
                    break;
            }
        }

        // --- Systems management ---
        public int AddSubsystem(SystemKind kind, string name, int forcedId = 0)
        {
            if (kind == SystemKind.Jump)
            {
                // Jump unique
                foreach (var s in _systems.Values)
                    if (s.Kind == SystemKind.Jump)
                        return s.Id;
            }

            int id = (forcedId != 0) ? forcedId : _nextSystemId++;
            if (forcedId != 0 && id >= _nextSystemId) _nextSystemId = id + 1;

            var sys = new SubsystemModel(this, id, kind, string.IsNullOrWhiteSpace(name) ? kind.ToString() : name);
            _systems[id] = sys;
            return id;
        }

        public void DeleteSubsystem(int id)
        {
            if (!_systems.ContainsKey(id)) return;
            var kind = _systems[id].Kind;

            // Can't delete jump unique if you want it always present; otherwise allow.
            _systems.Remove(id);

            // Clear main routes pointing at deleted system
            _mainRoutes.Remove(id);

            // Ensure templates remain
            EnsureDefaultSystems();
        }

        void SetMainRoutePercent(int targetSystemId, float percent)
        {
            percent = Util.MathUtil.Clamp(percent, 0f, 100f);

            // Main can route to multiple subsystems, but never to itself / aux / emergency
            if (targetSystemId == SystemIds.Main || targetSystemId == SystemIds.Aux || targetSystemId == SystemIds.Emergency)
                return;

            // Only route to real subsystems (including Jump)
            if (!_systems.ContainsKey(targetSystemId))
                return;

            if (percent <= 0.0001f)
                _mainRoutesDraft.Remove(targetSystemId);
            else
                _mainRoutesDraft[targetSystemId] = percent;
        }

        float GetMainDraftTotal()
        {
            float total = 0f;
            foreach (var kv in _mainRoutesDraft)
                total += kv.Value;
            return total;
        }

        public void SendSnapshotToSubscribers(PowerSnapshot snap)
        {
            foreach (var sid in _uiSubs)
                Net.Net.SendSnapshotToClient(sid, snap);
        }

        public void RenameSubsystem(int id, string name)
        {
            if (_systems.TryGetValue(id, out var s))
                s.Name = string.IsNullOrWhiteSpace(name) ? s.Name : name;
        }

        public IEnumerable<SubsystemModel> EnumerateSubsystems() => _systems.Values;

        // --- Command implementations ---
        void SetPowerOn(int systemId, bool on)
        {
            if (systemId == SystemIds.Main)
            {
                // main power on/off – you may decide whether that’s allowed; here we allow
                // TODO: apply to assigned main power producers
                return;
            }
            if (systemId == SystemIds.Aux) { return; }
            if (systemId == SystemIds.Emergency) { return; }

            if (_systems.TryGetValue(systemId, out var s))
                s.PowerOn = on;
        }

        void SetBurstSettings(int systemId, float percent, float duration)
        {
            percent = Util.MathUtil.Clamp(percent, 101f, 200f);
            duration = Util.MathUtil.Clamp(duration, 0.5f, 60f);

            if (systemId == SystemIds.Main)
            {
                // main burst defaults you gave: Main default 10 (seconds), but we store duration
                _mainBurstPercent = percent / 100f;
                _mainBurstDuration = duration;
                return;
            }

            if (_systems.TryGetValue(systemId, out var s))
            {
                s.BurstPercent = percent / 100f;
                s.BurstDuration = duration;
            }
        }

        void TriggerBurst(int systemId)
        {
            if (systemId == SystemIds.Main)
            {
                _mainBursting = true;
                _mainBurstRemaining = _mainBurstDuration;
                return;
            }

            if (_systems.TryGetValue(systemId, out var s))
            {
                s.StartBurst();
            }
        }

        void SetRouteTarget(int donorSystemId, int targetSystemId)
        {
            if (donorSystemId == SystemIds.Main)
            {
                // main target set is done by percent per target; ignore here
                return;
            }

            if (_systems.TryGetValue(donorSystemId, out var donor))
            {
                donor.DraftRouteTargetId = targetSystemId; // draft until apply
            }
        }

        void SetRoutePercent(int donorSystemId, float percent)
        {
            percent = Util.MathUtil.Clamp(percent, 0f, 100f);

            if (donorSystemId == SystemIds.Main)
            {
                // For main we treat ArgI as targetId; UI should send SetRoutePercent with SystemId=Main and ArgI=target
                // This skeleton uses cmd.ArgF1 only; if you want main per-target sliders, add cmd.ArgI usage in ApplyCommand.
                return;
            }

            if (_systems.TryGetValue(donorSystemId, out var donor))
            {
                donor.DraftRoutePercent = percent;
            }
        }

        void ResetRoute(int donorSystemId)
        {
            if (_systems.TryGetValue(donorSystemId, out var donor))
            {
                donor.DraftRouteTargetId = 0;
                donor.DraftRoutePercent = 0f;
                donor.RouteTargetId = 0;
                donor.RoutePercent = 0f;
            }
        }

        void ApplyRouting()
        {
            // Apply each subsystem's draft routing
            foreach (var s in _systems.Values)
            {
                // Single-target rule enforced here
                s.RouteTargetId = s.DraftRouteTargetId;
                s.RoutePercent = s.DraftRoutePercent;
            }

            // Main routing is handled via dedicated commands (you’ll add per-target)
        }

        void TriggerSacrifice(int systemId)
        {
            if (_systems.TryGetValue(systemId, out var s))
            {
                // Your spec: “set as emergency setting but still labeled as base group”
                s.IsSacrificed = true;
                s.PowerOn = false;
                // TODO: convert into emergency source contribution logic
            }
        }

        void SetConsolidation(bool on)
        {
            // TODO: implement consolidation logic per spec (routes subsystems to main at 75% efficiency)
            // Also affects blackout duration on main failure.
        }

        void ApplyProfile(int profileId)
        {
            // TODO: implement profiles persisted by id
            // Profile includes routing % and power on/off.
        }

        void AssignBlock(long blockEntityId, int systemId)
        {
            // One block can only belong to one system, enforced here.
            Assignments.Set(blockEntityId, systemId);
        }


        // --- Snapshot ---
        public PowerSnapshot BuildSnapshot(bool debugEnabled)
        {
            var snap = new PowerSnapshot
            {
                GridId = GridId,
                HarmonicActive = _harmonicActive,
                MainDrawMW = _mainDrawMW,
                HarmonicBonus = HARMONIC_BASE_BONUS,
                MainBurstBonus = (_harmonicActive && _mainBursting) ? Math.Max(0f, _mainBurstPercent - 1f) : 0f,
                TotalHarmonicBonus = (_harmonicActive)
                    ? HARMONIC_BASE_BONUS + ((_mainBursting) ? Math.Max(0f, _mainBurstPercent - 1f) : 0f)
                    : 0f,

                BlackoutRemaining = _blackoutRemaining,
                ControlLossActive = _controlLoss,
                ControlLossTime = _controlLossTime,

                DebugEnabled = debugEnabled,

                Systems = new List<SystemSnapshot>(_systems.Count + 3),
                MainRoutes = new List<MainRouteEntry>(_mainRoutes.Count),
                MainAllocatedPercent = 0f
            };

            // Main/Aux/Emergency as “systems”
            snap.Systems.Add(new SystemSnapshot
            {
                SystemId = SystemIds.Main,
                Kind = (byte)SystemKind.Main,
                Name = "Main Power",
                PowerOn = true,
                LocalMW = 0,
                RequiredMW = 0,
                IncomingMW = 0,
                RouteTargetId = 0,
                RoutePercent = 0,
                IsBursting = _mainBursting,
                BurstRemaining = _mainBurstRemaining,
                Stress = debugEnabled ? _mainStress : 0f,
                Flags = 0
            });

            snap.Systems.Add(new SystemSnapshot
            {
                SystemId = SystemIds.Aux,
                Kind = (byte)SystemKind.Aux,
                Name = "Aux Power",
                PowerOn = true,
                LocalMW = 0,
                RequiredMW = 0,
                IncomingMW = 0,
                RouteTargetId = 0,
                RoutePercent = 0,
                IsBursting = false,
                BurstRemaining = 0,
                Stress = debugEnabled ? _auxStress : 0f,
                Flags = 0
            });

            snap.Systems.Add(new SystemSnapshot
            {
                SystemId = SystemIds.Emergency,
                Kind = (byte)SystemKind.Emergency,
                Name = "Emergency Power",
                PowerOn = true,
                LocalMW = 0,
                RequiredMW = 0,
                IncomingMW = 0,
                RouteTargetId = 0,
                RoutePercent = 0,
                IsBursting = false,
                BurstRemaining = 0,
                Stress = debugEnabled ? _emeStress : 0f,
                Flags = 0
            });

            foreach (var s in _systems.Values)
                snap.Systems.Add(s.BuildSnapshot(debugEnabled));

            float total = 0f;
            foreach (var kv in _mainRoutes)
            {
                snap.MainRoutes.Add(new MainRouteEntry { TargetSystemId = kv.Key, Percent = kv.Value });
                total += kv.Value;
            }
            snap.MainAllocatedPercent = total;

            return snap;
        }
    }
}
