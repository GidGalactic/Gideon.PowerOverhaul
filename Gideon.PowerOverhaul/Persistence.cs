using Sandbox.ModAPI;
using System;
using System.IO;
using VRage.Utils;
using Gideon.PowerOverhaul.Model;

namespace Gideon.PowerOverhaul
{
    public static class Persistence
    {
        const string SETTINGS_FILE = "PowerOverhaul_Settings.xml";

        const string GRID_PREFIX = "PowerOverhaul_Grid_";

        public static void LoadServerSettings(out bool debugEnabled)
        {
            debugEnabled = false;
            try
            {
                if (!MyAPIGateway.Utilities.FileExistsInWorldStorage(SETTINGS_FILE, typeof(Persistence)))
                    return;

                using (var reader = MyAPIGateway.Utilities.ReadFileInWorldStorage(SETTINGS_FILE, typeof(Persistence)))
                {
                    var xml = reader.ReadToEnd();
                    debugEnabled = xml.Contains("<Debug>true</Debug>");
                }
            }
            catch { }
        }

        public static void SaveGridState(Model.GridPowerModel model)
        {
            try
            {
                string file = $"{GRID_PREFIX}{model.GridId}.xml";

                using (var writer = MyAPIGateway.Utilities.WriteFileInWorldStorage(file, typeof(Persistence)))
                {
                    // Minimal XML for now: main routes + (later) subsystem routes + assignments + profiles
                    // We'll only persist main routes in this step.

                    var snap = model.BuildSnapshot(debugEnabled: false);

                    writer.Write("<GridState>");
                    writer.Write($"<GridId>{model.GridId}</GridId>");

                    writer.Write("<MainRoutes>");
                    if (snap.MainRoutes != null)
                    {
                        foreach (var e in snap.MainRoutes)
                            writer.Write($"<R t=\"{e.TargetSystemId}\" p=\"{e.Percent}\" />");
                    }
                    writer.Write("</MainRoutes>");

                    writer.Write("</GridState>");
                }
            }
            catch { }
        }

        public static bool TryLoadGridState(long gridId, Model.GridPowerModel model)
        {
            try
            {
                string file = $"{GRID_PREFIX}{gridId}.xml";
                if (!MyAPIGateway.Utilities.FileExistsInWorldStorage(file, typeof(Persistence)))
                    return false;

                using (var reader = MyAPIGateway.Utilities.ReadFileInWorldStorage(file, typeof(Persistence)))
                {
                    string xml = reader.ReadToEnd();
                    // Very simple parse (safe enough for small tags)
                    // Extract <R t="X" p="Y" />
                    int idx = 0;
                    while (true)
                    {
                        int r = xml.IndexOf("<R ", idx, StringComparison.OrdinalIgnoreCase);
                        if (r < 0) break;
                        int end = xml.IndexOf("/>", r, StringComparison.OrdinalIgnoreCase);
                        if (end < 0) break;

                        string tag = xml.Substring(r, end - r + 2);
                        idx = end + 2;

                        int tPos = tag.IndexOf("t=\"", StringComparison.OrdinalIgnoreCase);
                        int pPos = tag.IndexOf("p=\"", StringComparison.OrdinalIgnoreCase);
                        if (tPos < 0 || pPos < 0) continue;

                        int tEnd = tag.IndexOf("\"", tPos + 3);
                        int pEnd = tag.IndexOf("\"", pPos + 3);
                        if (tEnd < 0 || pEnd < 0) continue;

                        if (int.TryParse(tag.Substring(tPos + 3, tEnd - (tPos + 3)), out int tid) &&
                            float.TryParse(tag.Substring(pPos + 3, pEnd - (pPos + 3)), out float pct))
                        {
                            // Load into draft then apply to live
                            model.ApplyCommand(new Net.PowerCmd { GridId = gridId, Type = Net.CmdType.SetMainRoutePercent, SystemId = Model.SystemIds.Main, ArgI = tid, ArgF1 = pct });
                        }
                    }

                    // ApplyRouting to commit draft to live
                    model.ApplyCommand(new Net.PowerCmd { GridId = gridId, Type = Net.CmdType.ApplyRouting, SystemId = Model.SystemIds.Main });

                    return true;
                }
            }
            catch { return false; }
        }

        public static void SaveServerSettings(bool debugEnabled)
        {
            try
            {
                using (var writer = MyAPIGateway.Utilities.WriteFileInWorldStorage(SETTINGS_FILE, typeof(Persistence)))
                {
                    writer.Write($"<Settings><Debug>{(debugEnabled ? "true" : "false")}</Debug></Settings>");
                }
            }
            catch { }
        }

        //public static void SaveGridState(GridPowerModel model)
        //{
            // TODO: serialize subsystem list, assignments, routing, profiles
            // Use one file per grid:
            // PowerOverhaul_Grid_<id>.bin or .xml
        //}

        //public static bool TryLoadGridState(long gridId, GridPowerModel model)
        //{
            // TODO: load matching file if exists and repopulate model
            //return false;
        //}
    }
}


