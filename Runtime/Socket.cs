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

namespace ART.ADK
{
    /// <summary>
    /// Core WebSocket handler. Manages the connection lifecycle, message routing,
    /// heartbeat, and fallback to long-polling.
    /// Uses System.Net.WebSockets.ClientWebSocket (works on all platforms except WebGL).
    /// For WebGL, a JS bridge plugin would be needed (flagged as limitation).
    /// </summary>
    public sealed class Socket : IWebSocketHandler
    {
        // Singleton
        private static Socket _instance;
        private static readonly object _singletonLock = new object();

        // WebSocket state
        private ClientWebSocket _ws;
        private CancellationTokenSource _wsCts;
        private AuthenticationConfig _credentials = new AuthenticationConfig();
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

        // Long poll client
        private LongPollClient _lpClient;
        private string _pullSource = "socket";
        private string _pushSource = "socket";

        // Event emitter
        private readonly EventEmitter _emitter = new EventEmitter();

        // Connection waiters
        private readonly List<TaskCompletionSource<bool>> _connectionWaiters = new List<TaskCompletionSource<bool>>();
        private readonly object _waiterLock = new object();

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
                    var auth = Auth.GetInstance(_credentials);
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
                OnError = err => Debug.Log($"[ART] LP error: {err}")
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

        // ---- InitiateSocket ----
        internal async Task InitiateSocket(AuthenticationConfig credentials)
        {
            if (_ws != null && IsConnectionActive) return;
            _credentials = credentials;

            // Try WebSocket
            try
            {
                await ConnectWebSocket();
                _pullSource = "socket";
                _pushSource = "socket";
                return;
            }
            catch (Exception ex)
            {
                Debug.Log($"[ART] WebSocket failed, falling back to LongPoll: {ex.Message}");
            }

            // Fallback: LongPoll
            _pullSource = "http";
            _pushSource = "http";
            _lpClient.Start(_connection?.ConnectionId);
        }

        // ---- ConnectWebSocket ----
        public async Task ConnectWebSocket()
        {
            if (_isConnecting) return;
            _isConnecting = true;

            AuthData authData;
            try
            {
                var auth = Auth.GetInstance(_credentials);
                authData = await auth.Authenticate(forceAuth: IsReConnecting);
            }
            catch (Exception ex)
            {
                _isConnecting = false;
                _emitter.Emit("close", ex);
                throw;
            }

            var wsUrl = $"{ArtConstants.WS_URL}" +
                $"?connection_id={Uri.EscapeDataString(_connection?.ConnectionId ?? "")}" +
                $"&Org-Title={Uri.EscapeDataString(_credentials.OrgTitle)}" +
                $"&token={Uri.EscapeDataString(authData.AccessToken)}" +
                $"&environment={Uri.EscapeDataString(_credentials.Environment)}" +
                $"&project-key={Uri.EscapeDataString(_credentials.ProjectKey)}";

            // Close existing connection
            await SafeClose();

            _wsCts = new CancellationTokenSource();
            _ws = new ClientWebSocket();

            try
            {
                var connectTask = _ws.ConnectAsync(new Uri(wsUrl), _wsCts.Token);
                var timeoutTask = Task.Delay(5000);
                var completed = await Task.WhenAny(connectTask, timeoutTask);

                if (completed == timeoutTask)
                {
                    _isConnecting = false;
                    _wsCts.Cancel();
                    throw new ARTTimeoutException("WebSocket handshake timeout");
                }

                await connectTask; // propagate any exception
                _isConnecting = false;
                _emitter.Emit("open");
                _ = StartReceiveLoop();
            }
            catch (ARTTimeoutException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _isConnecting = false;
                throw new ARTException($"WebSocket connection failed: {ex.Message}");
            }
        }

        private async Task SafeClose()
        {
            if (_ws == null) return;
            try
            {
                if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
                {
                    using var cts = new CancellationTokenSource(1000);
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", cts.Token);
                }
            }
            catch { }
            finally
            {
                _ws?.Dispose();
                _ws = null;
            }
        }

        // ---- Connection binding (art_ready) ----
        private void HandleConnectionBinding(string rawData)
        {
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

            _emitter.Emit("connection", _connection);
            IsConnectionActive = true;
            StartHeartbeat();
            ResolveWaiters();

            // Flush pending messages
            var queued = new List<string>(PendingSendMessages);
            PendingSendMessages.Clear();
            foreach (var msg in queued)
                SendMessage(msg);

            if (_autoReconnect)
            {
                foreach (var sub in _subscriptions.Values.ToList())
                    sub.Reconnect();
                foreach (var inter in _interceptors.Values.ToList())
                    inter.Reconnect();
            }
        }

        // ---- IWebSocketHandler: PushForSecureLine ----
        public async Task<object> PushForSecureLine(string eventName, object data, bool listen)
        {
            var connId = _connection?.ConnectionId ?? "";
            var rand = UnityEngine.Random.Range(0, 1000000).ToString();
            var refId = $"{connId}_secure_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{rand}";

            string content;
            try { content = JsonConvert.SerializeObject(data); }
            catch { content = "{}"; }

            string fromId   = connId;
            var fromName = connId;
            try { fromName = Auth.GetInstance().Username ?? fromId; } catch { }

            var payload = new JObject
            {
                ["from"] = fromId,
                ["from_username"] = fromName,
                ["from_id"] = fromId,
                ["channel"] = "art_secure",
                ["event"] = eventName,
                ["content"] = content,
                ["ref_id"] = refId
            };

            var msgStr = payload.ToString(Formatting.None);

            if (!listen)
            {
                SendMessage(msgStr);
                return null;
            }

            var tcs = new TaskCompletionSource<object>();
            SecureCallbacks[$"secure-{refId}"] = result => {
                tcs.TrySetResult(result);
            };
            SendMessage(msgStr);

            // Timeout after 30s
            var timeoutTask = Task.Delay(30000);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask);
            if (completed == timeoutTask)
            {
                SecureCallbacks.Remove($"secure-{refId}");
                return null;
            }
            return await tcs.Task;
        }

        // ---- IWebSocketHandler: RemoveSubscription ----
        public void RemoveSubscription(string channel)
        {
            _subscriptions.Remove(channel);
        }

        // ---- Subscribe ----
        public async Task<BaseSubscription> Subscribe(string channel)
        {
            return await HandleSubscription(channel);
        }

        private async Task<BaseSubscription> HandleSubscription(string channel)
        {
            await Wait();
            var connectionId = _connection?.ConnectionId ?? "";

            if (_subscriptions.TryGetValue(channel, out var existing))
            {
                await existing.SubscribeAsync();
                return existing;
            }

            var channelConfig = await ValidateSubscription(channel, "subscribe");
            if (channelConfig == null)
                throw new ARTChannelNotFoundException(channel);

            BaseSubscription subscription;
            if (channelConfig.ChannelType == "shared-object")
            {
                subscription = new SharedObjectChannel(connectionId, channelConfig, this, "subscribe");
            }
            else
            {
                subscription = new Subscription(connectionId, channelConfig, this, "subscribe");
            } 

            _subscriptions[channel] = subscription;

            // Replay buffered messages
            if (_pendingIncomingMessages.TryGetValue(channel, out var buf))
            {
                foreach (var item in buf)
                    await subscription.HandleMessage(item.evt, item.payload);
                _pendingIncomingMessages.Remove(channel);
            }

            return subscription;
        }

        private async Task<ChannelConfig> ValidateSubscription(string channelName, string process)
        {
            if (channelName == "art_config" || channelName == "art_secure")
            {
                return new ChannelConfig
                {
                    ChannelName = channelName,
                    ChannelNamespace = "",
                    ChannelType = "default"
                };
            }
            return await HelperFunctions.SubscribeToChannel(channelName, process, this);
        }

        // ---- IWebSocketHandler: GetConnection ----
        public ConnectionDetail GetConnection() => _connection;

        // ---- Intercept ----
        public async Task<Interceptor> Intercept(
            string interceptorName,
            Action<JObject, Action<object>, Action<string>> fn)
        {
            await Wait();

            if (_interceptors.TryGetValue(interceptorName, out var existing))
                return existing;

            var interception = new Interceptor(interceptorName, fn, this);
            await interception.ValidateInterception();
            _interceptors[interceptorName] = interception;
            return interception;
        }

        // ---- Message parsing ----
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
                Debug.Log($"[ART] Parse error: {ex.Message}");
            }
        }

        private void ProcessIncomingMessages(List<JObject> messages)
        {
            foreach (var msg in messages)
                HandleIncomingMessage(msg);
        }

        private void HandleIncomingMessage(JObject parsed)
        {
            var channel = parsed["channel"]?.ToString();
            if (string.IsNullOrEmpty(channel) && parsed["event"] == null)
            {
                return;
            }

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
                if (SecureCallbacks.TryGetValue(key, out var cb))
                {
                    var dataDict = new JObject
                    {
                        ["channel"] = channel,
                        ["namespace"] = ns,
                        ["ref_id"] = refId,
                        ["event"] = evt
                    };

                    if (rawData != null)
                    {
                        try
                        {
                            var innerParsed = JObject.Parse(rawData.ToString());
                            dataDict["data"] = innerParsed;
                        }
                        catch { }
                    }

                    var result = new JObject
                    {
                        ["data"] = dataDict["data"],
                        ["channel"] = channel,
                        ["namespace"] = ns,
                        ["ref_id"] = refId,
                        ["event"] = evt
                    };

                    cb(result);
                    SecureCallbacks.Remove(key);
                }
                return;
            }

            if (string.IsNullOrEmpty(channel) || (string.IsNullOrEmpty(evt) && returnFlag != "SA"))
            {
                return;
            }

            if (evt == "shift_to_http")
            {
                SwitchToHttpPoll();
                return;
            }

            // Build payload with "data" field instead of "content"
            var payload = new JObject(parsed);
            payload.Remove("content");
            payload["data"] = rawData;

            // Interceptor routing
            if (!string.IsNullOrEmpty(interceptorName))
            {
                if (_interceptors.TryGetValue(interceptorName, out var interception))
                {
                    _ = Task.Run(async () =>
                    {
                        try { await interception.HandleMessage(channel, payload); }
                        catch (Exception ex) { Debug.LogException(ex); }
                    });
                }
                else
                {
                }
                return;
            }

            // Subscription routing
            var subKey = channel;
            if (!string.IsNullOrEmpty(ns))
                subKey += $":{ns}";

            if (_subscriptions.TryGetValue(subKey, out var sub))
            {
                _ = Task.Run(async () =>
                {
                    try { await sub.HandleMessage(evt, payload); }
                    catch (Exception ex) { Debug.LogException(ex); }
                });
            }
            else
            {
                if (!_pendingIncomingMessages.ContainsKey(subKey))
                    _pendingIncomingMessages[subKey] = new List<(string, JObject)>();
                _pendingIncomingMessages[subKey].Add((evt, payload));
            }
        }

        private void SwitchToHttpPoll()
        {
            if (_pullSource == "http") return;
            _pullSource = "http";
            _pushSource = "http";
            _lpClient.Start(_connection?.ConnectionId ?? "");
        }

        // ---- IWebSocketHandler: SendMessage ----
        public bool SendMessage(string message)
        {
            if (_ws == null || _ws.State != WebSocketState.Open)
            {
                PendingSendMessages.Add(message);
                return false;
            } 

            var bytes = Encoding.UTF8.GetBytes(message);

            _ = Task.Run(async () =>
            {
                try
                {

                    await _ws.SendAsync(new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text, true, _wsCts?.Token ?? CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _emitter.Emit("error", ex);
                }
            });
            return true;
        }

        public void SetAutoReconnect(bool flag) => _autoReconnect = flag;

        // ---- CloseWebSocket ----
        public async Task CloseWebSocket(bool clearConnection = false)
        {
            await SafeClose();
            IsConnectionActive = false;
            _connection = null;
            _isConnecting = false;
            _wsCts?.Cancel();

            if (clearConnection)
            {
                _pendingIncomingMessages.Clear();
                PendingSendMessages.Clear();
                _subscriptions.Clear();
                _interceptors.Clear();
            }

            _heartbeatCts?.Cancel();
            _heartbeatCts = null;
        }

        // ---- IWebSocketHandler: Wait ----
        public Task Wait()
        {
            if (IsConnectionActive) return Task.CompletedTask;

            var tcs = new TaskCompletionSource<bool>();
            lock (_waiterLock)
            {
                _connectionWaiters.Add(tcs);
            }
            return tcs.Task;
        }

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

        // ---- Heartbeat ----
        private void StartHeartbeat()
        {
            if (_heartbeatCts != null) return;
            _heartbeatCts = new CancellationTokenSource();
            var ct = _heartbeatCts.Token;

            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(30000, ct);
                    if (!IsConnectionActive) continue;

                    var subs = _subscriptions.Select(kv => new JObject
                    {
                        ["name"] = kv.Key,
                        ["presenceTracking"] = kv.Value.IsListening
                    }).ToArray();

                    var payload = new JObject
                    {
                        ["connectionId"] = _connection?.ConnectionId,
                        ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        ["subscriptions"] = new JArray(subs)
                    };

                    try
                    {
                        await PushForSecureLine("heartbeat", payload, false);
                    }
                    catch { }
                }
            });
        }

        // ---- IWebSocketHandler: Encrypt/Decrypt ----
        public Task<string> Encrypt(string data, string recipientPublicKey)
            => EncryptFunc(data, recipientPublicKey);

        public Task<string> Decrypt(string encryptedHash, string senderPublicKey)
            => DecryptFunc(encryptedHash, senderPublicKey);

        // ---- Receive loop ----
        private async Task StartReceiveLoop()
        {
            var buffer = new byte[8192];

            try
            {
                while (_ws != null && _ws.State == WebSocketState.Open)
                {
                    var sb = new StringBuilder();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _wsCts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            IsConnectionActive = false;
                            _isConnecting = false;
                            _emitter.Emit("close", result.CloseStatusDescription);
                            return;
                        }
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    } while (!result.EndOfMessage);

                    var message = sb.ToString();
                    ParseIncomingMessage(message);
                }
            }
            catch (Exception ex)
            {
                IsConnectionActive = false;
                _isConnecting = false;
                _emitter.Emit("close", ex);
            }
        }

        // ---- Event listeners ----
        public string On(string eventName, Action<object> handler) => _emitter.On(eventName, handler);
        public void Off(string eventName, string id) => _emitter.Off(eventName, id);
    }
}
