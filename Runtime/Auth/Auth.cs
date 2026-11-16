using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace ART.ADK
{
    /// <summary>
    /// Singleton authentication manager handling JWT token lifecycle.
    /// <para>
    /// - <see cref="Authenticate"/> is single-flight: concurrent callers (socket,
    ///   long-poll, REST, storage) share one in-flight token request, so a refresh
    ///   token is never rotated twice in parallel.
    /// - Access tokens are renewed <see cref="RenewalLeewaySeconds"/> seconds
    ///   <b>before</b> they expire.
    /// - A complete client ID and secret pair uses the client-credentials grant:
    ///   tokens are renewed by authenticating again, never with a refresh token.
    ///   Without a client secret, <c>AccessToken</c> is exchanged and renewed with
    ///   the refresh token.
    /// </para>
    /// </summary>
    internal class Auth
    {
        private static Auth _instance;
        private static readonly object _lock = new object();

        // Guards _credentials, _authData and _inFlight.
        private readonly object _stateLock = new object();

        public CredentialStore _credentials;
        private AuthData _authData = new AuthData();
        private Task<AuthData> _inFlight;

        /// <summary>Seconds before <c>exp</c> at which an access token is renewed.</summary>
        private const double RenewalLeewaySeconds = 30;

        public string UserId
        {
            get
            {
                if (string.IsNullOrEmpty(_authData.AccessToken)) return null;
                try
                {
                    var payload = DecodeJWTPayload(_authData.AccessToken);
                    return payload.TryGetValue("sub", out var sub) ? sub?.ToString() : null;
                }
                catch { return null; }
            }
        }

        public string Username
        {
            get
            {
                if (string.IsNullOrEmpty(_authData.AccessToken)) return null;
                try
                {
                    var payload = DecodeJWTPayload(_authData.AccessToken);
                    if (payload.TryGetValue("name", out var n)) return n?.ToString();
                    if (payload.TryGetValue("username", out var u)) return u?.ToString();
                    return UserId;
                }
                catch { return null; }
            }
        }

        private Auth(CredentialStore credentials)
        {
            _credentials = credentials;
        }

        /// <summary>
        /// Returns the process-wide <see cref="Auth"/> instance.
        /// <para>
        /// <paramref name="credentials"/> is required on the very first call. On
        /// later calls it replaces the stored credentials when it names the same
        /// identity (organisation, environment, project and client ID), and replaces
        /// the whole instance, dropping its tokens, when it names another one.
        /// </para>
        /// </summary>
        public static Auth GetInstance(CredentialStore credentials = null)
        {
            lock (_lock)
            {
                if (_instance != null && credentials != null)
                {
                    if (SameIdentity(_instance._credentials, credentials))
                        _instance.ReplaceCredentials(credentials);
                    else
                        _instance = new Auth(credentials);
                }

                if (_instance != null)
                    return _instance;

                if (credentials == null)
                    throw new ARTForbiddenException("Auth not initialised - provide credentials on first call");

                _instance = new Auth(credentials);
                return _instance;
            }
        }

        public static void Reset()
        {
            lock (_lock) { _instance = null; }
        }

        private void ReplaceCredentials(CredentialStore credentials)
        {
            lock (_stateLock) { _credentials = credentials; }
        }

        private static bool SameIdentity(CredentialStore a, CredentialStore b)
        {
            if (a == null || b == null) return false;
            return a.OrgTitle == b.OrgTitle &&
                   a.Environment == b.Environment &&
                   a.ProjectKey == b.ProjectKey &&
                   a.ClientID == b.ClientID;
        }

        private static bool UsesClientCredentials(CredentialStore c)
            => !string.IsNullOrEmpty(c.ClientID) && !string.IsNullOrEmpty(c.ClientSecret);

        /// <summary>
        /// Returns a valid token pair, getting or renewing it when needed.
        /// Concurrent calls share a single request; <paramref name="forceAuth"/>
        /// renews a token that is still valid (used when reconnecting).
        /// <para>
        /// Throws <see cref="AuthHttpError"/> when the server rejects the request,
        /// and <see cref="ARTAuthenticationException"/> when the credentials are
        /// incomplete, the server cannot be reached or does not answer within
        /// <see cref="AdkConfig.ConnectionTimeoutMs"/>, or the response holds no
        /// access token.
        /// </para>
        /// </summary>
        public async Task<AuthData> Authenticate(bool forceAuth = false)
        {
            Task<AuthData> task;
            lock (_stateLock)
            {
                if (_inFlight != null)
                {
                    task = _inFlight;
                }
                else
                {
                    task = AuthenticateOnce(forceAuth);
                    _inFlight = task;
                }
            }

            try
            {
                return await task;
            }
            finally
            {
                lock (_stateLock)
                {
                    if (_inFlight == task) _inFlight = null;
                }
            }
        }

        private async Task<AuthData> AuthenticateOnce(bool forceAuth)
        {
            AuthData cached;
            lock (_stateLock) { cached = _authData; }

            if (!forceAuth &&
                !string.IsNullOrEmpty(cached.AccessToken) &&
                !IsTokenExpired(cached.AccessToken))
            {
                return cached;
            }

            ReadCredentialsProvider();

            CredentialStore creds;
            lock (_stateLock) { creds = _credentials; }

            if (string.IsNullOrEmpty(creds.OrgTitle) ||
                string.IsNullOrEmpty(creds.Environment) ||
                string.IsNullOrEmpty(creds.ProjectKey))
            {
                throw new ARTAuthenticationException("OrgTitle, Environment and ProjectKey are required");
            }

            // Client credentials renew by authenticating again, never through a
            // refresh token, even when an older server still returns one.
            if (UsesClientCredentials(creds))
                return await GenerateAuthToken(creds);

            string refreshToken;
            lock (_stateLock) { refreshToken = _authData.RefreshToken; }
            if (!GetRefreshTokenExpiryInfo(refreshToken).expired)
                return await RefreshAuthToken(creds, refreshToken);

            return await GenerateAuthToken(creds);
        }

        /// <summary>
        /// Applies the <c>GetCredentials</c> hook. Tokens issued to another identity
        /// are dropped, so they are never reused or refreshed.
        /// </summary>
        private void ReadCredentialsProvider()
        {
            var hook = _credentials?.Config?.GetCredentials;
            if (hook == null) return;

            var cred = hook();
            if (cred == null) return;

            lock (_stateLock)
            {
                bool sameId = SameIdentity(_credentials, cred);
                _credentials.AccessToken = cred.AccessToken;
                _credentials.ClientID = cred.ClientID;
                _credentials.ClientSecret = cred.ClientSecret;
                _credentials.OrgTitle = cred.OrgTitle;
                _credentials.Environment = cred.Environment;
                _credentials.ProjectKey = cred.ProjectKey;
                if (!sameId)
                    _authData = new AuthData();
            }
        }

        public AuthData GetAuthData() => _authData;
        public CredentialStore GetCredentials() => _credentials;

        private async Task<AuthData> GenerateAuthToken(CredentialStore creds)
        {
            var accessToken = creds.AccessToken ?? "";
            if (string.IsNullOrEmpty(accessToken) && !UsesClientCredentials(creds))
                throw new ARTAuthenticationException("ClientID and ClientSecret required when AccessToken is absent");

            var headers = new Dictionary<string, string>
            {
                { "Content-Type", "application/json" },
                { "X-Org", creds.OrgTitle },
                { "Environment", creds.Environment },
                { "ProjectKey", creds.ProjectKey },
            };
            if (UsesClientCredentials(creds))
            {
                headers["Client-Id"] = creds.ClientID;
                headers["Client-Secret"] = creds.ClientSecret;
            }
            else
            {
                headers["T-pass"] = accessToken;
            }
            AddPasscode(headers, creds);

            var data = await Post("/auth/token", headers, null, creds);
            return Store(data, creds, "Authentication did not return an access token. Please sign in again.");
        }

        private async Task<AuthData> RefreshAuthToken(CredentialStore creds, string refreshToken)
        {
            var accessToken = creds.AccessToken ?? "";
            if (string.IsNullOrEmpty(accessToken) && string.IsNullOrEmpty(creds.ClientID))
                throw new ARTAuthenticationException("ClientID required when AccessToken is absent");

            var headers = new Dictionary<string, string>
            {
                { "Content-Type", "application/json" },
                { "X-Org", creds.OrgTitle },
                { "Environment", creds.Environment },
                { "ProjectKey", creds.ProjectKey },
            };
            if (!string.IsNullOrEmpty(creds.ClientID))
                headers["Client-Id"] = creds.ClientID;
            if (!string.IsNullOrEmpty(accessToken))
                headers["T-pass"] = accessToken;
            AddPasscode(headers, creds);

            var body = new JObject { ["refresh_token"] = refreshToken };
            var data = await Post("/auth/token/refresh", headers, body, creds);
            return Store(data, creds, "Authentication refresh did not return an access token. Please sign in again.");
        }

        private static void AddPasscode(Dictionary<string, string> headers, CredentialStore creds)
        {
            var authToken = creds.Config?.AuthToken;
            if (!string.IsNullOrEmpty(authToken))
                headers["X-pass"] = authToken;
        }

        /// <summary>
        /// Stores the tokens in <paramref name="data"/>; throws
        /// <paramref name="missingTokenMessage"/> as an
        /// <see cref="ARTAuthenticationException"/> when it holds no access token.
        /// </summary>
        private AuthData Store(JObject data, CredentialStore creds, string missingTokenMessage)
        {
            var accessToken = data["access_token"]?.ToString();
            if (string.IsNullOrEmpty(accessToken))
                throw new ARTAuthenticationException(missingTokenMessage);

            var fresh = new AuthData
            {
                AccessToken = accessToken,
                RefreshToken = UsesClientCredentials(creds) ? "" : (data["refresh_token"]?.ToString() ?? "")
            };
            lock (_stateLock) { _authData = fresh; }
            return fresh;
        }

        /// <summary>
        /// Sends an auth request and returns the <c>data</c> object of its response.
        /// Gives up after <see cref="AdkConfig.ConnectionTimeoutMs"/> (30 seconds by
        /// default). Throws <see cref="AuthHttpError"/> on a non-2xx response.
        /// </summary>
        private async Task<JObject> Post(
            string path,
            Dictionary<string, string> headers,
            JObject body,
            CredentialStore creds)
        {
            var url = $"{ArtConstants.BASE_URL}{path}";
            using var request = new UnityWebRequest(url, "POST");
            request.downloadHandler = new DownloadHandlerBuffer();
            if (body != null)
            {
                var bytes = Encoding.UTF8.GetBytes(body.ToString(Formatting.None));
                request.uploadHandler = new UploadHandlerRaw(bytes);
            }
            foreach (var kv in headers)
                request.SetRequestHeader(kv.Key, kv.Value);

            var timeoutMs = creds.Config?.ConnectionTimeoutMs ?? 30000;
            request.timeout = (int)Math.Max(1, timeoutMs / 1000);

            var op = request.SendWebRequest();
            while (!op.isDone) await Task.Yield();

            // Network / timeout failure (not an HTTP status).
            if (request.result == UnityWebRequest.Result.ConnectionError ||
                request.result == UnityWebRequest.Result.DataProcessingError)
            {
                throw new ARTAuthenticationException(
                    "Authentication timed out. Please check your connection and sign in again.");
            }

            JToken json = null;
            var text = request.downloadHandler?.text;
            if (!string.IsNullOrEmpty(text))
            {
                try { json = JToken.Parse(text); } catch { json = null; }
            }

            var status = (int)request.responseCode;
            if (status < 200 || status >= 300)
            {
                // Keep both tokens: the gateway only lacked a WebSocket backend.
                if (path.EndsWith("/refresh") && status == 500)
                {
                    var wsMsg = (json as JObject)?["error"]?.ToString();
                    if (wsMsg == "Failed to get WebSocket backend")
                        throw new ARTServerException(wsMsg);
                }

                throw new AuthHttpError(
                    ErrorMessage(json) ?? StatusText(status),
                    status,
                    json);
            }

            var obj = json as JObject;
            if (obj == null)
                throw new ARTAuthenticationException($"Unexpected response from {path}");
            return obj["data"] as JObject ?? new JObject();
        }

        private static string ErrorMessage(JToken json)
        {
            var obj = json as JObject;
            if (obj == null) return null;
            foreach (var key in new[] { "message", "error" })
            {
                var val = obj[key]?.ToString();
                if (!string.IsNullOrEmpty(val)) return val;
            }
            return null;
        }

        private static string StatusText(int status) => $"HTTP {status}";

        private bool IsTokenExpired(string token)
        {
            if (string.IsNullOrEmpty(token)) return true;
            try
            {
                var payload = DecodeJWTPayload(token);
                if (payload.TryGetValue("exp", out var expObj) && double.TryParse(expObj.ToString(), out var exp))
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    return exp <= now + RenewalLeewaySeconds;
                }
            }
            catch { }
            return true;
        }

        private Dictionary<string, object> DecodeJWTPayload(string token)
        {
            var parts = token.Split('.');
            if (parts.Length < 2)
                throw new ARTAuthenticationException("Malformed JWT");

            var b64 = parts[1].Replace("-", "+").Replace("_", "/");
            var pad = (4 - b64.Length % 4) % 4;
            b64 += new string('=', pad);

            var jsonBytes = Convert.FromBase64String(b64);
            var jsonStr = Encoding.UTF8.GetString(jsonBytes);
            return JsonConvert.DeserializeObject<Dictionary<string, object>>(jsonStr);
        }

        private (bool expired, double? exp, double remaining) GetRefreshTokenExpiryInfo(string token)
        {
            if (string.IsNullOrEmpty(token))
                return (true, null, 0);

            var parts = token.Split('.');
            if (parts.Length < 2 || !double.TryParse(parts[1], out var exp))
                return (true, null, 0);

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return (now >= exp, exp, exp - now);
        }
    }
}
