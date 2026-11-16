using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace ART.ADK
{
    /// <summary>
    /// ADK connection state.
    /// </summary>
    public enum AdkState { Paused, Connected, Connecting, Stopped, Retrying }

    /// <summary>Kotlin: AdkPluginContext</summary>
    public interface IAdkPluginContext
    {
        Task<BaseSubscription> Subscribe(string channel);
        Task<JObject> Call(string endpoint, CallApiProps options = null);
        CredentialStore GetCredentials();
        string BaseUrl();
    }

    /// <summary>Kotlin: AdkPlugin&lt;N, T&gt;</summary>
    public interface IAdkPlugin<T>
    {
        string Name { get; }
        T Install(IAdkPluginContext ctx);
    }

    /// <summary>
    /// Main entry point for the ART ADK SDK.
    /// Initialize with an AdkConfig, call Connect() to establish a WebSocket connection,
    /// then Subscribe to channels, Push messages, and Listen for events.
    /// </summary>
    public class Adk
    {
        // ---- State ----
        public Socket SocketInstance { get; }
        private int _reconnectAttempts;
        private readonly int _maxReconnectAttempts = 5;
        private double _reconnectDelay = 3000; // ms
        private readonly double _maxDelay = 5000;
        public KeyPairType MyKeyPair { get; set; }
        private AdkConfig _adkConfig;
        private volatile bool _isPaused;
        private volatile bool _isConnectable;
        private volatile bool _isLimitExceeded;
        private CancellationTokenSource _reconnectCts;
        private Task _connectionAttempt;
        private readonly Dictionary<string, object> _plugins = new Dictionary<string, object>();
        private readonly List<(string evt, string id)> _lifecycleHandlers = new List<(string, string)>();
        public AdkState State { get; private set; } = AdkState.Stopped;

        /// <summary>Fired when the connection is established.</summary>
        public event Action<ConnectionDetail> OnConnected;
        /// <summary>Fired when the connection is closed.</summary>
        public event Action OnClosed;
        /// <summary>Fired on connection error.</summary>
        public event Action<object> OnError;
        public event Action<object> OnLimitExceeded;

        private CredentialStore credentialData;

        /// <summary>
        /// Create a new Adk instance.
        /// </summary>
        /// <param name="config">SDK configuration with URI and credentials.</param>
        public Adk(AdkConfig config = null)
        {
            MainThreadDispatcher.Initialize();

            var rawUrl = config?.Uri ?? "";
            ArtConstants.BASE_URL = $"https://{rawUrl}";
            ArtConstants.WS_URL = $"wss://{rawUrl}/v1/connect";
            ArtConstants.SSE_URL = $"https://{rawUrl}/v1/connect/sse";
            ArtConstants.LPOLL = $"https://{rawUrl}/v1/connect/longpoll";

            _adkConfig = config;

            SocketInstance = Socket.GetInstance(
                encrypt: (data, _) => Task.FromResult(data),
                decrypt: (data, _) => Task.FromResult(data));

            // Wire up encryption
            SocketInstance.EncryptFunc = async (data, pubKey) => await Encrypt(data, pubKey);
            SocketInstance.DecryptFunc = async (data, pubKey) => await Decrypt(data, pubKey);

           
            // Connection events
            SocketInstance.On("connection", data =>
            {
                if (data is ConnectionDetail conn)
                    HandleOnConnection(conn);
            });

            SocketInstance.On("close", _ => HandleOnClose());
            SocketInstance.On("error", err => OnError?.Invoke(err));

            SocketInstance.On("limitExceeded", detail =>
            {
                HandleLimitExceeded(detail);
            });

            AttachLifecycle();
        }

        // ---- Lifecycle (mirrors Kotlin attachLifecycle / detachLifecycle) ----
        private void Reg(string evt, Action<object> handler)
        {
            _lifecycleHandlers.Add((evt, SocketInstance.On(evt, handler)));
        }

        private void AttachLifecycle()
        {
            DetachLifecycle();
            Reg("connection", data =>
            {
                if (data is ConnectionDetail conn) HandleOnConnection(conn);
            });
            Reg("close", _ => HandleOnClose());
            Reg("limitExceeded", HandleLimitExceeded);
            Reg("authenticationFailed", HandleAuthenticationFailed);
            Reg("connectionFailed", HandleConnectionFailed);
        }

        private void DetachLifecycle()
        {
            foreach (var (evt, id) in _lifecycleHandlers)
                SocketInstance.Off(evt, id);
            _lifecycleHandlers.Clear();
        }

        private void CancelReconnect()
        {
            var cts = Interlocked.Exchange(ref _reconnectCts, null);
            cts?.Cancel();
        }

        // ---- Connect ----
        /// <summary>Connect to the ART real-time infrastructure.</summary>
        public async Task Connect(ConnectConfig config = null)
        {
            if (_connectionAttempt != null)
            {
                await _connectionAttempt;
                return;
            }
            if (SocketInstance.IsConnectionActive) return;

            AttachLifecycle();
            CancelReconnect();
            _isPaused = false;
            _isLimitExceeded = false;
            _reconnectAttempts = 0;
            _reconnectDelay = 3000;
            State = AdkState.Connecting;

            SocketInstance.BeginConnection(_adkConfig?.ConnectionTimeoutMs ?? 30_000L);

            if (_adkConfig?.AutoLoadCredsFromJSON == true)
            {
                credentialData = await LoadConfig();
            }

            _isConnectable = true;
            await AttemptConnection();

            if (SocketInstance.IsConnectionActive)
                State = AdkState.Connected;
        }

        // Dedups concurrent connection attempts onto a single in-flight task.
        private Task AttemptConnection()
        {
            if (_connectionAttempt != null) return _connectionAttempt;

            var attempt = AttemptConnectionCore();
            _connectionAttempt = attempt;
            _ = attempt.ContinueWith(t =>
            {
                if (ReferenceEquals(_connectionAttempt, attempt)) _connectionAttempt = null;
                var _ = t.Exception; // observe
            }, TaskContinuationOptions.ExecuteSynchronously);
            return attempt;
        }

        private async Task AttemptConnectionCore()
        {
            var a = InitiateSocketConnection();
            var b = SocketInstance.WaitForConnection();
            try
            {
                await a;
                await b;
            }
            catch
            {
                _ = b.ContinueWith(t => { var _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                throw;
            }
        }

        // ---- Pause ----
        /// <summary>Pause the connection (keeps state, stops reconnection and closes the socket).</summary>
        public void Pause()
        {
            if (_isPaused) return;
            _isPaused = true;
            _isConnectable = false;
            CancelReconnect();
            // stop any reconnection loop
            _reconnectAttempts = _maxReconnectAttempts;
            // close the socket
            SocketInstance.FailConnection(new ConnectionError("The connection is paused.", "CONNECTION_PAUSED"));
            State = AdkState.Paused;
        }

        // ---- Resume ----
        /// <summary>Resume a paused connection.</summary>
        public async Task Resume()
        {
            if (!_isPaused) return;
            await Connect();
        }

        // ---- Disconnect ----
        /// <summary>Gracefully disconnect from the ART infrastructure.</summary>
        public async Task Disconnect()
        {
            _isConnectable = false;
            CancelReconnect();
            SocketInstance.FailConnection(new ConnectionError("The connection was closed.", "CONNECTION_CLOSED"));
            // stop any further reconnection attempts
            _reconnectAttempts = _maxReconnectAttempts;
            // close the underlying WebSocket
            await SocketInstance.CloseWebSocket(clearConnection: true);
            // reset internal state so you can reconnect later if needed
            SocketInstance.IsConnectionActive = false;
            SocketInstance.PendingSendMessages.Clear();
            DetachLifecycle();
            State = AdkState.Stopped;
        }

        // ---- GetState ----
        /// <summary>Get the current connection state as a string.</summary>
        public string GetState()
        {
            if (_isPaused) return "paused";
            if (_reconnectAttempts >= _maxReconnectAttempts) return "stopped";
            if (_reconnectAttempts > 0) return "retrying";
            if (SocketInstance.IsConnectionActive) return "connected";
            return "stopped";
        }

        // ---- Event Hooks ----
        /// <summary>Register an event handler.</summary>
        public string On(string eventName, Action<object> handler)
        {
            return SocketInstance.On(eventName, handler);
        }

        /// <summary>Remove an event handler.</summary>
        public void Off(string eventName, string id)
        {
            SocketInstance.Off(eventName, id);
        }

        // ---- Subscribe ----
        /// <summary>Subscribe to a channel. Returns a Subscription or SharedObjectChannel.</summary>
        public async Task<BaseSubscription> Subscribe(string channel)
            => await SocketInstance.Subscribe(channel);

        // ---- Intercept ----
        /// <summary>Register an interceptor for middleware-style message processing.</summary>
        public async Task<Interceptor> Intercept(
            string interceptorName,
            Action<JObject, Action<object>, Action<string>> fn)
            => await SocketInstance.Intercept(interceptorName, fn);

        // ---- Agent / Orchestrator ----
        public Agent Agent(string agentId)
        {
            return new Agent(agentId, SocketInstance);
        }

        public Orchestrator Orchestrator(string orchId)
        {
            return new Orchestrator(orchId, SocketInstance);
        }

        /// <summary>Resolve and manage the authenticated user's profile for a connector. (JS: connector())</summary>
        // public Connector Connector(string connectorId)
        // {
        //     return new Connector(connectorId, SocketInstance);
        // }

        // ---- Plugins ----
        public T Use<T>(IAdkPlugin<T> plugin)
        {
            var api = plugin.Install(new PluginContext(this));
            _plugins[plugin.Name] = api;
            return api;
        }

        /// <summary>Retrieve a previously Use()d plugin's API by name.</summary>
        public T Plugin<T>(string name)
        {
            if (!_plugins.TryGetValue(name, out var api) || api == null)
                throw new InvalidOperationException($"Plugin '{name}' not installed. Call Use(...) first.");
            return (T)api;
        }

        // The slice of this Adk a plugin is handed at install time.
        private class PluginContext : IAdkPluginContext
        {
            private readonly Adk _adk;
            public PluginContext(Adk adk) { _adk = adk; }

            public Task<BaseSubscription> Subscribe(string channel) => _adk.SocketInstance.Subscribe(channel);

            public Task<JObject> Call(string endpoint, CallApiProps options = null) => _adk.Call(endpoint, options);

            public CredentialStore GetCredentials() => Auth.GetInstance().GetCredentials();

            public string BaseUrl()
            {
                try
                {
                    var uri = new Uri(ArtConstants.BASE_URL);
                    var s = $"{uri.Scheme}://{uri.Host}";
                    if (!uri.IsDefaultPort) s += $":{uri.Port}";
                    return s;
                }
                catch
                {
                    return ArtConstants.BASE_URL;
                }
            }
        }

        // ---- Close WebSocket ----
        /// <summary>Close the WebSocket connection (stops reconnection).</summary>
        public void CloseWebSocket()
        {
            _isConnectable = false;
            CancelReconnect();
            SocketInstance.FailConnection(new ConnectionError("The connection was closed.", "CONNECTION_CLOSED"));
            State = AdkState.Stopped;
        }

        // ---- PushForSecureLine ----
        public async Task<object> PushForSecureLine(string eventName, object data, bool listen = false)
            => await SocketInstance.PushForSecureLine(eventName, data, listen);

        // ---- Connection Handlers ----
        private void HandleConnectionFailed(object detail)
        {
            _isConnectable = false;
            CancelReconnect();
            State = AdkState.Stopped;
        }

        private void HandleOnConnection(ConnectionDetail connection)
        {
            CancelReconnect();
            _reconnectAttempts = 0;
            _reconnectDelay = 3000;
            State = AdkState.Connected;

            MainThreadDispatcher.Enqueue(() => OnConnected?.Invoke(connection));

            // Hook so child classes can do something here (JS: onConnectedHook)
            OnConnectedHook(connection);
        }

        /// <summary>
        /// Hook that a subclass can override if needed.
        /// Called whenever a 'connection' event is received.
        /// </summary>
        protected virtual void OnConnectedHook(ConnectionDetail connection) { }

        private void HandleOnClose()
        {
            MainThreadDispatcher.Enqueue(() => OnClosed?.Invoke());

            if (_isLimitExceeded) return; // billing limit hit - never reconnect

            if (_isConnectable && _connectionAttempt == null)
            {
                SocketInstance.IsReConnecting = true;
                HandleReconnection();
            }
            else if (!_isConnectable)
            {
                State = AdkState.Stopped;
            }
        }

        private static string ReadField(object detail, string jsonKey, string propName)
        {
            if (detail is JObject json) return json[jsonKey]?.ToString();
            if (detail == null) return null;
            return detail.GetType().GetProperty(propName)?.GetValue(detail)?.ToString();
        }

        private void HandleLimitExceeded(object detail)
        {
            var code = ReadField(detail, "code", "Code");
            var error = ReadField(detail, "error", "Error");

            var message = code == "CONCURRENT_LIMIT_EXCEEDED"
                ? $"[ART] Concurrent connection limit reached: {error}. All reconnection attempts stopped. Call Connect() again to retry."
                : $"[ART] Billing limit reached: {error}. All reconnection attempts permanently stopped.";

            Debug.LogError(message);

            _isLimitExceeded = true;
            _isConnectable = false;
            CancelReconnect();
            State = AdkState.Stopped;

            MainThreadDispatcher.Enqueue(() => OnLimitExceeded?.Invoke(detail));
        }

        private void HandleAuthenticationFailed(object detail)
        {
            int status = 0;
            var statusStr = ReadField(detail, "status", "Status");
            if (!string.IsNullOrEmpty(statusStr)) int.TryParse(statusStr, out status);
            var error = ReadField(detail, "error", "Error");

            Debug.LogError(
                $"[ART] Authentication rejected{(status != 0 ? $" with HTTP {status}" : "")}: {error}. " +
                "Automatic reconnection has been stopped. Update the credentials and call Connect() again.");

            _isConnectable = false;
            CancelReconnect();
            SocketInstance.IsReConnecting = false;
            State = AdkState.Stopped;
        }

        private void HandleReconnection()
        {
            if (_isLimitExceeded || !_isConnectable || _isPaused || _reconnectCts != null) return;

            if (_reconnectAttempts < _maxReconnectAttempts)
            {
                _reconnectAttempts++;
                State = AdkState.Retrying;
                Debug.Log($"[ART] Attempting to reconnect in {_reconnectDelay / 1000} seconds... (Attempt {_reconnectAttempts})");

                var cts = new CancellationTokenSource();
                _reconnectCts = cts;
                var delayMs = (int)_reconnectDelay;

                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(delayMs, cts.Token); }
                    catch (TaskCanceledException) { return; }

                    if (cts.IsCancellationRequested) return;
                    Interlocked.CompareExchange(ref _reconnectCts, null, cts);

                    if (!_isConnectable || _isPaused || _isLimitExceeded) return;

                    // Run the attempt on the main thread (UnityWebRequest requires it).
                    MainThreadDispatcher.Enqueue(() => { _ = ReconnectAttempt(); });

                    // Exponential backoff, capped at maxDelay
                    _reconnectDelay = Math.Min(_reconnectDelay + 2000, _maxDelay);
                });
            }
            else
            {
                SocketInstance.FailConnection(new ConnectionError(
                    "The connection could not be restored. Please reconnect to check your execution.",
                    "CONNECTION_LOST"));
                State = AdkState.Stopped;
            }
        }

        private async Task ReconnectAttempt()
        {
            try
            {
                await AttemptConnection();
            }
            catch
            {
                if (_isConnectable) HandleReconnection();
            }
        }

        // ---- Socket Connection ----
        private async Task InitiateSocketConnection()
        {
            // Kotlin uses credentialData; the GetCredentials callback is kept as a Unity fallback
            // (used by the example app).
            var creds = credentialData ?? _adkConfig?.GetCredentials?.Invoke();

            if (creds == null)
            {
                var error = new ConnectionError(
                    "Connection credentials are missing. Please sign in again.",
                    "AUTHENTICATION_FAILED");
                SocketInstance.FailConnection(error);
                throw error;
            }

            creds.Config = _adkConfig;
            await SocketInstance.InitiateSocket(creds);
            await SocketInstance.WaitForConnection();
        }

        // ---- Credentials ----
        public void SetCredentials(CredentialStore config)
        {
            credentialData = config;
        }

        /// <summary>
        /// Loads credentials from ArtConstants.CONFIG_JSON_PATH using the same keys as the Kotlin SDK:
        /// Client-ID, Client-Secret, Environment, Org-Title, ProjectKey.
        /// </summary>
        public async Task<CredentialStore> LoadConfig()
        {
            using var request = UnityWebRequest.Get(ArtConstants.CONFIG_JSON_PATH);

            var op = request.SendWebRequest();
            while (!op.isDone) await Task.Yield();

            if (request.result != UnityWebRequest.Result.Success)
                throw new ARTServerException(
                    $"Failed to load configuration from {ArtConstants.CONFIG_JSON_PATH}: {request.error}");

            var json = JObject.Parse(request.downloadHandler.text);

            string Required(string key)
            {
                var v = json[key]?.ToString();
                if (v == null)
                    throw new ARTServerException($"Config JSON is missing required key '{key}'");
                return v;
            }

            return new CredentialStore
            {
                ClientID = Required("Client-ID"),
                ClientSecret = Required("Client-Secret"),
                Environment = Required("Environment"),
                OrgTitle = Required("Org-Title"),
                ProjectKey = Required("ProjectKey")
            };
        }

          // ---- Encryption ----
        /// <summary>Encrypt data for a recipient.</summary>
        public virtual Task<string> Encrypt(string data, string recipientPublicKey)
        {
            if (MyKeyPair == null)
                throw new ARTEncryptionException("Please generate a new key pair or set an existing key pair");
            return Task.FromResult(EncryptionHelper.Encrypt(data, recipientPublicKey, MyKeyPair.PrivateKey));
        }

        /// <summary>Decrypt data from a sender.</summary>
        public virtual Task<string> Decrypt(string data, string senderPublicKey)
        {
            if (MyKeyPair == null)
                throw new ARTDecryptionException("Please generate a new key pair or set an existing key pair");
            return Task.FromResult(EncryptionHelper.Decrypt(data, senderPublicKey, MyKeyPair.PrivateKey));
        }

        /// <summary>
        /// Generate a new encryption key pair and return it. Like the JS SDK, this does NOT store
        /// or upload it: call <see cref="SetKeyPair"/> with the result to start using it.
        /// </summary>
        public async Task<KeyPairType> GenerateKeyPair()
        {
           // return Task.FromResult(EncryptionHelper.GenerateKeyPair());
    
            var keyPair = EncryptionHelper.GenerateKeyPair();
            await SetKeyPair(keyPair);
            return keyPair;
        }

        /// <summary>Set an existing key pair and save its public key to the server.</summary>
        public async Task SetKeyPair(KeyPairType keyPair)
        {
            if (keyPair == null ||
                string.IsNullOrEmpty(keyPair.PublicKey) ||
                string.IsNullOrEmpty(keyPair.PrivateKey))
            {
                throw new ARTEncryptionException(
                    "Invalid KeyPair: PublicKey and PrivateKey must be non-empty strings");
            }

            // Like JS, MyKeyPair is only set once the public key was saved successfully.
            await SavePublicKey(keyPair);
        }

        public async Task<JObject> SavePublicKey(KeyPairType keyPair)
        {
            var auth = Auth.GetInstance();
            await auth.Authenticate();
            var authData = auth.GetAuthData();
            var creds = auth.GetCredentials();

            var url = $"{ArtConstants.BASE_URL}/v1/update-publickey";
            var body = JsonConvert.SerializeObject(new { public_key = keyPair.PublicKey });
            var bodyBytes = Encoding.UTF8.GetBytes(body);

            using var request = new UnityWebRequest(url, "POST");
            request.uploadHandler = new UploadHandlerRaw(bodyBytes);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("X-Org", creds.OrgTitle);
            request.SetRequestHeader("Authorization", $"Bearer {authData.AccessToken}");
            request.SetRequestHeader("Environment", creds.Environment);
            request.SetRequestHeader("ProjectKey", creds.ProjectKey);

            var op = request.SendWebRequest();
            while (!op.isDone) await Task.Yield();

            if (request.responseCode < 200 || request.responseCode >= 300)
                throw new ARTServerException("Error updating keypair");

            var text = request.downloadHandler.text;
            if (string.IsNullOrEmpty(text))
                throw new ARTServerException("Empty response body");

            var json = JObject.Parse(text);
            MyKeyPair = keyPair;
            return json;
        }

        // ---- Profile ----
        /// <summary>Update the authenticated user's profile (all fields optional).</summary>
        public async Task UpdateProfile(UpdateProfileData data)
        {
            var payload = new JObject();
            if (data.FirstName != null) payload["first_name"] = data.FirstName;
            if (data.LastName != null) payload["last_name"] = data.LastName;
            if (data.Email != null) payload["email"] = data.Email;
            if (data.Attributes != null) payload["attributes"] = JObject.FromObject(data.Attributes);

            await Call("/v1/update-profile", new CallApiProps { Method = "POST", Payload = payload });
        }

        // ---- REST API Call ----
        /// <summary>
        /// Make an authenticated REST API call to the ART backend.
        /// </summary>
        public async Task<JObject> Call(string endpoint, CallApiProps options = null)
        {
            options ??= new CallApiProps();

            var auth = Auth.GetInstance();
            await auth.Authenticate();
            var accessToken = auth.GetAuthData().AccessToken;
            var creds = auth.GetCredentials();

            var urlStr = $"{options.BaseUrl ?? ArtConstants.BASE_URL}{endpoint}";
            if (options.QueryParams != null && options.QueryParams.Count > 0)
            {
                var qs = new List<string>();
                foreach (var kv in options.QueryParams)
                    qs.Add($"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}");
                urlStr += "?" + string.Join("&", qs);
            }

            var headers = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {accessToken}",
                ["Accept"] = "application/json",
                ["X-Org"] = creds.OrgTitle,
                ["Environment"] = creds.Environment,
                ["ProjectKey"] = creds.ProjectKey
            };
            if (options.Headers != null)
            {
                foreach (var kv in options.Headers) headers[kv.Key] = kv.Value;
            }

            var method = (options.Method ?? "GET").ToUpperInvariant();

            using var request = new UnityWebRequest(urlStr, method);
            request.downloadHandler = new DownloadHandlerBuffer();
            if (options.TimeoutMs.HasValue)
                request.timeout = (int)Math.Ceiling(options.TimeoutMs.Value / 1000.0);

            if (options.Payload != null)
            {
                headers["Content-Type"] = "application/json";
                if (method != "GET")
                {
                    var payloadStr = JsonConvert.SerializeObject(options.Payload);
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payloadStr));
                }
            }

            foreach (var kv in headers) request.SetRequestHeader(kv.Key, kv.Value);

            var op = request.SendWebRequest();
            while (!op.isDone) await Task.Yield();

            var code = request.responseCode;
            if (code < 200 || code >= 300)
            {
                string msg;
                try
                {
                    var errJson = JObject.Parse(request.downloadHandler.text);
                    var m = errJson["message"]?.ToString();
                    msg = string.IsNullOrEmpty(m) ? errJson.ToString(Formatting.None) : m;
                }
                catch { msg = request.error; }
                throw new ARTServerException($"API {endpoint} failed: {msg}");
            }

            if (code == 204) return new JObject();

            var text = request.downloadHandler.text;
            if (string.IsNullOrEmpty(text))
                throw new ARTServerException("Empty response body");

            return JObject.Parse(text);
        }

        /// <summary>Typed variant of Call (Kotlin: call&lt;T&gt;(endpoint, options, responseType)).</summary>
        public async Task<T> Call<T>(string endpoint, CallApiProps options = null)
        {
            var obj = await Call(endpoint, options);
            return obj.ToObject<T>();
        }

        // ---- Storage (JS: upload / listFiles / getFile / deleteFile) ----
        // ASSUMPTION: the Unity Storage class mirrors the JS one (see notes in the reply).
        private readonly Storage _storage = new Storage();

        public Task<FileRef> Upload(byte[] file, UploadOptions opts = null)
            => _storage.Upload(file, opts ?? new UploadOptions());

        public Task<ListFilesResult> ListFiles(ListOptions opts = null)
            => _storage.ListFiles(opts ?? new ListOptions());

        public Task<StorageFile> GetFile(string fileId, FileAccessOptions opts)
            => _storage.GetFile(fileId, opts);

        public Task DeleteFile(string fileId, bool hard = false, CancellationToken signal = default, long? timeoutMs = null)
            => _storage.DeleteFile(fileId, hard, signal, timeoutMs);
    }
}