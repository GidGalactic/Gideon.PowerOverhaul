using System.Collections.Generic;

namespace Gideon.PowerOverhaul.Model
{
    // Stores "this block belongs to that system"
    public class AssignmentStore
    {
        // blockEntityId -> systemId
        private readonly Dictionary<long, int> _map = new Dictionary<long, int>(4096);

        public int Get(long blockEntityId)
            => _map.TryGetValue(blockEntityId, out var id) ? id : 0;

        public void Set(long blockEntityId, int systemId)
        {
            if (systemId == 0)
                _map.Remove(blockEntityId);
            else
                _map[blockEntityId] = systemId;
        }

        public Dictionary<long, int> GetRaw() => _map;
    }
}
