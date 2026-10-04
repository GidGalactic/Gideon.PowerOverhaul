using Sandbox.ModAPI;
using System;
using VRage.Utils;

namespace Gideon.PowerOverhaul
{
    public static class Log
    {
        public static bool EnableInfo = true;
        public static bool EnableDebug = false;

        private const string Prefix = "[PowerOverhaul] ";

        public static void Info(string msg)
        {
            if (!EnableInfo) return;
            Write("INFO", msg);
        }

        public static void Debug(string msg)
        {
            if (!EnableDebug) return;
            Write("DEBUG", msg);
        }

        public static void Warn(string msg)
        {
            Write("WARN", msg);
        }

        public static void Error(string msg, Exception ex)
        {
            if (ex != null)
                Write("ERROR", msg + " | " + ex.ToString());
            else
                Write("ERROR", msg);
        }

        public static void Hud(string msg)
        {
            try
            {
                if (MyAPIGateway.Utilities != null)
                    MyAPIGateway.Utilities.ShowMessage("Power", msg);
            }
            catch { }
        }

        private static void Write(string level, string msg)
        {
            try
            {
                if (MyLog.Default != null)
                    MyLog.Default.WriteLineAndConsole(Prefix + level + ": " + msg);
            }
            catch { }
        }
    }
}
