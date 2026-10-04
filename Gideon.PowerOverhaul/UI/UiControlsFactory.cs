using Sandbox.Graphics.GUI;
using System;
using System.Reflection;

namespace Gideon.PowerOverhaul.UI
{
    public static class UIControlsFactory
    {
        public static void SetToolTipSafe(MyGuiControlBase control, string tip)
        {
            if (control == null) return;
            if (string.IsNullOrEmpty(tip)) return;

            // 1) If Tooltips exists, add to it
            try
            {
                var existing = control.Tooltips; // read-only getter is fine
                if (existing != null)
                {
                    existing.AddToolTip(tip);
                    return;
                }
            }
            catch { }

            // 2) Try SetToolTip(string)
            try
            {
                // Many SE builds have SetToolTip(string)
                var m = control.GetType().GetMethod("SetToolTip",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(string) },
                    null);

                if (m != null)
                {
                    m.Invoke(control, new object[] { tip });
                    return;
                }
            }
            catch { }

            // 3) Try SetToolTip(MyToolTips)
            try
            {
                var tips = new MyToolTips();
                tips.AddToolTip(tip);

                var m = control.GetType().GetMethod("SetToolTip",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(MyToolTips) },
                    null);

                if (m != null)
                {
                    m.Invoke(control, new object[] { tips });
                    return;
                }
            }
            catch { }

            // 4) Last resort: try to find internal tooltip field and set it
            try
            {
                var tips = new MyToolTips();
                tips.AddToolTip(tip);

                var f =
                    control.GetType().GetField("m_tooltips", BindingFlags.NonPublic | BindingFlags.Instance) ??
                    control.GetType().GetField("_tooltips", BindingFlags.NonPublic | BindingFlags.Instance) ??
                    control.GetType().GetField("Tooltips", BindingFlags.NonPublic | BindingFlags.Instance);

                if (f != null && typeof(MyToolTips).IsAssignableFrom(f.FieldType))
                    f.SetValue(control, tips);
            }
            catch { }
        }
    }
}
