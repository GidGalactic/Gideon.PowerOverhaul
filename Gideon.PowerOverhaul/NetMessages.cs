using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Gideon.PowerOverhaul.Net
{
    public enum CmdType : byte
    {
        RequestSnapshot = 1,
        Unsubscribe = 2,

        // UI actions
        SetPowerOn = 10,
        SetBurstSettings = 11,
        TriggerBurst = 12,

        SetRouteTarget = 20,
        SetRoutePercent = 21,
        ResetRoute = 22,
        ApplyRouting = 23,

        SetMainRoutePercent = 24,
        ClearMainRoutes = 25,

        TriggerSacrifice = 30,
        SetConsolidation = 31,

        ApplyProfile = 40,

        // Engineering
        AddSubsystem = 50,
        DeleteSubsystem = 51,
        RenameSubsystem = 52,
        AssignBlock = 53,
    }

    public struct PowerCmd
    {
        public long GridId;
        public CmdType Type;
        public int SystemId;
        public int ArgI;
        public float ArgF1;
        public float ArgF2;
        public bool ArgB;
        public long ArgEntityId;
        public string ArgS;

        public byte[] ToBytes()
        {
            using (var ms = new MemoryStream(128))
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(GridId);
                w.Write((byte)Type);
                w.Write(SystemId);
                w.Write(ArgI);
                w.Write(ArgF1);
                w.Write(ArgF2);
                w.Write(ArgB);
                w.Write(ArgEntityId);
                w.Write(ArgS ?? "");
                w.Flush();
                return ms.ToArray();
            }
        }

        public static bool TryFromBytes(byte[] data, out PowerCmd cmd)
        {
            cmd = default;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true))
                {
                    cmd.GridId = r.ReadInt64();
                    cmd.Type = (CmdType)r.ReadByte();
                    cmd.SystemId = r.ReadInt32();
                    cmd.ArgI = r.ReadInt32();
                    cmd.ArgF1 = r.ReadSingle();
                    cmd.ArgF2 = r.ReadSingle();
                    cmd.ArgB = r.ReadBoolean();
                    cmd.ArgEntityId = r.ReadInt64();
                    cmd.ArgS = r.ReadString();
                    return true;
                }
            }
            catch
            {
                cmd = default;
                return false;
            }
        }
    }

    public struct SystemSnapshot
    {
        public int SystemId;
        public byte Kind;
        public string Name;

        public bool PowerOn;

        public float LocalMW;
        public float RequiredMW;
        public float IncomingMW;

        public int RouteTargetId;
        public float RoutePercent;

        public bool IsBursting;
        public float BurstRemaining;

        public float Stress;
        public uint Flags;
    }

    public struct MainRouteEntry
    {
        public int TargetSystemId;
        public float Percent;
    }

    public struct PowerSnapshot
    {
        public long GridId;

        public bool HarmonicActive;
        public float MainDrawMW;
        public float HarmonicBonus;
        public float MainBurstBonus;
        public float TotalHarmonicBonus;

        public float BlackoutRemaining;
        public bool ControlLossActive;
        public float ControlLossTime;

        public List<SystemSnapshot> Systems;
        public List<MainRouteEntry> MainRoutes;
        public float MainAllocatedPercent;

        public bool DebugEnabled;

        public byte[] ToBytes()
        {
            using (var ms = new MemoryStream(1024))
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(GridId);

                w.Write(HarmonicActive);
                w.Write(MainDrawMW);
                w.Write(HarmonicBonus);
                w.Write(MainBurstBonus);
                w.Write(TotalHarmonicBonus);

                w.Write(BlackoutRemaining);
                w.Write(ControlLossActive);
                w.Write(ControlLossTime);

                w.Write(DebugEnabled);

                int sc = Systems?.Count ?? 0;
                w.Write(sc);
                if (sc > 0)
                {
                    for (int i = 0; i < sc; i++)
                    {
                        var s = Systems[i];
                        w.Write(s.SystemId);
                        w.Write(s.Kind);
                        w.Write(s.Name ?? "");
                        w.Write(s.PowerOn);
                        w.Write(s.LocalMW);
                        w.Write(s.RequiredMW);
                        w.Write(s.IncomingMW);
                        w.Write(s.RouteTargetId);
                        w.Write(s.RoutePercent);
                        w.Write(s.IsBursting);
                        w.Write(s.BurstRemaining);
                        w.Write(s.Stress);
                        w.Write(s.Flags);
                    }
                }

                int mc = MainRoutes?.Count ?? 0;
                w.Write(mc);
                if (mc > 0)
                {
                    for (int i = 0; i < mc; i++)
                    {
                        var e = MainRoutes[i];
                        w.Write(e.TargetSystemId);
                        w.Write(e.Percent);
                    }
                }

                w.Write(MainAllocatedPercent);

                w.Flush();
                return ms.ToArray();
            }
        }

        public static bool TryFromBytes(byte[] data, out PowerSnapshot snap)
        {
            snap = default;
            try
            {
                using (var ms = new MemoryStream(data))
                using (var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true))
                {
                    snap.GridId = r.ReadInt64();

                    snap.HarmonicActive = r.ReadBoolean();
                    snap.MainDrawMW = r.ReadSingle();
                    snap.HarmonicBonus = r.ReadSingle();
                    snap.MainBurstBonus = r.ReadSingle();
                    snap.TotalHarmonicBonus = r.ReadSingle();

                    snap.BlackoutRemaining = r.ReadSingle();
                    snap.ControlLossActive = r.ReadBoolean();
                    snap.ControlLossTime = r.ReadSingle();

                    snap.DebugEnabled = r.ReadBoolean();

                    int sc = r.ReadInt32();
                    snap.Systems = new List<SystemSnapshot>(Math.Max(sc, 0));
                    for (int i = 0; i < sc; i++)
                    {
                        var s = new SystemSnapshot();
                        s.SystemId = r.ReadInt32();
                        s.Kind = r.ReadByte();
                        s.Name = r.ReadString();
                        s.PowerOn = r.ReadBoolean();
                        s.LocalMW = r.ReadSingle();
                        s.RequiredMW = r.ReadSingle();
                        s.IncomingMW = r.ReadSingle();
                        s.RouteTargetId = r.ReadInt32();
                        s.RoutePercent = r.ReadSingle();
                        s.IsBursting = r.ReadBoolean();
                        s.BurstRemaining = r.ReadSingle();
                        s.Stress = r.ReadSingle();
                        s.Flags = r.ReadUInt32();
                        snap.Systems.Add(s);
                    }

                    int mc = r.ReadInt32();
                    snap.MainRoutes = new List<MainRouteEntry>(Math.Max(mc, 0));
                    for (int i = 0; i < mc; i++)
                    {
                        var e = new MainRouteEntry();
                        e.TargetSystemId = r.ReadInt32();
                        e.Percent = r.ReadSingle();
                        snap.MainRoutes.Add(e);
                    }

                    snap.MainAllocatedPercent = r.ReadSingle();
                    return true;
                }
            }
            catch
            {
                snap = default;
                return false;
            }
        }
    }

    public static class Net
    {
        public static void SendCmdToServer(PowerCmd cmd)
        {
            var bytes = cmd.ToBytes();
            MyAPIGateway.Multiplayer.SendMessageToServer(PowerOverhaulSession.NET_ID, bytes);
        }

        public static void SendSnapshotToClient(ulong steamId, PowerSnapshot snap)
        {
            var bytes = snap.ToBytes();
            MyAPIGateway.Multiplayer.SendMessageTo(PowerOverhaulSession.NET_ID, bytes, steamId);
        }

        public static bool TryParseCommand(byte[] data, out PowerCmd cmd)
            => PowerCmd.TryFromBytes(data, out cmd);

        public static bool TryParseSnapshot(byte[] data, out PowerSnapshot snap)
            => PowerSnapshot.TryFromBytes(data, out snap);
    }
}
