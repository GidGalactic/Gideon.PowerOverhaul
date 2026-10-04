using Sandbox.ModAPI;
using Sandbox.Game.World;
using VRage.Game.Components;
using System;
using System.Collections.Generic;

namespace Gideon.PowerOverhaul
{
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation | MyUpdateOrder.AfterSimulation)]
    public class PowerOverhaulSession : MySessionComponentBase
    {
        public static PowerOverhaulSession Instance;

        // One network channel ID for all messages
        public const ushort NET_ID = 49211;

        // GridId -> model
        internal readonly Dictionary<long, Model.GridPowerModel> Grids =
            new Dictionary<long, Model.GridPowerModel>(64);

        // Client-side cache of last snapshot for UI
        internal readonly Dictionary<long, Net.PowerSnapshot> ClientSnapshots =
            new Dictionary<long, Net.PowerSnapshot>(64);

        public bool DebugModeEnabledServer { get; private set; } = false;

        bool _init;
        int _tick;

        public override void LoadData()
        {
            Instance = this;
        }

        public override void BeforeStart()
        {
            if (_init) return;
            _init = true;

            MyAPIGateway.Utilities.ShowNotification("Power Overhaul DLL loaded", 3000, "Green");

            // Networking
            MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(NET_ID, OnNetMessage);

            // Terminal controls (block combobox assignment + “Open Power Routing” button)
            TerminalControls.Init();

            // Load persisted settings/state (server)
            if (MyAPIGateway.Multiplayer.IsServer)
            {
                Persistence.LoadServerSettings(out var dbg);
                DebugModeEnabledServer = dbg;
            }
        }

        protected override void UnloadData()
        {
            try
            {
                MyAPIGateway.Multiplayer?.UnregisterSecureMessageHandler(NET_ID, OnNetMessage);
            }
            catch { }

            TerminalControls.Close();
            Instance = null;
        }

        public override void UpdateBeforeSimulation()
        {
            if (!_init) return;
            _tick++;

            // Client hotkey polling (Ctrl+F)
            if (!MyAPIGateway.Multiplayer.IsServer)
            {
                if (InputHotkey.ConsumeOpenPressed())
                {
                    // If you have an OpenOrFocus helper use it; otherwise call your open method.
                    UI.PowerRoutingScreen.OpenOrFocus();
                }
            }

            // Server: advance models
            if (MyAPIGateway.Multiplayer.IsServer)
            {
                foreach (var kv in Grids)
                    kv.Value.TickServer();

                // Periodic cleanup (every 10 minutes at 60 ticks/sec)
                if ((_tick % (60 * 10)) == 0)
                    CleanupMissingGrids();
            }
        }

        public override void UpdateAfterSimulation()
        {
            // Server: send snapshots to clients who requested them
            if (!MyAPIGateway.Multiplayer.IsServer) return;

            // Send snapshots at ~6Hz for subscribed grids
            if ((_tick % 10) != 0) return;

            foreach (var kv in Grids)
            {
                var model = kv.Value;
                if (!model.HasAnyUiSubscriber()) continue;

                var snap = model.BuildSnapshot(DebugModeEnabledServer);
                model.SendSnapshotToSubscribers(snap);
            }
        }

        void CleanupMissingGrids()
        {
            var remove = new List<long>();
            foreach (var kv in Grids)
            {
                if (MyAPIGateway.Entities.GetEntityById(kv.Key) == null)
                    remove.Add(kv.Key);
            }
            foreach (var id in remove)
                Grids.Remove(id);
        }

        internal Model.GridPowerModel GetOrCreateGridModel(long gridId)
        {
            if (!Grids.TryGetValue(gridId, out var model))
            {
                model = new Model.GridPowerModel(gridId);
                Grids[gridId] = model;

                // Load grid-specific persisted state (routing, profiles, subsystem assignments)
                Persistence.TryLoadGridState(gridId, model);
            }
            return model;
        }

        void OnNetMessage(ushort channel, byte[] data, ulong senderSteamId, bool fromServer)
        {
            // Server receives commands from clients; clients receive snapshots from server.
            if (MyAPIGateway.Multiplayer.IsServer)
            {
                if (!Gideon.PowerOverhaul.Net.Net.TryParseCommand(data, out var cmd))
                    return;

                HandleServerCommand(senderSteamId, cmd);
            }
            else
            {
                if (!Gideon.PowerOverhaul.Net.Net.TryParseSnapshot(data, out var snap))
                    return;

                ClientSnapshots[snap.GridId] = snap;

                // If UI is open for this grid, notify it
                UI.PowerRoutingScreen.NotifySnapshotArrived(snap);
            }
        }

        void HandleServerCommand(ulong senderSteamId, Net.PowerCmd cmd)
        {
            var model = GetOrCreateGridModel(cmd.GridId);

            // Subscribe/unsubscribe for snapshots
            if (cmd.Type == Net.CmdType.RequestSnapshot)
            {
                model.AddUiSubscriber(senderSteamId);

                // Immediate push
                var snap = model.BuildSnapshot(DebugModeEnabledServer);
                Gideon.PowerOverhaul.Net.Net.SendSnapshotToClient(senderSteamId, snap);
                return;
            }

            if (cmd.Type == Net.CmdType.Unsubscribe)
            {
                model.RemoveUiSubscriber(senderSteamId);
                return;
            }

            // Engineering-mode checks for assignment operations
            if (cmd.Type == Net.CmdType.AssignBlock ||
                cmd.Type == Net.CmdType.AddSubsystem ||
                cmd.Type == Net.CmdType.DeleteSubsystem ||
                cmd.Type == Net.CmdType.RenameSubsystem)
            {
                if (!model.CanEngineerNow(out var reason))
                    return; // silently reject per your “no feedback” preference
            }

            // Routing changes disallowed during burst (server-enforced)
            if (cmd.Type == Net.CmdType.SetRouteTarget ||
                cmd.Type == Net.CmdType.SetRoutePercent ||
                cmd.Type == Net.CmdType.ResetRoute ||
                cmd.Type == Net.CmdType.ApplyRouting)
            {
                if (model.IsAnyBurstActiveFor(cmd.SystemId))
                    return;
            }

            model.ApplyCommand(cmd);

            // Persist grid state after any meaningful change
            Persistence.SaveGridState(model);
        }
    }
}
