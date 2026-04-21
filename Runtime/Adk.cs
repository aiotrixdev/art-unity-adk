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
    public enum AdkState { Paused, Connected, Connecting, Stopped }

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
        private bool _isPaused;
        private bool _isConnectable;
        private CancellationTokenSource _reconnectCts;
        public AdkState State { get; private set; } = AdkState.Stopped;

        /// <summary>Fired when the connection is established.</summary>
        public event Action<ConnectionDetail> OnConnected;
        /// <summary>Fired when the connection is closed.</summary>
        public event Action OnClosed;
        /// <summary>Fired on connection error.</summary>
        public event Action<object> OnError;

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
        }

        // ---- Connect ----
        /// <summary>Connect to the ART real-time infrastructure.</summary>
        public async Task Connect(ConnectConfig config = null)
        {
            _isConnectable = true;
            State = AdkState.Connecting;
            await InitiateSocketConnection();
            State = AdkState.Connected;
        }

        // ---- Pause ----
        /// <summary>Pause the connection (keeps state, stops WebSocket).</summary>
        public async Task Pause()
        {
            if (_isPaused) return;
            _isPaused = true;
            _reconnectAttempts = _maxReconnectAttempts;
            await SocketInstance.CloseWebSocket();
            State = AdkState.Paused;
        }

        // ---- Resume ----
        /// <summary>Resume a paused connection.</summary>
        public async Task Resume()
        {
            if (!_isPaused) return;
            _isPaused = false;
            _reconnectAttempts = 0;
            _reconnectDelay = 3000;
            State = AdkState.Connecting;
            await SocketInstance.ConnectWebSocket();
            State = AdkState.Connected;
        }

        // ---- Disconnect ----
        /// <summary>Gracefully disconnect from the ART infrastructure.</summary>
        public async Task Disconnect()
        {
            _isConnectable = false;
            _reconnectAttempts = _maxReconnectAttempts;
            _reconnectCts?.Cancel();
            await SocketInstance.CloseWebSocket(clearConnection: true);
            State = AdkState.Stopped;
            SocketInstance.IsConnectionActive = false;
            SocketInstance.PendingSendMessages.Clear();
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
            => SocketInstance.On(eventName, handler);

        /// <summary>Remove an event handler.</summary>
        public void Off(string eventName, string id)
            => SocketInstance.Off(eventName, id);

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

        // ---- Close WebSocket ----
        public async Task CloseWebSocket()
            => await SocketInstance.CloseWebSocket();

        // ---- PushForSecureLine ----
        public async Task<object> PushForSecureLine(string eventName, object data, bool listen = false)
            => await SocketInstance.PushForSecureLine(eventName, data, listen);

        // ---- Connection Handlers ----
        private void HandleOnConnection(ConnectionDetail connection)
        {
            _reconnectAttempts = 0;
            _reconnectDelay = 3000;
            MainThreadDispatcher.Enqueue(() => OnConnected?.Invoke(connection));
        }

        private void HandleOnClose()
        {
            if (!_isConnectable) return;
            SocketInstance.IsReConnecting = true;
            MainThreadDispatcher.Enqueue(() => OnClosed?.Invoke());
            HandleReconnection();
        }

        private void HandleReconnection()
        {
            _reconnectCts?.Cancel();
            _reconnectCts = new CancellationTokenSource();
            var ct = _reconnectCts.Token;

            _ = Task.Run(async () =>
            {
                if (_reconnectAttempts < _maxReconnectAttempts)
                {
                    _reconnectAttempts++;
                    Debug.Log($"[ART] Reconnecting in {_reconnectDelay / 1000}s (attempt {_reconnectAttempts})");
                    await Task.Delay((int)_reconnectDelay, ct);
                    if (ct.IsCancellationRequested) return;
                    await Connect();
                    _reconnectDelay = Math.Min(_reconnectDelay + 2000, _maxDelay);
                }
                else
                {
                    Debug.Log($"[ART] Max reconnect attempts reached. Retrying in {_maxDelay / 1000}s");
                    await Task.Delay((int)_maxDelay, ct);
                    if (ct.IsCancellationRequested) return;
                    await Connect();
                }
            });
        }

        // ---- Socket Connection ----
        private async Task InitiateSocketConnection()
        {
            AuthenticationConfig authConfig;

            if (_adkConfig?.GetCredentials != null)
            {
                var store = _adkConfig.GetCredentials();
                authConfig = new AuthenticationConfig
                {
                    Environment = store.Environment,
                    ProjectKey = store.ProjectKey,
                    OrgTitle = store.OrgTitle,
                    ClientID = store.ClientID,
                    ClientSecret = store.ClientSecret,
                    AccessToken = store.AccessToken
                };
            }
            else
            {
                authConfig = new AuthenticationConfig();
            }

            authConfig.Config = _adkConfig;
            authConfig.GetCredentials = _adkConfig?.GetCredentials;
            await SocketInstance.InitiateSocket(authConfig);
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

        /// <summary>Generate a new encryption key pair and save it to the server.</summary>
        public async Task<KeyPairType> GenerateKeyPair()
        {
            var keyPair = EncryptionHelper.GenerateKeyPair();
            await SetKeyPair(keyPair);
            return keyPair;
        }

        /// <summary>Set an existing key pair and save it to the server.</summary>
        public async Task SetKeyPair(KeyPairType keyPair)
        { 
            Debug.Log($"Generated key value pair...public key...'{keyPair.PublicKey}'");
            Debug.Log($"Generated key value pair...private key...'{keyPair.PrivateKey}'");

            if (string.IsNullOrEmpty(keyPair.PublicKey) || string.IsNullOrEmpty(keyPair.PrivateKey))
                throw new ARTEncryptionException("Invalid KeyPair: keys must be non-empty strings");
            await SavePublicKey(keyPair);
        }

        private async Task SavePublicKey(KeyPairType keyPair)
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
            request.SetRequestHeader("Authorization", $"Bearer {authData.AccessToken}");
            request.SetRequestHeader("X-Org", creds.OrgTitle);
            request.SetRequestHeader("Environment", creds.Environment);
            request.SetRequestHeader("ProjectKey", creds.ProjectKey);

            var op = request.SendWebRequest();
            while (!op.isDone) await Task.Yield();

            Debug.Log($"[ART] UpdatePublicKey Response ({request.responseCode}): {request.downloadHandler.text}");

            if (request.responseCode != 200)
                throw new ARTServerException($"Error updating keypair: {request.downloadHandler.text}");

            MyKeyPair = keyPair;
        }

        // ---- REST API Call ----
        /// <summary>
        /// Make an authenticated REST API call to the ART backend.
        /// </summary>
        public async Task<JObject> Call(string endpoint, CallApiOptions options = null)
        {
            options ??= new CallApiOptions();

            var auth = Auth.GetInstance();
            await auth.Authenticate();
            var authData = auth.GetAuthData();
            var creds = auth.GetCredentials();

            var urlStr = $"{ArtConstants.BASE_URL}{endpoint}";
            if (options.QueryParams != null && options.QueryParams.Count > 0)
            {
                var qs = new List<string>();
                foreach (var kv in options.QueryParams)
                    qs.Add($"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}");
                urlStr += "?" + string.Join("&", qs);
            }

            using var request = new UnityWebRequest(urlStr, options.Method.ToUpper());
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Authorization", $"Bearer {authData.AccessToken}");
            request.SetRequestHeader("Accept", "application/json");
            request.SetRequestHeader("X-Org", creds.OrgTitle);
            request.SetRequestHeader("Environment", creds.Environment);
            request.SetRequestHeader("ProjectKey", creds.ProjectKey);

            if (options.Headers != null)
            {
                foreach (var kv in options.Headers)
                    request.SetRequestHeader(kv.Key, kv.Value);
            }

            if (options.Payload != null)
            {
                var payloadStr = JsonConvert.SerializeObject(options.Payload);
                var payloadBytes = Encoding.UTF8.GetBytes(payloadStr);
                request.uploadHandler = new UploadHandlerRaw(payloadBytes);
                request.SetRequestHeader("Content-Type", "application/json");
            }

            var op = request.SendWebRequest();
            while (!op.isDone) await Task.Yield();

            if (request.responseCode == 204) return new JObject();

            if (request.responseCode < 200 || request.responseCode >= 300)
            {
                string msg;
                try
                {
                    var errJson = JObject.Parse(request.downloadHandler.text);
                    msg = errJson["message"]?.ToString() ?? request.error;
                }
                catch { msg = request.error; }
                throw new ARTServerException($"API {endpoint} failed: {msg}");
            }

            return JObject.Parse(request.downloadHandler.text);
        }
    }
}
