using Sandbox.ModAPI;
using System;
using System.Collections.Generic;

namespace Gideon.PowerOverhaul
{
    // Basic “profile” storage. Expand this as your design grows.
    public static class Profiles
    {
        // Stored on server (authoritative). Clients can request a list later if you add networking for it.
        private static readonly Dictionary<long, List<PowerProfile>> _gridProfiles =
            new Dictionary<long, List<PowerProfile>>(64);

        // ---------- Data Types ----------
        [Serializable]
        public class PowerProfile
        {
            public int ProfileId;
            public string Name;

            // Main distribution presets
            public List<MainRoutePreset> MainRoutes = new List<MainRoutePreset>();

            // Optional: per-subsystem presets (power on/off, route target, route %, burst settings, etc.)
            public List<SystemPreset> SystemPresets = new List<SystemPreset>();
        }

        [Serializable]
        public struct MainRoutePreset
        {
            public int TargetSystemId;
            public float Percent;
        }

        [Serializable]
        public struct SystemPreset
        {
            public int SystemId;
            public bool PowerOn;

            public int RouteTargetId;
            public float RoutePercent;

            public float BurstPercent;
            public float BurstDuration;
        }

        // ---------- Public API ----------
        public static List<PowerProfile> GetProfilesForGrid(long gridId)
        {
            if (!_gridProfiles.TryGetValue(gridId, out var list))
            {
                list = new List<PowerProfile>(8);
                _gridProfiles[gridId] = list;
            }
            return list;
        }

        public static PowerProfile GetProfile(long gridId, int profileId)
        {
            var list = GetProfilesForGrid(gridId);
            for (int i = 0; i < list.Count; i++)
                if (list[i].ProfileId == profileId)
                    return list[i];
            return null;
        }

        public static int AddOrReplaceProfile(long gridId, PowerProfile profile)
        {
            if (profile == null) return -1;

            var list = GetProfilesForGrid(gridId);

            // Assign an id if missing
            if (profile.ProfileId <= 0)
                profile.ProfileId = NextId(list);

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].ProfileId == profile.ProfileId)
                {
                    list[i] = profile;
                    return profile.ProfileId;
                }
            }

            list.Add(profile);
            return profile.ProfileId;
        }

        public static bool DeleteProfile(long gridId, int profileId)
        {
            var list = GetProfilesForGrid(gridId);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].ProfileId == profileId)
                {
                    list.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        // ---------- Persistence ----------
        // Call these from your Persistence.cs if you want profiles saved with the world.
        // You can keep profiles separate or merge into your grid-state blob.

        public static byte[] SerializeGridProfiles(long gridId)
        {
            try
            {
                var list = GetProfilesForGrid(gridId);
                return MyAPIGateway.Utilities.SerializeToBinary(list);
            }
            catch (Exception e)
            {
                Log.Error($"SerializeGridProfiles failed for grid {gridId}", e);
                return null;
            }
        }

        public static void DeserializeGridProfiles(long gridId, byte[] data)
        {
            if (data == null || data.Length == 0) return;

            try
            {
                var list = MyAPIGateway.Utilities.SerializeFromBinary<List<PowerProfile>>(data);
                if (list == null) return;
                _gridProfiles[gridId] = list;
            }
            catch (Exception e)
            {
                Log.Error($"DeserializeGridProfiles failed for grid {gridId}", e);
            }
        }

        // ---------- Helpers ----------
        private static int NextId(List<PowerProfile> list)
        {
            int max = 0;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].ProfileId > max)
                    max = list[i].ProfileId;
            return max + 1;
        }
    }
}
