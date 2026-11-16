using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
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
    /// Core WebSocket handler. Manages the connection lifecycle, message routing,
    /// heartbeat, authorization refresh and fallback to long-polling.
    /// Mirrors <c>websocket/socket.ts</c>. Uses System.Net.WebSockets.ClientWebSocket
    /// (works on all platforms except WebGL). The JS SSE fallback is not ported
    /// (Unity has no EventSource); the fallback chain is WebSocket -> long-poll.
    /// </summary>
    public sealed class Socket : IWebSocketHandler
    {
        // Singleton
        private static Socket _instance;
        private static readonly object _singletonLock = new object();

        // Shared-state lock (subscriptions, interceptors, pending buffers, failure state)
        private readonly object _stateLock = new object();

        // WebSocket state
        private ClientWebSocket _ws;
        private CancellationTokenSource _wsCts;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private CredentialStore _credentials = new CredentialStore();
        private readonly Dictionary<string, BaseSubscription> _subscriptions = new Dictionary<string, BaseSubscription>();
        private readonly Dictionary<string, Interceptor> _interceptors = new Dictionary<string, Interceptor>();
        private ConnectionDetail _connection;
        public bool IsConnectionActive { get; set; }
        private CancellationTokenSource _heartbeatCts;
        public List<string> PendingSendMessages { get; set; } = new List<string>();
        public Dictionary<string, Action<object>> SecureCallbacks { get; } = new Dictionary<string, Action<object>>();
        private readonly Dictionary<string, List<(string evt, JObject payload)>> _pendingIncomingMessages
            = new Dictionary<string, List<(string, JObject)>>();

        // Encrypt/Decrypt delegates
        public Func<string, string, Task<string>> EncryptFunc { get; set; }
        public Func<string, string, Task<string>> DecryptFunc { get; set; }

        private bool _isConnecting;
        public bool IsReConnecting { get; set; }
        private bool _autoReconnect;

        // Connection lifecycle (JS: connectionFailure / connectionGeneration / connectionTimeoutMs)
        private ConnectionError _connectionFailure;
        private int _connectionGeneration;
        private long _connectionTimeoutMs = 30_000L;
        private string _authorizedAccessToken = "";
        private Task _authorizationRefreshInFlight;

        // Long poll client
        private readonly LongPollClient _lpClient;
        private string _pullSource = "socket"; // "socket" | "http"
        private string _pushSource = "socket"; // "socket" | "http"

        // Event emitter ("open", "close", "error", "connection", "limitExceeded",
        // "authenticationFailed", "connectionFailed")
        private readonly EventEmitter _emitter = new EventEmitter();

        // Connection waiters
        private readonly List<TaskCompletionSource<bool>> _connectionWaiters = new List<TaskCompletionSource<bool>>();
        private readonly object _waiterLock = new object();

        // Connection failure listeners (also surfaced as the "connectionFailed" event)
        private readonly List<Action<ConnectionError>> _failureListeners = new List<Action<ConnectionError>>();

        private Socket(
            Func<string, string, Task<string>> encrypt,
            Func<string, string, Task<string>> decrypt)
        {
            EncryptFunc = encrypt;
            DecryptFunc = decrypt;

            _lpClient = new LongPollClient(new LongPollOptions
            {
                Endpoint = ArtConstants.LPOLL,
                GetAuthHeaders = async () =>
                {
                    var auth = Auth.GetInstance();
                    var authData = await auth.Authenticate();
                    var creds = auth.GetCredentials();
                    return new Dictionary<string, string>
                    {
                        { "Authorization", $"Bearer {authData.AccessToken}" },
                        { "X-Org", creds.OrgTitle },
                        { "Environment", creds.Environment },
                        { "ProjectKey", creds.ProjectKey }
                    };
                },
                OnMessages = messages => ProcessIncomingMessages(messages),
                OnError = err =>
                {
                    if (AuthErrors.IsTerminalAuthError(err)) StopAfterAuthenticationFailure(new Exception(err));
                    else Debug.LogError($"[ART] LP error: {err}");
                }
            });
        }

        public static Socket GetInstance(
            Func<string, string, Task<string>> encrypt,
            Func<string, string, Task<string>> decrypt)
        {
            lock (_singletonLock)
            {
                if (_instance == null)
                    _instance = new Socket(encrypt, decrypt);
                return _instance;
            }
        }

        // =====================================================================
        // Connection lifecycle (JS: beginConnection / failConnection / on|offConnectionFailure)
        // =====================================================================

        /// <summary>Starts a new connection cycle: clears any previous failure and sets the wait timeout.</summary>
        public void BeginConnection(long timeoutMs = 30_000L)
        {
            if (timeoutMs <= 0) throw new ArgumentException("ConnectionTimeoutMs must be positive");
            _connectionTimeoutMs = timeoutMs;
            lock (_stateLock) { _connectionFailure = null; }
            Interlocked.Increment(ref _connectionGeneration);
        }

        /// <summary>
        /// Fails the connection: stops everything, rejects all waiters and listeners and closes the socket.
        /// Only the first failure of a cycle is applied.
        /// </summary>
        public void FailConnection(ConnectionError error)
        {
            lock (_stateLock)
            {
                if (_connectionFailure != null) return;
                _connectionFailure = error;
            }

            Interlocked.Increment(ref _connectionGeneration);
            IsConnectionActive = false;
            _isConnecting = false;
            IsReConnecting = false;
            _autoReconnect = false;
            PendingSendMessages = new List<string>();
            try { _lpClient.Stop(); } catch { }

            // Reject everything waiting for the connection.
            List<TaskCompletionSource<bool>> waiters;
            lock (_waiterLock)
            {
                waiters = new List<TaskCompletionSource<bool>>(_connectionWaiters);
                _connectionWaiters.Clear();
            }
            foreach (var w in waiters) w.TrySetException(error);

            // Notify failure listeners + the "connectionFailed" event.
            List<Action<ConnectionError>> listeners;
            lock (_failureListeners) { listeners = new List<Action<ConnectionError>>(_failureListeners); }
            foreach (var l in listeners)
            {
                try { l(error); } catch (Exception ex) { Debug.LogException(ex); }
            }
            _emitter.Emit("connectionFailed", error);

            _ = CloseWebSocket().ContinueWith(t => { var _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        public void OnConnectionFailure(Action<ConnectionError> listener)
        {
            lock (_failureListeners) { _failureListeners.Add(listener); }
        }

        public void OffConnectionFailure(Action<ConnectionError> listener)
        {
            lock (_failureListeners) { _failureListeners.Remove(listener); }
        }

        private int CurrentGeneration => Volatile.Read(ref _connectionGeneration);

        // The failure if there is one, otherwise the fallback (JS: `this.connectionFailure || err`).
        private Exception FailureOr(Exception fallback) => (Exception)_connectionFailure ?? fallback;

        private static ConnectionError CancelledError() =>
            new ConnectionError("Connection attempt cancelled.", "CONNECTION_CANCELLED");

        // =====================================================================
        // InitiateSocket / ConnectWebSocket
        // =====================================================================

        internal async Task InitiateSocket(CredentialStore credentials)
        {
            var generation = CurrentGeneration;
            if (_connectionFailure != null) throw _connectionFailure;
            if (_ws != null && IsConnectionActive) return;

            _credentials = credentials;

            // 1) WebSocket
            try
            {
                await ConnectWebSocket();
                _pullSource = "socket";
                _pushSource = "socket";
                return;
            }
            catch (Exception wsErr)
            {
                if (generation != CurrentGeneration) throw FailureOr(wsErr);
                if (_connectionFailure != null) throw _connectionFailure;
                Debug.LogWarning($"[ART] WebSocket failed, falling back to LongPoll: {wsErr.Message}");
            }

            // 2) Long poll (JS also tries SSE here; not available in Unity)
            _pullSource = "http";
            _pushSource = "http";
            _lpClient.Start(_connection?.ConnectionId);
        }

        public async Task ConnectWebSocket()
        {
            var generation = CurrentGeneration;
            try
            {
                if (_connectionFailure != null) throw _connectionFailure;
                if (_isConnecting) return;
                _isConnecting = true;

                var auth = Auth.GetInstance(_credentials);

                AuthData authData;
                try
                {
                    authData = await auth.Authenticate(forceAuth: IsReConnecting);
                }
                catch (Exception err)
                {
                    if (generation != CurrentGeneration) throw FailureOr(err);
                    Debug.LogError($"[ART] Authentication failed: {err.Message}");
                    _isConnecting = false;
                    // Authentication is not a transport outage. A failed forced refresh during
                    // reconnect must end that reconnect cycle instead of retrying forever.
                    if (IsReConnecting || AuthErrors.IsTerminalAuthError(err))
                    {
                        StopAfterAuthenticationFailure(err);
                        throw _connectionFailure;
                    }
                    throw;
                }
                if (generation != CurrentGeneration) throw FailureOr(CancelledError());

                var wsUrl = $"{ArtConstants.WS_URL}" +
                    $"?connection_id={Uri.EscapeDataString(_connection?.ConnectionId ?? "")}" +
                    $"&Org-Title={Uri.EscapeDataString(_credentials.OrgTitle)}" +
                    $"&token={Uri.EscapeDataString(authData.AccessToken)}" +
                    $"&environment={Uri.EscapeDataString(_credentials.Environment)}" +
                    $"&project-key={Uri.EscapeDataString(_credentials.ProjectKey)}";

                await SafeClose();
                if (generation != CurrentGeneration) throw FailureOr(CancelledError());

                var cts = new CancellationTokenSource();
                _wsCts = cts;
                var ws = new ClientWebSocket();
                _ws = ws;

                try
                {
                    var connectTask = ws.ConnectAsync(new Uri(wsUrl), cts.Token);
                    var completed = await Task.WhenAny(connectTask, Task.Delay(5000));

                    if (completed != connectTask)
                    {
                        cts.Cancel();
                        DiscardSocket(ws);
                        throw new ARTTimeoutException("WebSocket handshake timeout");
                    }

                    await connectTask; // propagate any exception

                    if (generation != CurrentGeneration)
                    {
                        DiscardSocket(ws);
                        throw FailureOr(CancelledError());
                    }

                    _isConnecting = false;
                    _authorizedAccessToken = authData.AccessToken;
                    _emitter.Emit("open");
                    _ = StartReceiveLoop(ws, cts.Token, generation);
                }
                catch (ARTTimeoutException) { throw; }
                catch (ConnectionError) { throw; }
                catch (Exception ex)
                {
                    DiscardSocket(ws);
                    _isConnecting = false;
                    if (generation == CurrentGeneration) _emitter.Emit("close", ex);
                    throw new ARTException($"WebSocket connection failed: {ex.Message}");
                }
            }
            catch
            {
                if (generation == CurrentGeneration)
                {
                    _isConnecting = false;
                    IsConnectionActive = false;
                }
                throw;
            }
        }

        private void DiscardSocket(ClientWebSocket ws)
        {
            if (ReferenceEquals(_ws, ws)) _ws = null;
            try { ws.Dispose(); } catch { }
        }

        private async Task SafeClose()
        {
            var ws = _ws;
            if (ws == null) return;
            _ws = null; // detach first so the receive loop ignores its own close

            try
            {
                if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                {
                    using var cts = new CancellationTokenSource(1000);
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", cts.Token);
                }
            }
            catch { }
            finally
            {
                try { ws.Dispose(); } catch { }
            }
        }

        // =====================================================================
        // Connection binding (art_ready)
        // =====================================================================

        private void HandleConnectionBinding(string rawData)
        {
            if (_connectionFailure != null) return;
            SetAutoReconnect(true);

            JObject data;
            try { data = JObject.Parse(rawData); }
            catch { return; }

            _connection = new ConnectionDetail
            {
                ConnectionId = data["connection_id"]?.ToString() ?? "",
                InstanceId = data["instance_id"]?.ToString() ?? "",
                TenantName = _credentials.OrgTitle,
                Environment = _credentials.Environment,
                ProjectKey = _credentials.ProjectKey
            };

            Debug.Log($"[ART] Live connection opened {_connection.ConnectionId}");
            IsConnectionActive = true;
            IsReConnecting = false;
            _emitter.Emit("connection", _connection);
            StartHeartbeat();
            ResolveWaiters();

            if (_autoReconnect)
            {
                List<BaseSubscription> subs;
                List<Interceptor> inters;
                lock (_stateLock)
                {
                    subs = _subscriptions.Values.ToList();
                    inters = _interceptors.Values.ToList();
                }
                foreach (var sub in subs) sub.Reconnect();
                foreach (var inter in inters) inter.Reconnect();
            }
        }

        // =====================================================================
        // Secure line + authorization
        // =====================================================================

        /// <summary>
        /// Sends an event on the reserved art_secure channel. With <paramref name="listen"/> it waits
        /// for the server's reply (rejecting on timeout or connection failure); otherwise returns null.
        /// </summary>
        public async Task<object> PushForSecureLine(string eventName, object data, bool listen = false)
        {
            await WaitForConnection();

            var refId = $"{_connection?.ConnectionId}_secure_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid().ToString("N").Substring(0, 6)}";

            var message = new JObject
            {
                ["from"] = _connection?.ConnectionId ?? "",
                ["channel"] = "art_secure",
                ["event"] = eventName,
                ["content"] = JsonConvert.SerializeObject(data ?? new JObject()),
                ["ref_id"] = refId
            };
            var msgStr = message.ToString(Formatting.None);

            if (!listen)
            {
                try
                {
                    SendMessage(msgStr);
                }
                catch (Exception error)
                {
                    FailConnection(error as ConnectionError ?? new ConnectionError("The connection was lost.", "CONNECTION_LOST"));
                    throw;
                }
                return null;
            }

            var key = $"secure-{refId}";
            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var timerCts = new CancellationTokenSource();
            Action<ConnectionError> onFailure = null;

            void Cleanup()
            {
                timerCts.Cancel();
                if (onFailure != null) OffConnectionFailure(onFailure);
                lock (SecureCallbacks) { SecureCallbacks.Remove(key); }
            }

            onFailure = error => { Cleanup(); tcs.TrySetException(error); };

            _ = Task.Delay((int)Math.Min(_connectionTimeoutMs, int.MaxValue), timerCts.Token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                onFailure(new ConnectionError(
                    "The connection did not respond in time. Please reconnect and try again.", "CONNECTION_TIMEOUT"));
            }, TaskContinuationOptions.ExecuteSynchronously);

            OnConnectionFailure(onFailure);
            lock (SecureCallbacks)
            {
                SecureCallbacks[key] = reply => { Cleanup(); tcs.TrySetResult(reply); };
            }

            try
            {
                SendMessage(msgStr);
            }
            catch (Exception error)
            {
                FailConnection(error as ConnectionError ?? new ConnectionError("The connection was lost.", "CONNECTION_LOST"));
                Cleanup();
                tcs.TrySetException(error);
            }

            return await tcs.Task;
        }

        /// <summary>
        /// Makes sure the connection's access token is current, refreshing the server-side
        /// authorization lease when the token changed.
        /// </summary>
        public async Task EnsureAuthorization()
        {
            await WaitForConnection();

            var inFlight = _authorizationRefreshInFlight;
            if (inFlight != null)
            {
                await inFlight;
                return;
            }

            var auth = Auth.GetInstance(_credentials);
            string accessToken;
            try
            {
                accessToken = (await auth.Authenticate()).AccessToken;
            }
            catch (Exception error)
            {
                StopAfterAuthenticationFailure(error);
                throw _connectionFailure;
            }
            if (_connectionFailure != null) throw _connectionFailure;

            if (accessToken == _authorizedAccessToken) return;

            var pending = RefreshAuthorizationLease(accessToken);
            _authorizationRefreshInFlight = pending;
            try
            {
                await pending;
            }
            finally
            {
                _authorizationRefreshInFlight = null;
            }
        }

        private async Task RefreshAuthorizationLease(string accessToken)
        {
            var connectionId = _connection?.ConnectionId;
            if (string.IsNullOrEmpty(connectionId))
                throw new InvalidOperationException("Connection is not ready for authorization refresh");

            var body = new JObject
            {
                ["channel"] = "art_secure",
                ["event"] = "authorization-refresh",
                ["content"] = "{}",
                ["connection_id"] = connectionId,
                ["to"] = new JArray()
            };

            using var request = new UnityWebRequest($"{ArtConstants.BASE_URL}/v1/push-message", "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body.ToString(Formatting.None)));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Authorization", $"Bearer {accessToken}");
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Accept", "application/json");
            request.SetRequestHeader("X-Org", _credentials.OrgTitle);
            request.SetRequestHeader("Environment", _credentials.Environment);
            request.SetRequestHeader("ProjectKey", _credentials.ProjectKey);

            var res = await SendRequest(request, _connectionTimeoutMs);

            if (res.TimedOut || res.NetworkError)
            {
                throw new ConnectionError(
                    res.TimedOut
                        ? "Connection authorization timed out. Please reconnect."
                        : $"Connection authorization failed: {res.Error ?? "network error"}",
                    "CONNECTION_AUTHORIZATION_FAILED");
            }

            if (res.Status < 200 || res.Status >= 300)
            {
                var json = TryParseObject(res.Text);
                var msg = json?["message"]?.ToString();
                var error = new AuthHttpError(
                    string.IsNullOrEmpty(msg) ? $"HTTP {res.Status} {res.Error}" : msg,
                    (int)res.Status,
                    json);
                if (AuthErrors.IsTerminalAuthError(error)) StopAfterAuthenticationFailure(error);
                throw error;
            }

            _authorizedAccessToken = accessToken;
        }

        private void StopAfterAuthenticationFailure(Exception error)
        {
            int? status = (error as AuthHttpError)?.Status;
            var failure = new ConnectionError(
                string.IsNullOrEmpty(error?.Message) ? "Authentication failed. Please sign in again." : error.Message,
                "AUTHENTICATION_FAILED",
                status);
            FailConnection(failure);

            _emitter.Emit("authenticationFailed", new JObject
            {
                ["status"] = AuthErrors.IsTerminalAuthError(error) ? (status ?? 0) : 0,
                ["error"] = error?.Message ?? "Authentication refresh failed"
            });
        }

        // =====================================================================
        // Subscriptions
        // =====================================================================

        public void RemoveSubscription(string channel)
        {
            lock (_stateLock) { _subscriptions.Remove(channel); }
        }

        public async Task<BaseSubscription> Subscribe(string channel)
        {
            return await HandleSubscription(channel);
        }

        private async Task<BaseSubscription> HandleSubscription(string channel)
        {
            await WaitForConnection();
            await EnsureAuthorization();
            var connectionId = _connection?.ConnectionId ?? "";

            BaseSubscription existing;
            lock (_stateLock) { _subscriptions.TryGetValue(channel, out existing); }
            if (existing != null)
            {
                await existing.SubscribeAsync();
                return existing;
            }

            var channelConfig = await ValidateSubscription(channel, "subscribe");
            if (channelConfig == null)
                throw new ARTChannelNotFoundException(channel);

            BaseSubscription subscription;
            if (channelConfig.ChannelType == "shared-object")
                subscription = new SharedObjectChannel(connectionId, channelConfig, this, "subscribe");
            else
                subscription = new Subscription(connectionId, channelConfig, this, "subscribe");

            lock (_stateLock) { _subscriptions[channel] = subscription; }

            // Replay buffered messages
            foreach (var item in TakeBufferedMessages(channel))
            {
                try { await subscription.HandleMessage(item.evt, item.payload); }
                catch (Exception ex) { Debug.LogException(ex); }
            }

            return subscription;
        }

        // Buffered frames are stored under "channel[:namespace]" (JS key). A subscription made with the
        // plain channel name also receives frames buffered under its namespaced variants.
        private List<(string evt, JObject payload)> TakeBufferedMessages(string channel)
        {
            var result = new List<(string evt, JObject payload)>();
            lock (_stateLock)
            {
                foreach (var key in _pendingIncomingMessages.Keys.ToList())
                {
                    var matches = key == channel ||
                                  (!channel.Contains(":") && key.StartsWith(channel + ":", StringComparison.Ordinal));
                    if (!matches) continue;
                    result.AddRange(_pendingIncomingMessages[key]);
                    _pendingIncomingMessages.Remove(key);
                }
            }
            return result;
        }

        private async Task<ChannelConfig> ValidateSubscription(string channelName, string process)
        {
            if (channelName == "art_config" || channelName == "art_secure" || channelName == "art_notifications")
            {
                return new ChannelConfig
                {
                    ChannelName = channelName,
                    ChannelNamespace = "",
                    ChannelType = "default",
                    PresenceUsers = new List<string>(),
                    SubscriptionID = "",
                    OrchestratorEnabled = false
                };
            }
            return await HelperFunctions.SubscribeToChannel(channelName, process, this);
        }

        public ConnectionDetail GetConnection() => _connection;

        // ---- Intercept ----
        public async Task<Interceptor> Intercept(
            string interceptorName,
            Action<JObject, Action<object>, Action<string>> fn)
        {
            await WaitForConnection();

            lock (_stateLock)
            {
                if (_interceptors.TryGetValue(interceptorName, out var existing))
                    return existing;
            }

            var interception = new Interceptor(interceptorName, fn, this);
            await interception.ValidateInterception();
            lock (_stateLock) { _interceptors[interceptorName] = interception; }
            return interception;
        }

        // =====================================================================
        // Incoming messages
        // =====================================================================

        private void ParseIncomingMessage(string message)
        {
            try
            {
                var token = JToken.Parse(message);
                if (token is JArray arr)
                {
                    foreach (var item in arr)
                    {
                        if (item is JObject obj)
                            HandleIncomingMessage(obj);
                    }
                }
                else if (token is JObject obj)
                {
                    HandleIncomingMessage(obj);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ART] Failed to parse incoming message: {ex.Message}");
            }
        }

        private void ProcessIncomingMessages(List<JObject> messages)
        {
            foreach (var msg in messages)
                HandleIncomingMessage(msg);
        }

        private void HandleIncomingMessage(JObject parsed)
        {
            try
            {
                var channel = parsed["channel"]?.ToString();
                var evt = parsed["event"]?.ToString() ?? "";
                var refId = parsed["ref_id"]?.ToString() ?? "";
                var returnFlag = parsed["return_flag"]?.ToString() ?? "";
                var interceptorName = parsed["interceptor_name"]?.ToString();
                var ns = parsed["namespace"]?.ToString() ?? "";
                var rawData = parsed["content"];

                // art_ready -> connection binding
                if (channel == "art_ready" && evt == "ready")
                {
                    MainThreadDispatcher.Enqueue(() =>
                        HandleConnectionBinding(rawData?.ToString() ?? ""));
                    return;
                }

                // art_secure -> secure callback
                if (channel == "art_secure")
                {
                    var key = $"secure-{refId}";
                    Action<object> cb;
                    lock (SecureCallbacks) { SecureCallbacks.TryGetValue(key, out cb); }
                    if (cb != null)
                    {
                        JToken innerParsed = null;
                        if (rawData != null)
                        {
                            try { innerParsed = JToken.Parse(rawData.ToString()); }
                            catch { }
                        }

                        var result = new JObject
                        {
                            ["channel"] = channel,
                            ["namespace"] = ns,
                            ["data"] = innerParsed,
                            ["ref_id"] = refId,
                            ["event"] = evt
                        };

                        cb(result);
                        lock (SecureCallbacks) { SecureCallbacks.Remove(key); }
                    }
                    return;
                }

                // Limit block: a frame without a channel carrying a limit code.
                var code = parsed["code"]?.ToString();
                if (string.IsNullOrEmpty(channel) &&
                    (code == "CONNECTION_MINUTES_LIMIT_EXCEEDED" || code == "CONCURRENT_LIMIT_EXCEEDED"))
                {
                    var error = parsed["error"]?.ToString();
                    Debug.LogError(
                        $"[ART] {(code == "CONCURRENT_LIMIT_EXCEEDED" ? "Concurrent connection limit reached" : "Connection minutes limit exceeded")}. Reconnection blocked.");
                    _emitter.Emit("limitExceeded", new JObject { ["code"] = code, ["error"] = error });
                    FailConnection(new ConnectionError(
                        string.IsNullOrEmpty(error) ? "Your connection limit has been reached." : error, code));
                    return;
                }

                if (string.IsNullOrEmpty(channel) || (string.IsNullOrEmpty(evt) && returnFlag != "SA"))
                {
                    Debug.LogWarning($"[ART] Received message without channel or event: {parsed}");
                    return;
                }

                if (evt == "shift_to_http")
                {
                    SwitchToHttpPoll();
                    return;
                }

                if (evt == "error")
                {
                    Debug.LogError($"[ART] Received error message: {parsed}");
                }

                // Build payload with "data" field instead of "content"
                var payload = new JObject(parsed);
                payload.Remove("content");
                payload["data"] = rawData;

                // Interceptor routing
                if (!string.IsNullOrEmpty(interceptorName))
                {
                    Interceptor interception;
                    lock (_stateLock) { _interceptors.TryGetValue(interceptorName, out interception); }

                    if (interception != null)
                    {
                        _ = Task.Run(async () =>
                        {
                            try { await interception.HandleMessage(channel, payload); }
                            catch (Exception ex) { Debug.LogException(ex); }
                        });
                    }
                    else
                    {
                        Debug.LogWarning($"[ART] No Interception found for channel: {channel}");
                    }
                    return;
                }

                // Subscription routing - key is "channel[:namespace]" like the JS socket. If nothing is
                // registered under the namespaced key, fall back to the plain channel name.
                var subKey = string.IsNullOrEmpty(ns) ? channel : $"{channel}:{ns}";

                BaseSubscription sub = null;
                lock (_stateLock)
                {
                    if (!_subscriptions.TryGetValue(subKey, out sub) && !string.IsNullOrEmpty(ns))
                        _subscriptions.TryGetValue(channel, out sub);

                    if (sub == null)
                    {
                        Debug.LogWarning($"[ART] No subscription found for channel: {subKey}, adding to buffer");
                        if (!_pendingIncomingMessages.TryGetValue(subKey, out var arr))
                        {
                            arr = new List<(string, JObject)>();
                            _pendingIncomingMessages[subKey] = arr;
                        }
                        arr.Add((evt, payload));
                    }
                }

                if (sub != null)
                {
                    var target = sub;
                    _ = Task.Run(async () =>
                    {
                        try { await target.HandleMessage(evt, payload); }
                        catch (Exception ex) { Debug.LogException(ex); }
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ART] Failed to handle incoming message: {ex.Message}");
            }
        }

        private void SwitchToHttpPoll()
        {
            if (_pullSource == "http") return;
            Debug.Log("[ART] Shifting pullSource -> http, pushSource -> http");
            _pullSource = "http";
            _pushSource = "http";
            _lpClient.Start(_connection?.ConnectionId ?? "");
        }

        // =====================================================================
        // Sending
        // =====================================================================

        /// <summary>
        /// Sends a message. Nothing is ever queued for silent replay after a reconnect: if the
        /// connection is not usable this throws a <see cref="ConnectionError"/>.
        /// </summary>
        public bool SendMessage(string message)
        {
            if (_connectionFailure != null) throw _connectionFailure;

            if (_pushSource == "http")
            {
                var connectionId = _connection?.ConnectionId;
                if (string.IsNullOrEmpty(connectionId) || !IsConnectionActive)
                    throw new ConnectionError("The connection is not ready. Please reconnect.", "CONNECTION_LOST");

                var generation = CurrentGeneration;
                // Dispatch once. A timeout does not establish whether execution happened,
                // so never queue or replay the operation after reconnecting.
                _ = SendViaHttp(message, connectionId, generation).ContinueWith(t =>
                {
                    if (!t.IsFaulted) return;
                    var error = t.Exception.GetBaseException();
                    if (generation != CurrentGeneration) return;
                    FailConnection(error as ConnectionError ?? new ConnectionError(
                        $"Could not send the request: {error.Message}", "CONNECTION_SEND_FAILED"));
                });
                return true;
            }

            var ws = _ws;
            if (ws != null && ws.State == WebSocketState.Open)
            {
                _ = SendOverWebSocket(ws, Encoding.UTF8.GetBytes(message));
                return true;
            }

            // Never queue an execution for silent replay after reconnection.
            throw new ConnectionError(
                "The connection was lost before the message could be sent. Please reconnect.", "CONNECTION_LOST");
        }

        // ClientWebSocket allows a single outstanding SendAsync, so sends are serialized.
        private async Task SendOverWebSocket(ClientWebSocket ws, byte[] bytes)
        {
            await _sendLock.WaitAsync();
            try
            {
                await ws.SendAsync(new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text, true, _wsCts?.Token ?? CancellationToken.None);
            }
            catch (Exception ex)
            {
                _emitter.Emit("error", ex);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task SendViaHttp(string message, string connectionId, int generation)
        {
            var payload = JObject.Parse(message);
            var auth = Auth.GetInstance(_credentials);
            var accessToken = (await auth.Authenticate()).AccessToken;
            if (generation != CurrentGeneration || connectionId != _connection?.ConnectionId) return;

            payload["connection_id"] = connectionId;

            using var request = new UnityWebRequest($"{ArtConstants.BASE_URL}/v1/push-message", "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload.ToString(Formatting.None)));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Authorization", $"Bearer {accessToken}");
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("X-Org", _credentials.OrgTitle);
            request.SetRequestHeader("Environment", _credentials.Environment);
            request.SetRequestHeader("ProjectKey", _credentials.ProjectKey);

            var res = await SendRequest(request, _connectionTimeoutMs);

            if (res.TimedOut)
                throw new ConnectionError("The request timed out. Check its outcome before trying again.", "CONNECTION_TIMEOUT");
            if (res.NetworkError)
                throw new InvalidOperationException(res.Error ?? "network error");

            if (res.Status < 200 || res.Status >= 300)
            {
                var body = TryParseObject(res.Text);
                var msg = body?["error"]?.Type == JTokenType.String
                    ? body["error"].ToString()
                    : body?["message"]?.ToString();
                var errorCode = body?["error_code"]?.ToString();
                if (string.IsNullOrEmpty(errorCode)) errorCode = body?["code"]?.ToString();
                if (string.IsNullOrEmpty(errorCode)) errorCode = "CONNECTION_SEND_FAILED";

                throw new ConnectionError(
                    string.IsNullOrEmpty(msg) ? $"Request failed (HTTP {res.Status})." : msg,
                    errorCode,
                    (int)res.Status);
            }
        }

        // ---- UnityWebRequest helpers ----
        private struct HttpResult
        {
            public long Status;
            public string Text;
            public string Error;
            public bool TimedOut;
            public bool NetworkError;
        }

        private static async Task<HttpResult> SendRequest(UnityWebRequest request, long timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var op = request.SendWebRequest();
            while (!op.isDone)
            {
                if (sw.ElapsedMilliseconds > timeoutMs)
                {
                    request.Abort();
                    return new HttpResult { TimedOut = true, Error = "timeout" };
                }
                await Task.Yield();
            }

            return new HttpResult
            {
                Status = request.responseCode,
                Text = request.downloadHandler?.text,
                Error = request.error,
                NetworkError = request.result == UnityWebRequest.Result.ConnectionError
            };
        }

        private static JObject TryParseObject(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try { return JObject.Parse(text); }
            catch { return null; }
        }

        public void SetAutoReconnect(bool flag) => _autoReconnect = flag;

        // =====================================================================
        // Close / wait
        // =====================================================================

        public async Task CloseWebSocket(bool clearConnection = false)
        {
            IsConnectionActive = false;
            _connection = null;
            _isConnecting = false;

            if (clearConnection)
            {
                lock (_stateLock)
                {
                    _pendingIncomingMessages.Clear();
                    _subscriptions.Clear();
                    _interceptors.Clear();
                }
                PendingSendMessages = new List<string>();
            }

            _heartbeatCts?.Cancel();
            _heartbeatCts = null;

            await SafeClose();
            _wsCts?.Cancel();
        }

        /// <summary>
        /// Completes when the connection is ready. Rejects on connection failure; after the
        /// connection timeout without a connection it fails the connection (CONNECTION_TIMEOUT).
        /// </summary>
        public async Task WaitForConnection()
        {
            if (_connectionFailure != null) throw _connectionFailure;
            if (IsConnectionActive) return;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waiterLock) { _connectionWaiters.Add(tcs); }

            var timerCts = new CancellationTokenSource();
            _ = Task.Delay((int)Math.Min(_connectionTimeoutMs, int.MaxValue), timerCts.Token).ContinueWith(t =>
            {
                if (t.IsCanceled || tcs.Task.IsCompleted) return;
                FailConnection(new ConnectionError(
                    "Unable to establish a connection. Please check your connection and try again.",
                    "CONNECTION_TIMEOUT"));
            }, TaskContinuationOptions.ExecuteSynchronously);

            try
            {
                await tcs.Task;
            }
            finally
            {
                timerCts.Cancel();
                lock (_waiterLock) { _connectionWaiters.Remove(tcs); }
            }
        }

        // Kept for existing callers.
        public Task Wait() => WaitForConnection();

        private void ResolveWaiters()
        {
            List<TaskCompletionSource<bool>> waiters;
            lock (_waiterLock)
            {
                waiters = new List<TaskCompletionSource<bool>>(_connectionWaiters);
                _connectionWaiters.Clear();
            }
            foreach (var w in waiters)
                w.TrySetResult(true);
        }

        // =====================================================================
        // Heartbeat
        // =====================================================================

        private JObject BuildHeartbeatPayload()
        {
            JObject[] subs;
            lock (_stateLock)
            {
                subs = _subscriptions.Select(kv => new JObject
                {
                    ["name"] = kv.Key,
                    ["presenceTracking"] = kv.Value.IsListening
                }).ToArray();
            }

            return new JObject
            {
                ["connectionId"] = _connection?.ConnectionId,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["subscriptions"] = new JArray(subs)
            };
        }

        private void StartHeartbeat()
        {
            if (_heartbeatCts != null) return; // prevent multiple timers

            var cts = new CancellationTokenSource();
            _heartbeatCts = cts;
            var ct = cts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(30000, ct);
                        if (!IsConnectionActive) continue; // only when alive

                        try { await PushForSecureLine("heartbeat", BuildHeartbeatPayload(), false); }
                        catch { /* Lifecycle errors are delivered through connectionFailed. */ }
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

        // =====================================================================
        // Encrypt / Decrypt
        // =====================================================================

        public Task<string> Encrypt(string data, string recipientPublicKey)
            => EncryptFunc(data, recipientPublicKey);

        public Task<string> Decrypt(string encryptedHash, string senderPublicKey)
            => DecryptFunc(encryptedHash, senderPublicKey);

        // =====================================================================
        // Receive loop
        // =====================================================================

        private async Task StartReceiveLoop(ClientWebSocket ws, CancellationToken token, int generation)
        {
            var buffer = new byte[8192];

            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var sb = new StringBuilder();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            HandleSocketClosed(ws, generation, result.CloseStatusDescription);
                            return;
                        }
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    } while (!result.EndOfMessage);

                    if (generation != CurrentGeneration) return;
                    ParseIncomingMessage(sb.ToString());
                }

                HandleSocketClosed(ws, generation, "closed");
            }
            catch (Exception ex)
            {
                HandleSocketClosed(ws, generation, ex);
            }
        }

        // Only the current socket of the current generation may report a close; a socket we
        // detached/closed ourselves (reconnect, FailConnection, Disconnect) stays silent.
        private void HandleSocketClosed(ClientWebSocket ws, int generation, object detail)
        {
            if (generation != CurrentGeneration || !ReferenceEquals(ws, _ws)) return;

            IsConnectionActive = false;
            _isConnecting = false;
            Debug.Log($"[ART] Live closed: {detail}");
            _emitter.Emit("close", detail);
        }

        // =====================================================================
        // Event listeners
        // =====================================================================

        public string On(string eventName, Action<object> handler) => _emitter.On(eventName, handler);
        public void Off(string eventName, string id) => _emitter.Off(eventName, id);

        // IWebSocketHandler.SendMessage returns void; the public method above returns bool.
        void IWebSocketHandler.SendMessage(string message) => SendMessage(message);
    }
}