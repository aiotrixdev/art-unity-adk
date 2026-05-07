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
    /// </summary>
    internal class Auth
    {
        private static Auth _instance;
        private static readonly object _lock = new object();

        private AuthenticationConfig _credentials;
        private AuthData _authData = new AuthData();

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

        private Auth(AuthenticationConfig credentials)
        {
            _credentials = credentials;
        }

        public static Auth GetInstance(AuthenticationConfig credentials = null)
        {
            lock (_lock)
            {
                if (_instance == null)
                {
                    if (credentials == null)
                        throw new ARTForbiddenException("Auth not initialised - provide credentials on first call");
                    _instance = new Auth(credentials);
                }
                return _instance;
            }
        }

        public static void Reset()
        {
            lock (_lock) { _instance = null; }
        }

        public async Task<AuthData> Authenticate(bool forceAuth = false)
        {
            if (!forceAuth && !string.IsNullOrEmpty(_authData.AccessToken) && !IsTokenExpired(_authData.AccessToken))
                return _authData;

            // Refresh credentials via hook if present
            if (_credentials.GetCredentials != null)
            {
                var cred = _credentials.GetCredentials();
                _credentials.AccessToken = cred.AccessToken;
                _credentials.ClientID = cred.ClientID;
                _credentials.ClientSecret = cred.ClientSecret;
                _credentials.OrgTitle = cred.OrgTitle;
                _credentials.Environment = cred.Environment;
                _credentials.ProjectKey = cred.ProjectKey;
            }

            if (string.IsNullOrEmpty(_credentials.OrgTitle) ||
                string.IsNullOrEmpty(_credentials.Environment) ||
                string.IsNullOrEmpty(_credentials.ProjectKey))
            {
                throw new ARTAuthenticationException("OrgTitle, Environment and ProjectKey are required");
            }

            // Try refresh token first
            var refreshInfo = GetRefreshTokenExpiryInfo(_authData.RefreshToken);
            if (!refreshInfo.expired)
                return await RefreshAuthToken();

            return await GenerateAuthToken();
        }

        public AuthData GetAuthData() => _authData;
        public AuthenticationConfig GetCredentials() => _credentials;

        private async Task<AuthData> GenerateAuthToken()
        {
            if (string.IsNullOrEmpty(_credentials.AccessToken))
            {
                if (string.IsNullOrEmpty(_credentials.ClientID) || string.IsNullOrEmpty(_credentials.ClientSecret))
                    throw new ARTAuthenticationException("ClientID and ClientSecret required when AccessToken is absent");
            }

            var url = $"{ArtConstants.BASE_URL}/auth/token";
            using var request = new UnityWebRequest(url, "POST");
            request.downloadHandler = new DownloadHandlerBuffer();

            request.SetRequestHeader("Client-Id", _credentials.ClientID);
            request.SetRequestHeader("Client-Secret", _credentials.ClientSecret);
            request.SetRequestHeader("X-Org", _credentials.OrgTitle);
            request.SetRequestHeader("Environment", _credentials.Environment);
            request.SetRequestHeader("ProjectKey", _credentials.ProjectKey);

            if (!string.IsNullOrEmpty(_credentials.AccessToken))
                request.SetRequestHeader("T-pass", _credentials.AccessToken);
            if (_credentials.Config?.AuthToken != null)
                request.SetRequestHeader("X-pass", _credentials.Config.AuthToken);

            var op = request.SendWebRequest();
            while (!op.isDone) await Task.Yield();

            if (request.result != UnityWebRequest.Result.Success)
                throw new ARTAuthenticationException(request.error);

            var json = JObject.Parse(request.downloadHandler.text);
            var tokenData = json["data"] as JObject;
            if (tokenData == null)
                throw new ARTAuthenticationException("Unexpected token response shape");

            _authData = new AuthData
            {
                AccessToken = tokenData["access_token"]?.ToString() ?? "",
                RefreshToken = tokenData["refresh_token"]?.ToString() ?? ""
            };
            return _authData;
        }

        private async Task<AuthData> RefreshAuthToken()
        {
            if (string.IsNullOrEmpty(_credentials.AccessToken) && string.IsNullOrEmpty(_credentials.ClientID))
                throw new ARTAuthenticationException("ClientID required when AccessToken is absent");

            var url = $"{ArtConstants.BASE_URL}/auth/token/refresh";
            var body = JsonConvert.SerializeObject(new { refresh_token = _authData.RefreshToken });
            var bodyBytes = Encoding.UTF8.GetBytes(body);

            using var request = new UnityWebRequest(url, "POST");
            request.uploadHandler = new UploadHandlerRaw(bodyBytes);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Client-Id", _credentials.ClientID);
            request.SetRequestHeader("X-Org", _credentials.OrgTitle);
            request.SetRequestHeader("Environment", _credentials.Environment);
            request.SetRequestHeader("ProjectKey", _credentials.ProjectKey);

            var op = request.SendWebRequest();
            while (!op.isDone) await Task.Yield();

            if (request.responseCode == 500)
            {
                try
                {
                    var errJson = JObject.Parse(request.downloadHandler.text);
                    if (errJson["error"]?.ToString() == "Failed to get WebSocket backend")
                        throw new ARTServerException(errJson["error"].ToString());
                }
                catch (ARTServerException) { throw; }
                catch { }
            }

            if (request.result != UnityWebRequest.Result.Success)
                throw new ARTAuthenticationException(request.error);

            var json = JObject.Parse(request.downloadHandler.text);
            var tokenData = json["data"] as JObject;
            if (tokenData == null)
                throw new ARTAuthenticationException("Unexpected refresh response shape");

            _authData = new AuthData
            {
                AccessToken = tokenData["access_token"]?.ToString() ?? "",
                RefreshToken = tokenData["refresh_token"]?.ToString() ?? ""
            };
            return _authData;
        }

        private bool IsTokenExpired(string token)
        {
            if (string.IsNullOrEmpty(token)) return true;
            try
            {
                var payload = DecodeJWTPayload(token);
                if (payload.TryGetValue("exp", out var expObj) && double.TryParse(expObj.ToString(), out var exp))
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    return exp < (now - 100);
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
