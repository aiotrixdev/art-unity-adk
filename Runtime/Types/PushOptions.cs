using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;

namespace ART.ADK
{
    /// <summary>
    /// Options for pushing messages to a channel.
    /// Mirrors the JS <c>PushConfig</c> (ExecutionOrigin, to, ThreadID, FileMeta).
    /// </summary>
    public class PushOptions
    {
        /// <summary>Expected value: "playground".</summary>
        public string ExecutionOrigin { get; set; }

        /// <summary>Target user(s) for targeted/secure channels.</summary>
        public List<string> To { get; set; } = new List<string>();

        /// <summary>
        /// Orchestrator thread this push belongs to. Serialized as a top-level
        /// <c>thread_id</c> field on the wire frame, NOT inside the content payload.
        /// </summary>
        public string ThreadId { get; set; }

        /// <summary>Files attached to the message (sent as <c>file_meta</c>).</summary>
        public List<MediaRef> FileMeta { get; set; }

        public PushOptions() { }

        public PushOptions(params string[] to)
        {
            To = new List<string>(to);
        }
    }

    /// <summary>
    /// JS <c>PushConfig</c>. Same fields as <see cref="PushOptions"/> under the JS names; used by
    /// <c>OrchestratorThread.Push()</c>.
    /// NOTE: the old <c>EventName</c> and <c>TimeoutMs</c> fields are gone because the JS type does
    /// not have them - remove any code that still reads them.
    /// </summary>
    public class PushConfig
    {
        /// <summary>Expected value: "playground".</summary>
        public string ExecutionOrigin { get; set; }

        public List<string> To { get; set; } = new List<string>();

        public string ThreadID { get; set; }

        public List<MediaRef> FileMeta { get; set; }

        public PushConfig() { }

        // Copy constructor used in OrchestratorThread.Push()
        public PushConfig(PushConfig source)
        {
            if (source == null) return;
            ExecutionOrigin = source.ExecutionOrigin;
            To = source.To != null ? new List<string>(source.To) : new List<string>();
            ThreadID = source.ThreadID;
            FileMeta = source.FileMeta != null ? new List<MediaRef>(source.FileMeta) : null;
        }
    }

    /// <summary>JS <c>MediaRef</c>: { id, name, content_type }.</summary>
    public class MediaRef
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("content_type")] public string ContentType { get; set; }
    }

    /// <summary>JS <c>AdkSecureUserInterface</c>.</summary>
    public class AdkSecureUserInterface
    {
        public string Username { get; set; } = "";
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
    }

    /// <summary>
    /// Options for REST API calls via Adk.Call(). Mirrors the JS <c>CallApiProps</c>.
    /// </summary>
    public class CallApiProps
    {
        /// <summary>HTTP method; defaults to GET when null.</summary>
        public string Method { get; set; }

        public object Payload { get; set; }

        public Dictionary<string, string> QueryParams { get; set; }

        public Dictionary<string, string> Headers { get; set; }

        /// <summary>Optional cancel (JS: signal).</summary>
        public CancellationToken Signal { get; set; }

        /// <summary>Optional request timeout in milliseconds.</summary>
        public long? TimeoutMs { get; set; }

        /// <summary>Overrides the configured base URL for this call.</summary>
        public string BaseUrl { get; set; }
    }

    /// <summary>
    /// Legacy options type for <c>Adk.Call()</c>. The current <c>Adk.Call</c> takes
    /// <see cref="CallApiProps"/>; this is kept only so older code keeps compiling.
    /// </summary>
    [Obsolete("Use CallApiProps instead.")]
    public class CallApiOptions
    {
        public string Method { get; set; } = "GET";
        public object Payload { get; set; }
        public Dictionary<string, string> QueryParams { get; set; }
        public Dictionary<string, string> Headers { get; set; }
    }

    /// <summary>
    /// ADK SDK configuration.
    /// </summary>
    public class AdkConfig
    {
        public string Uri { get; set; } = "";
        public string AuthToken { get; set; }
        public string Root { get; set; }

        /// <summary>
        /// Maximum wait for connection readiness/control replies, not execution duration.
        /// Default 30 seconds (null means use the default).
        /// </summary>
        public long? ConnectionTimeoutMs { get; set; }

        public bool AutoLoadCredsFromJSON { get; set; } = false;

        /// <summary>
        /// Optional callback to provide credentials dynamically.
        /// </summary>
        public System.Func<CredentialStore> GetCredentials { get; set; }
    }

    /// <summary>
    /// Credential store for authentication.
    /// </summary>
    public class CredentialStore
    {
        public string Environment { get; set; } = "";
        public string ProjectKey { get; set; } = "";
        public string OrgTitle { get; set; } = "";
        public string ClientID { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        public string AccessToken { get; set; }
        public AdkConfig Config { get; set; }
    }

    /// <summary>
    /// Internal authentication configuration.
    /// </summary>
    internal class AuthenticationConfig
    {
        public string Environment { get; set; } = "";
        public string ProjectKey { get; set; } = "";
        public string OrgTitle { get; set; } = "";
        public string ClientID { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        public string AccessToken { get; set; }
        public AdkConfig Config { get; set; }
        public System.Func<CredentialStore> GetCredentials { get; set; }
    }

    /// <summary>
    /// Internal auth data (JWT tokens).
    /// </summary>
    internal class AuthData
    {
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
    }

    /// <summary>
    /// Connection config for Adk.Connect().
    /// </summary>
    public class ConnectConfig
    {
        public bool RestoreConnection { get; set; }
    }

    /// <summary>
    /// Encryption key pair.
    /// </summary>
    public class KeyPairType
    {
        public string PublicKey { get; set; } = "";
        public string PrivateKey { get; set; } = "";

        public KeyPairType() { }
        public KeyPairType(string publicKey, string privateKey)
        {
            PublicKey = publicKey;
            PrivateKey = privateKey;
        }
    }
  

  public class UpdateProfileData
    {
        public string FirstName { get; set; }

        public string LastName { get; set; }

        public string Email { get; set; }
        public Dictionary<string, object> Attributes { get; set; }
    }


}