using System.Collections.Generic;

namespace ART.ADK
{
    /// <summary>
    /// Options for pushing messages to a channel.
    /// </summary>
    public class PushOptions
    {
        /// <summary>Target user(s) for targeted/secure channels.</summary>
        public List<string> To { get; set; } = new List<string>();

        /// <summary>Username of the sender.</summary>
        public string FromUsername { get; set; } = "";

        public PushOptions() { }

        public PushOptions(params string[] to)
        {
            To = new List<string>(to);
        }
    }

    /// <summary>
    /// Options for REST API calls via Adk.Call().
    /// </summary>
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
}
