using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using System;
using System.Collections.Generic;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;

namespace Gideon.PowerOverhaul
{
    public static class TerminalControls
    {
        static bool _init;

        // Persistent per-block storage key for subsystem assignment
        static readonly Guid SUBSYSTEM_STORAGE_KEY = new Guid("b9f1d2c8-4c21-4c0e-8b7f-7d5d0b2b6f20");

        public static void Init()
        {
            if (_init) return;
            _init = true;

            // Button on ship controllers
            AddOpenButton<IMyShipController>();

            // Combobox on various block types
            AddSubsystemSelector<IMyThrust>();
            AddSubsystemSelector<IMyGyro>();
            AddSubsystemSelector<IMyMotorStator>();
            AddSubsystemSelector<IMyMotorAdvancedStator>();
            AddSubsystemSelector<IMyPistonBase>();
            AddSubsystemSelector<IMyUserControllableGun>();
            AddSubsystemSelector<IMyLargeTurretBase>();
            AddSubsystemSelector<IMyJumpDrive>();
            AddSubsystemSelector<IMyBatteryBlock>();
            AddSubsystemSelector<IMyReactor>();
        }

        public static void Close()
        {
            // Nothing required; terminal controls persist once added for the session.
        }

        static void AddOpenButton<T>() where T : class, IMyTerminalBlock
        {
            var btn = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, T>("PWR_OPEN_UI");
            btn.Title = MyStringId.GetOrCompute("Open Power Routing");
            btn.Tooltip = MyStringId.GetOrCompute("Opens the Power Routing screen (Ctrl+F).");

            btn.Action = (b) =>
            {
                var grid = b?.CubeGrid;
                if (grid == null) return;
                UI.PowerRoutingScreen.Open(grid.EntityId);
            };

            MyAPIGateway.TerminalControls.AddControl<T>(btn);
        }

        static void AddSubsystemSelector<T>() where T : class, IMyTerminalBlock
        {
            var combo = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCombobox, T>("PWR_SUBSYSTEM_ASSIGN");
            combo.Title = MyStringId.GetOrCompute("Power System");
            combo.Tooltip = MyStringId.GetOrCompute("Assign this block to a power system/subsystem (Engineering changes).");

            combo.Visible = (b) => b?.CubeGrid != null;
            combo.Enabled = (b) => true;

            // IMPORTANT: matches delegate Action<List<MyTerminalControlComboBoxItem>>
            combo.ComboBoxContent = (items) => FillSubsystemItems(items);

            // ✅ Fixed Getter: reads persistent selection from ModStorage
            combo.Getter = (b) => GetStoredSubsystem(b);

            // ✅ Fixed Setter: writes ModStorage so UI updates immediately, then sends server command
            combo.Setter = (b, key) =>
            {
                if (b?.CubeGrid == null) return;

                // Store locally for instant UI correctness
                SetStoredSubsystem(b, key);

                // Send assignment request to server (server remains authoritative/validates)
                Net.Net.SendCmdToServer(new Net.PowerCmd
                {
                    GridId = b.CubeGrid.EntityId,
                    Type = Net.CmdType.AssignBlock,
                    SystemId = (int)key,
                    ArgEntityId = b.EntityId
                });
            };

            MyAPIGateway.TerminalControls.AddControl<T>(combo);
        }

        static void FillSubsystemItems(List<MyTerminalControlComboBoxItem> items)
        {
            items.Clear();

            // NOTE: This uses VRage.Utils.MyTerminalControlComboBoxItem with MyStringId values
            items.Add(new MyTerminalControlComboBoxItem { Key = 0L, Value = MyStringId.GetOrCompute("None") });
            items.Add(new MyTerminalControlComboBoxItem { Key = (long)Model.SystemIds.Main, Value = MyStringId.GetOrCompute("Main Power") });
            items.Add(new MyTerminalControlComboBoxItem { Key = (long)Model.SystemIds.Aux, Value = MyStringId.GetOrCompute("Aux Power") });
            items.Add(new MyTerminalControlComboBoxItem { Key = (long)Model.SystemIds.Emergency, Value = MyStringId.GetOrCompute("Emergency Power") });

            // Later: you can add dynamic custom subsystems (from client snapshot cache) here.
        }

        static long GetStoredSubsystem(IMyTerminalBlock b)
        {
            try
            {
                if (b?.Storage == null) return 0L;

                string raw;
                if (!b.Storage.TryGetValue(SUBSYSTEM_STORAGE_KEY, out raw)) return 0L;

                long val;
                if (long.TryParse(raw, out val)) return val;
            }
            catch { }
            return 0L;
        }

        static void SetStoredSubsystem(IMyTerminalBlock b, long value)
        {
            try
            {
                if (b == null) return;

                if (b.Storage == null)
                    b.Storage = new MyModStorageComponent();

                b.Storage[SUBSYSTEM_STORAGE_KEY] = value.ToString();
            }
            catch { }
        }
    }
}
