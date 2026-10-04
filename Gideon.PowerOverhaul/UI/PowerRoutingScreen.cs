 tousing Gideon.PowerOverhaul.Model;
using Gideon.PowerOverhaul.Net;
using Sandbox.Game.Screens;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using VRage.Game.ModAPI;
using VRageMath;
using VRage.Utils;

namespace Gideon.PowerOverhaul.UI
{
    using NetApi = Gideon.PowerOverhaul.Net.Net;

    public class PowerRoutingScreen : MyGuiScreenBase
    {
        static PowerRoutingScreen _open;

        readonly long _gridId;

        MyGuiControlListbox _mainRouteList;
        MyGuiControlLabel _lblMainTotal;
        MyGuiControlButton _btnMainClear;

        MyGuiControlButton _btnAddSubsystem;
        MyGuiControlListbox _listSystems;

        MyGuiControlCheckbox _chkPowerOn;
        MyGuiControlButton _btnBurst;
        MyGuiControlSlider _sldBurstPercent;
        MyGuiControlSlider _sldBurstDuration;

        MyGuiControlCombobox _cmbRouteTarget;
        MyGuiControlSlider _sldRoutePercent;
        MyGuiControlButton _btnResetRoute;
        MyGuiControlButton _btnApply;

        MyGuiControlButton _btnSacrifice;

        MyGuiControlCheckbox _chkConsolidation;

        MyGuiControlLabel _lblStatus;
        MyGuiControlLabel _lblHarmonic;

        PowerSnapshot _snapshot;
        int _selectedSystemId;

        readonly List<MyGuiControlBase> _dynMainControls = new List<MyGuiControlBase>(64);

        // Polling state (avoids ItemSelected delegate mismatch)
        long _lastRouteTargetKey = long.MinValue;

        // --- Reflection helpers ------------------------------------------------
        static object TryCreateInstance(Type t, params object[] args)
        {
            try { return Activator.CreateInstance(t, args); }
            catch
            {
                try { return Activator.CreateInstance(t); }
                catch { return null; }
            }
        }

        static void SetTextIfPossible(object ctrl, string text)
        {
            if (ctrl == null) return;
            var t = ctrl.GetType();

            var textProp = t.GetProperty("Text") ?? t.GetProperty("LabelText") ?? t.GetProperty("Caption");
            if (textProp != null && textProp.CanWrite)
            {
                var pType = textProp.PropertyType;
                if (pType == typeof(string)) textProp.SetValue(ctrl, text);
                else if (pType == typeof(StringBuilder)) textProp.SetValue(ctrl, new StringBuilder(text ?? ""));
                else if (pType == typeof(MyStringId)) textProp.SetValue(ctrl, MyStringId.GetOrCompute(text ?? ""));
                return;
            }

            var textField = t.GetField("Text", BindingFlags.Public | BindingFlags.Instance) ??
                            t.GetField("_text", BindingFlags.NonPublic | BindingFlags.Instance);
            if (textField != null)
            {
                var fType = textField.FieldType;
                if (fType == typeof(string)) textField.SetValue(ctrl, text);
                else if (fType == typeof(StringBuilder)) textField.SetValue(ctrl, new StringBuilder(text ?? ""));
                else if (fType == typeof(MyStringId)) textField.SetValue(ctrl, MyStringId.GetOrCompute(text ?? ""));
            }
        }

        static void SetToolTipIfPossible(object ctrl, string tip)
        {
            if (ctrl == null) return;
            var t = ctrl.GetType();
            var prop = t.GetProperty("ToolTip") ?? t.GetProperty("ToolTipText") ?? t.GetProperty("ToolTipString");
            if (prop != null && prop.CanWrite)
            {
                var pType = prop.PropertyType;
                if (pType == typeof(string)) prop.SetValue(ctrl, tip);
                else if (pType == typeof(StringBuilder)) prop.SetValue(ctrl, new StringBuilder(tip ?? ""));
                else if (pType == typeof(MyStringId)) prop.SetValue(ctrl, MyStringId.GetOrCompute(tip ?? ""));
                return;
            }

            var field = t.GetField("ToolTip", BindingFlags.Public | BindingFlags.Instance) ??
                        t.GetField("_toolTip", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                var fType = field.FieldType;
                if (fType == typeof(string)) field.SetValue(ctrl, tip);
                else if (fType == typeof(StringBuilder)) field.SetValue(ctrl, new StringBuilder(tip ?? ""));
                else if (fType == typeof(MyStringId)) field.SetValue(ctrl, MyStringId.GetOrCompute(tip ?? ""));
            }
        }

        static T CreateControl<T>(string text = null, string tooltip = null, params object[] ctorArgs) where T : class
        {
            var ctrlObj = TryCreateInstance(typeof(T), ctorArgs) as T;
            if (ctrlObj != null)
            {
                SetTextIfPossible(ctrlObj, text);
                SetToolTipIfPossible(ctrlObj, tooltip);
            }
            return ctrlObj;
        }

        static object CreateListBoxItem(string text, object userData)
        {
            var lbType = typeof(MyGuiControlListbox);
            var itemType = lbType.GetNestedType("Item", BindingFlags.Public | BindingFlags.NonPublic);
            if (itemType == null) return null;

            object instance = null;
            instance = TryCreateInstance(itemType, new StringBuilder(text ?? ""), userData);
            if (instance != null) return instance;
            instance = TryCreateInstance(itemType, text, userData);
            if (instance != null) return instance;

            instance = TryCreateInstance(itemType);
            if (instance != null)
            {
                var propText = itemType.GetProperty("Text") ?? itemType.GetProperty("Label");
                if (propText != null && propText.CanWrite) propText.SetValue(instance, text);
                var propUser = itemType.GetProperty("UserData") ?? itemType.GetProperty("userData");
                if (propUser != null && propUser.CanWrite) propUser.SetValue(instance, userData);
            }
            return instance;
        }

        static void ComboboxAddItem(MyGuiControlCombobox cmb, long key, string text)
        {
            if (cmb == null) return;
            var t = cmb.GetType();

            MethodInfo addMethod = null;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "AddItem") continue;
                var ps = m.GetParameters();
                if (ps.Length == 2) { addMethod = m; break; }
            }
            if (addMethod == null) return;

            var p1 = addMethod.GetParameters()[1].ParameterType;
            object argText;
            if (p1 == typeof(string)) argText = text;
            else if (p1 == typeof(StringBuilder)) argText = new StringBuilder(text ?? "");
            else if (p1 == typeof(MyStringId)) argText = MyStringId.GetOrCompute(text ?? "");
            else argText = text;

            addMethod.Invoke(cmb, new object[] { key, argText });
        }

        static void SetSliderRange(MyGuiControlSlider slider, float min, float max)
        {
            if (slider == null) return;
            var t = slider.GetType();

            var minProp = t.GetProperty("MinValue") ?? t.GetProperty("Minimum") ?? t.GetProperty("Min");
            var maxProp = t.GetProperty("MaxValue") ?? t.GetProperty("Maximum") ?? t.GetProperty("Max");

            if (minProp != null && maxProp != null && minProp.CanWrite && maxProp.CanWrite)
            {
                try
                {
                    if (minProp.PropertyType == typeof(float)) minProp.SetValue(slider, min);
                    else if (minProp.PropertyType == typeof(double)) minProp.SetValue(slider, (double)min);

                    if (maxProp.PropertyType == typeof(float)) maxProp.SetValue(slider, max);
                    else if (maxProp.PropertyType == typeof(double)) maxProp.SetValue(slider, (double)max);
                    return;
                }
                catch { }
            }
        }

        static long ComboboxGetSelectedKey(MyGuiControlCombobox cmb)
        {
            if (cmb == null) return 0L;

            try
            {
                var mi = cmb.GetType().GetMethod("GetSelectedKey", BindingFlags.Public | BindingFlags.Instance);
                if (mi != null)
                {
                    object val = mi.Invoke(cmb, null);
                    if (val == null) return 0L;

                    if (val is long) return (long)val;

                    // If the API returns long? boxed as object, cast it (no pattern matching)
                    try
                    {
                        var ln = (long?)val;
                        return ln.GetValueOrDefault();
                    }
                    catch { }
                }
            }
            catch { }

            return 0L;
        }

        // ----------------------------------------------------------------------

        public static void Open(long gridId)
        {
            if (_open != null)
            {
                _open.CloseScreen();
                _open = null;
                return;
            }

            _open = new PowerRoutingScreen(gridId);
            MyGuiSandbox.AddScreen(_open);
        }

        public static void OpenOrFocus()
        {
            long gridId = 0;
            try
            {
                var ent = MyAPIGateway.Session?.Player?.Controller?.ControlledEntity?.Entity;
                if (ent is IMyCubeBlock cb && cb.CubeGrid != null)
                    gridId = cb.CubeGrid.EntityId;
                else if (ent is IMyCubeGrid cg)
                    gridId = cg.EntityId;
            }
            catch { }

            if (gridId != 0)
                Open(gridId);
        }

        public static void NotifySnapshotArrived(PowerSnapshot snap)
        {
            if (_open == null) return;
            if (_open._gridId != snap.GridId) return;
            _open._snapshot = snap;
            _open.RefreshFromSnapshot();
        }

        PowerRoutingScreen(long gridId) : base(
            position: new Vector2(0.5f, 0.5f),
            size: new Vector2(0.90f, 0.82f),
            backgroundColor: null,
            isTopMostScreen: false)
        {
            _gridId = gridId;
            EnabledBackgroundFade = true;
            CloseButtonEnabled = true;
            CanHideOthers = true;
        }

        public override string GetFriendlyName() => "PowerRoutingScreen";

        public override void LoadContent()
        {
            base.LoadContent();
            NetApi.SendCmdToServer(new PowerCmd { GridId = _gridId, Type = CmdType.RequestSnapshot });
        }

        protected override void OnClosed()
        {
            base.OnClosed();
            _open = null;
            NetApi.SendCmdToServer(new PowerCmd { GridId = _gridId, Type = CmdType.Unsubscribe });
        }

        public override bool Update(bool hasFocus)
        {
            bool result = base.Update(hasFocus);

            // Poll route target selection changes (no delegate mismatch possible)
            if (_cmbRouteTarget != null && _selectedSystemId != 0)
            {
                var key = ComboboxGetSelectedKey(_cmbRouteTarget);
                if (key != _lastRouteTargetKey)
                {
                    _lastRouteTargetKey = key;
                    Send(CmdType.SetRouteTarget, _selectedSystemId, argI: (int)key);
                }
            }

            return result;
        }


        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);

            var title = CreateControl<MyGuiControlLabel>("POWER ROUTING", null, new Vector2(0f, -0.39f));
            Controls.Add(title);

            float top = -0.32f;

            // LEFT
            _btnAddSubsystem = CreateControl<MyGuiControlButton>("+ Add Subsystem", null, new Vector2(-0.40f, top));
            if (_btnAddSubsystem != null)
                _btnAddSubsystem.ButtonClicked += (b) => OnAddSubsystem();
            Controls.Add(_btnAddSubsystem);

            _listSystems = CreateControl<MyGuiControlListbox>(null, null, new Vector2(-0.40f, 0.04f));
            if (_listSystems != null)
            {
                _listSystems.Size = new Vector2(0.25f, 0.64f);
                _listSystems.ItemsSelected += OnSystemSelected;
            }
            Controls.Add(_listSystems);

            // MIDDLE
            float midX = -0.05f;
            float y = top;

            Controls.Add(CreateControl<MyGuiControlLabel>("Power:", null, new Vector2(midX - 0.14f, y)));

            _chkPowerOn = CreateControl<MyGuiControlCheckbox>(null, "Power On/Off", new Vector2(midX, y));
            if (_chkPowerOn != null)
                _chkPowerOn.IsCheckedChanged += (b) => Send(CmdType.SetPowerOn, _selectedSystemId, argB: b.IsChecked);
            Controls.Add(_chkPowerOn);
            y += 0.06f;

            _btnBurst = CreateControl<MyGuiControlButton>("Power Burst", null, new Vector2(midX, y));
            if (_btnBurst != null)
                _btnBurst.ButtonClicked += (b) => OnBurst();
            Controls.Add(_btnBurst);
            y += 0.06f;

            Controls.Add(CreateControl<MyGuiControlLabel>("Burst %:", null, new Vector2(midX - 0.14f, y)));
            _sldBurstPercent = CreateControl<MyGuiControlSlider>(null, null, new Vector2(midX + 0.08f, y));
            if (_sldBurstPercent != null) SetSliderRange(_sldBurstPercent, 101f, 200f);
            Controls.Add(_sldBurstPercent);
            y += 0.05f;

            Controls.Add(CreateControl<MyGuiControlLabel>("Burst Time:", null, new Vector2(midX - 0.14f, y)));
            _sldBurstDuration = CreateControl<MyGuiControlSlider>(null, null, new Vector2(midX + 0.08f, y));
            if (_sldBurstDuration != null) SetSliderRange(_sldBurstDuration, 0.5f, 60f);
            Controls.Add(_sldBurstDuration);
            y += 0.07f;

            Controls.Add(CreateControl<MyGuiControlLabel>("Route To:", null, new Vector2(midX - 0.14f, y)));
            _cmbRouteTarget = CreateControl<MyGuiControlCombobox>(null, null, new Vector2(midX + 0.08f, y));
            if (_cmbRouteTarget != null) _cmbRouteTarget.Size = new Vector2(0.28f, 0.04f);
            Controls.Add(_cmbRouteTarget);
            y += 0.06f;

            Controls.Add(CreateControl<MyGuiControlLabel>("Route %:", null, new Vector2(midX - 0.14f, y)));
            _sldRoutePercent = CreateControl<MyGuiControlSlider>(null, null, new Vector2(midX + 0.08f, y));
            if (_sldRoutePercent != null) SetSliderRange(_sldRoutePercent, 0f, 100f);
            Controls.Add(_sldRoutePercent);
            y += 0.06f;

            _btnResetRoute = CreateControl<MyGuiControlButton>("Reset Route", null, new Vector2(midX - 0.10f, y));
            if (_btnResetRoute != null)
                _btnResetRoute.ButtonClicked += (b) => Send(CmdType.ResetRoute, _selectedSystemId);
            Controls.Add(_btnResetRoute);

            _btnApply = CreateControl<MyGuiControlButton>("Apply", null, new Vector2(midX + 0.12f, y));
            if (_btnApply != null)
                _btnApply.ButtonClicked += (b) => OnApply();
            Controls.Add(_btnApply);
            y += 0.07f;

            _btnSacrifice = CreateControl<MyGuiControlButton>("Emergency Sacrifice", null, new Vector2(midX, y));
            if (_btnSacrifice != null)
                _btnSacrifice.ButtonClicked += (b) => Send(CmdType.TriggerSacrifice, _selectedSystemId);
            Controls.Add(_btnSacrifice);
            y += 0.07f;

            Controls.Add(CreateControl<MyGuiControlLabel>("Consolidation:", null, new Vector2(midX - 0.14f, y)));
            _chkConsolidation = CreateControl<MyGuiControlCheckbox>(null, null, new Vector2(midX, y));
            if (_chkConsolidation != null)
                _chkConsolidation.IsCheckedChanged += (b) => Send(CmdType.SetConsolidation, SystemIds.Main, argB: b.IsChecked);
            Controls.Add(_chkConsolidation);
            y += 0.07f;

            Controls.Add(CreateControl<MyGuiControlLabel>("Main Distribution:", null, new Vector2(midX - 0.14f, y)));
            y += 0.05f;

            _mainRouteList = CreateControl<MyGuiControlListbox>(null, null, new Vector2(midX + 0.02f, y + 0.12f));
            if (_mainRouteList != null) _mainRouteList.Size = new Vector2(0.36f, 0.24f);
            Controls.Add(_mainRouteList);

            _lblMainTotal = CreateControl<MyGuiControlLabel>("Total: 0% / <100%", null, new Vector2(midX - 0.14f, y + 0.26f));
            Controls.Add(_lblMainTotal);

            _btnMainClear = CreateControl<MyGuiControlButton>("Clear Main", null, new Vector2(midX + 0.12f, y + 0.26f));
            if (_btnMainClear != null)
                _btnMainClear.ButtonClicked += (b) => Send(CmdType.ClearMainRoutes, SystemIds.Main);
            Controls.Add(_btnMainClear);

            // RIGHT
            _lblHarmonic = CreateControl<MyGuiControlLabel>("HLS: --", null, new Vector2(0.38f, top));
            Controls.Add(_lblHarmonic);

            _lblStatus = CreateControl<MyGuiControlLabel>("Select a system.", null, new Vector2(0.38f, -0.05f));
            Controls.Add(_lblStatus);

            RefreshFromSnapshot();
        }

        void PopulateMainRoutingUI()
        {
            _mainRouteList.Items.Clear();
            RemoveDynamicMainControls();

            if (_snapshot.Systems == null) return;

            float startY = -0.02f;
            float xLabel = -0.15f;
            float xSlider = 0.05f;

            int shown = 0;
            foreach (var sys in _snapshot.Systems)
            {
                if (sys.SystemId == SystemIds.Main || sys.SystemId == SystemIds.Aux || sys.SystemId == SystemIds.Emergency)
                    continue;

                if (shown++ >= 12) break;

                float rowY = startY + (shown * 0.04f);

                var lbl = CreateControl<MyGuiControlLabel>(sys.Name ?? "", null, new Vector2(xLabel, rowY));
                _dynMainControls.Add(lbl);
                Controls.Add(lbl);

                float currentPct = 0f;
                if (_snapshot.MainRoutes != null)
                {
                    foreach (var e in _snapshot.MainRoutes)
                        if (e.TargetSystemId == sys.SystemId) { currentPct = e.Percent; break; }
                }

                var sld = CreateControl<MyGuiControlSlider>(null, null, new Vector2(xSlider, rowY));
                if (sld != null)
                {
                    sld.Value = currentPct;

                    int targetId = sys.SystemId;
                    sld.ValueChanged += (sl) =>
                    {
                        Send(CmdType.SetMainRoutePercent, SystemIds.Main, argI: targetId, argF1: sl.Value);
                    };
                }

                _dynMainControls.Add(sld);
                Controls.Add(sld);
            }

            SetTextIfPossible(_lblMainTotal, $"Total: {_snapshot.MainAllocatedPercent:0}% / <100%");
        }

        void RemoveDynamicMainControls()
        {
            for (int i = 0; i < _dynMainControls.Count; i++)
                Controls.Remove(_dynMainControls[i]);
            _dynMainControls.Clear();
        }

        void OnAddSubsystem()
        {
            Send(CmdType.AddSubsystem, systemId: 0, argI: (int)SystemKind.Custom, argS: "Custom Subsystem");
        }

        void OnSystemSelected(MyGuiControlListbox list)
        {
            if (list.SelectedItems.Count == 0) return;
            var item = list.SelectedItems[0];
            _selectedSystemId = (int)item.UserData;

            // Reset route polling so we don't instantly send a stale command
            _lastRouteTargetKey = long.MinValue;

            RefreshFromSnapshot();
        }

        void RefreshFromSnapshot()
        {
            if (_snapshot.Systems == null) return;

            _listSystems.Items.Clear();
            foreach (var s in _snapshot.Systems)
            {
                var itemObj = CreateListBoxItem(s.Name ?? "", s.SystemId);
                if (itemObj != null)
                {
                    var addMethod = _listSystems.Items.GetType().GetMethod("Add");
                    if (addMethod != null)
                        addMethod.Invoke(_listSystems.Items, new[] { itemObj });
                }
            }

            SetTextIfPossible(_lblHarmonic, _snapshot.HarmonicActive
                ? $"HLS: ACTIVE  Bonus: +{(_snapshot.TotalHarmonicBonus * 100f):0}%"
                : "HLS: DISENGAGED");

            var sel = FindSystem(_selectedSystemId);
            if (sel.SystemId == 0)
            {
                SetTextIfPossible(_lblStatus, "Select a system.");
                return;
            }

            _chkPowerOn.IsChecked = sel.PowerOn;

            bool isAux = sel.SystemId == SystemIds.Aux;
            bool isEme = sel.SystemId == SystemIds.Emergency;
            bool isMain = sel.SystemId == SystemIds.Main;

            _mainRouteList.Visible = isMain;
            _lblMainTotal.Visible = isMain;
            _btnMainClear.Visible = isMain;

            _mainRouteList.Enabled = isMain && !sel.IsBursting;
            _btnMainClear.Enabled = isMain && !sel.IsBursting;

            if (isMain)
                PopulateMainRoutingUI();

            _btnBurst.Enabled = !(isAux || isEme);
            _sldBurstPercent.Enabled = !(isAux || isEme);
            _sldBurstDuration.Enabled = !(isAux || isEme);

            bool burstActive = sel.IsBursting;
            _cmbRouteTarget.Enabled = !burstActive && !isAux && !isEme && !isMain;
            _sldRoutePercent.Enabled = !burstActive && !isAux && !isEme && !isMain;
            _btnResetRoute.Enabled = !burstActive && !isAux && !isEme && !isMain;
            _btnApply.Enabled = !burstActive;

            _btnSacrifice.Enabled = _snapshot.ControlLossActive && !(isMain || isAux || isEme);

            _chkConsolidation.Enabled = isMain;
            _chkConsolidation.Visible = isMain;

            _sldRoutePercent.Value = sel.RoutePercent;

            _cmbRouteTarget.ClearItems();
            ComboboxAddItem(_cmbRouteTarget, 0L, "None");
            foreach (var s in _snapshot.Systems)
            {
                if (s.SystemId == sel.SystemId) continue;
                if (s.SystemId == SystemIds.Main || s.SystemId == SystemIds.Aux || s.SystemId == SystemIds.Emergency) continue;
                ComboboxAddItem(_cmbRouteTarget, (long)s.SystemId, s.Name ?? "");
            }
            _cmbRouteTarget.SelectItemByKey((long)sel.RouteTargetId);

            // IMPORTANT: sync polling key to the selected value so Update() doesn't spam-send
            _lastRouteTargetKey = (long)sel.RouteTargetId;

            var stressText = (_snapshot.DebugEnabled) ? $"Stress: {sel.Stress:0.0}\n" : "";
            SetTextIfPossible(_lblStatus,
                $"System: {sel.Name}\n" +
                $"Local MW: {sel.LocalMW:0.0}\n" +
                $"Incoming MW: {sel.IncomingMW:0.0}\n" +
                $"Required MW: {sel.RequiredMW:0.0}\n" +
                $"Route: → {sel.RouteTargetId} @ {sel.RoutePercent:0}%\n" +
                $"Bursting: {sel.IsBursting} ({sel.BurstRemaining:0.0}s)\n" +
                stressText);
        }

        SystemSnapshot FindSystem(int id)
        {
            if (_snapshot.Systems == null) return default;
            foreach (var s in _snapshot.Systems)
                if (s.SystemId == id) return s;
            return default;
        }

        void OnBurst()
        {
            Send(CmdType.SetBurstSettings, _selectedSystemId, argF1: _sldBurstPercent.Value, argF2: _sldBurstDuration.Value);
            Send(CmdType.TriggerBurst, _selectedSystemId);
        }

        void OnApply()
        {
            Send(CmdType.SetRoutePercent, _selectedSystemId, argF1: _sldRoutePercent.Value);
            Send(CmdType.ApplyRouting, _selectedSystemId);
        }

        void Send(CmdType type, int systemId, int argI = 0, float argF1 = 0f, float argF2 = 0f, bool argB = false, long argEntityId = 0, string argS = null)
        {
            NetApi.SendCmdToServer(new PowerCmd
            {
                GridId = _gridId,
                Type = type,
                SystemId = systemId,
                ArgI = argI,
                ArgF1 = argF1,
                ArgF2 = argF2,
                ArgB = argB,
                ArgEntityId = argEntityId,
                ArgS = argS
            });
        }
    }
}
