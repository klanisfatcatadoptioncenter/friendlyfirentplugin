using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SamplePlugin
{
  
    public class CachedFriendEntry
    {
        public string Name { get; set; } = string.Empty;
        public ushort WorldId { get; set; }
        public long LastSeenUnixSeconds { get; set; }
        public ulong ContentId { get; set; } // <-- NEW
    }

}
