using System.Collections.Generic;

namespace ART.ADK
{
    /// <summary>
    /// Holds presence information for a channel.
    /// </summary>
    public class PresenceInfo
    {
        public List<string> Usernames { get; set; } = new List<string>();
        public bool Error { get; set; }
    }
}
