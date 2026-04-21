using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ART.ADK
{
    /// <summary>
    /// Represents a WebSocket message sent/received by the ART infrastructure.
    /// </summary>
    public class ArtMessage
    {
        [JsonProperty("from")]
        public string From { get; set; } = "";

        [JsonProperty("to")]
        public JToken To { get; set; } = new JArray();

        [JsonProperty("channel")]
        public string Channel { get; set; } = "";

        [JsonProperty("namespace")]
        public string Namespace { get; set; } = "";

        [JsonProperty("event")]
        public string Event { get; set; } = "";

        [JsonProperty("content")]
        public object Content { get; set; }

        [JsonProperty("data")]
        public object Data { get; set; }

        [JsonProperty("ref_id")]
        public string RefId { get; set; } = "";

        [JsonProperty("return_flag")]
        public string ReturnFlag { get; set; } = "";

        [JsonProperty("interceptor_name")]
        public string InterceptorName { get; set; } = "";

        [JsonProperty("id")]
        public string Id { get; set; } = "";

        [JsonProperty("pipeline_id")]
        public string PipelineId { get; set; } = "";

        [JsonProperty("attempt_id")]
        public string AttemptId { get; set; } = "";

        [JsonProperty("to_username")]
        public string ToUsername { get; set; } = "";

        [JsonProperty("from_username")]
        public string FromUsername { get; set; } = "";
    }

    /// <summary>
    /// Connection details returned after successful WebSocket binding.
    /// </summary>
    public class ConnectionDetail
    {
        public string ConnectionId { get; set; } = "";
        public string InstanceId { get; set; } = "";
        public string TenantName { get; set; } = "";
        public string Environment { get; set; } = "";
        public string ProjectKey { get; set; } = "";
    }

    /// <summary>
    /// Channel configuration returned from the server on subscribe.
    /// </summary>
    public class ChannelConfig
    {
        public string ChannelName { get; set; } = "";
        public string ChannelNamespace { get; set; } = "";
        public string ChannelType { get; set; } = "default";
        public List<string> PresenceUsers { get; set; } = new List<string>();
        public object Snapshot { get; set; }
        public string SubscriptionID { get; set; }
    }
}
