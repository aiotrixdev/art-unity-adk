using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace ART.ADK
{
    /// <summary>
    /// Represents a notification object within the ART ADK system.
    /// </summary>
    [Serializable]
    public class ArtNotification
    {
        [JsonProperty("id")]
        public string Id { get; set; } = "";

        [JsonProperty("title")]
        public string Title { get; set; } = "";

        [JsonProperty("body")]
        public string Body { get; set; } = "";

        [JsonProperty("type")]
        public string Type { get; set; } = "";

        [JsonProperty("status")]
        public string Status { get; set; } = "unread";

        [JsonProperty("created_at")]
        public string CreatedAt { get; set; } = "";

        [JsonProperty("data")]
        public Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();
    }
}
