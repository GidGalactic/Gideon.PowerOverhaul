namespace Gideon.PowerOverhaul.Model
{
    public static class SystemIds
    {
        public const int Main = -1;
        public const int Aux = -2;
        public const int Emergency = -3;
        public const int JumpUnique = -4; // optional fixed id for jump
    }

    public enum SystemKind : byte
    {
        Main = 1,
        Aux = 2,
        Emergency = 3,
        Propulsion = 10,
        Control = 11,
        Weapons = 12,
        Jump = 13,
        Custom = 20,
    }

    [System.Flags]
    public enum SystemFlags : uint
    {
        None = 0,
        Degraded = 1 << 0,
        Offline = 1 << 1,
        EmergencyFed = 1 << 2,
        BurstLocked = 1 << 3,
        ControlLoss = 1 << 4,
        Blackout = 1 << 5,
    }
}
