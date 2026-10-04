using VRage.Input;
using Sandbox.ModAPI;

namespace Gideon.PowerOverhaul
{
    public static class InputHotkey
    {
        // Ctrl+F to open routing page
        public static bool ConsumeOpenPressed()
        {
            var input = MyAPIGateway.Input;
            if (input == null) return false;

            return input.IsAnyCtrlKeyPressed() && input.IsNewKeyPressed(MyKeys.F);
        }
    }
}
