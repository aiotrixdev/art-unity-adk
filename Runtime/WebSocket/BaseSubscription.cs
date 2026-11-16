using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ART.ADK
{
    /// <summary>Kotlin: PresenceConfig(unique = true)</summary>
    public class PresenceConfig
    {
        public bool Unique { get; set; } = true;
    }

    /// <summary>Handle returned by FetchPresence; mirrors Kotlin PresenceSubscription.</summary>
    public class PresenceSubscription
    {
        public Func<Task> Unsubscribe { get; }
        public PresenceSubscription(Func<Task> unsubscribe) { Unsubscribe = unsubscribe; }
    }

    /// <summary>
    /// Base class for all subscription types, providing shared functionality:
    /// subscribe/unsubscribe lifecycle, push, presence, ACK handling, and buffering.
    /// </summary>
    public class BaseSubscription
    {
        public string ConnectionID { get; }
        public bool IsSubscribed { get; set; }
        public bool IsListening { get; set; }
        public IWebSocketHandler WebSocketHandler { get; }
        public ChannelConfig ChannelConfig { get; set; }
        public Dictionary<string, List<JObject>> MessageBuffer { get; } = new Dictionary<string, List<JObject>>();
        protected Dictionary<string, Dictionary<string, List<JObject>>> ThreadBuffers { get; }
            = new Dictionary<string, Dictionary<string, List<JObject>>>();
        public List<string> PresenceUsers { get; set; } = new List<string>();
        public EventEmitter Emitter { get; } = new EventEmitter();

        private readonly Dictionary<string, (TaskCompletionSource<string> tcs, CancellationTokenSource cts)> _pendingAcks
            = new Dictionary<string, (TaskCompletionSource<string>, CancellationTokenSource)>();
        private int _messageCount;

        /// <summary>Server-ack wait window, mirrors the Kotlin ACK_TIMEOUT (50s).</summary>
        protected const int ACK_TIMEOUT_MS = 50_000;

        public BaseSubscription(
            string connectionID,
            ChannelConfig channelConfig,
            IWebSocketHandler websocketHandler,
            string process = "subscribe")
        {
            ConnectionID = connectionID;
            WebSocketHandler = websocketHandler;
            ChannelConfig = channelConfig;
            PresenceUsers = new List<string>(channelConfig.PresenceUsers);

            if (process == "subscribe") IsSubscribed = true;
            else if (process == "presence") IsListening = true;
        }

        // ---- Connection failure listeners (forwarded to the handler) ----
        public void OnConnectionFailure(Action<ConnectionError> listener)
        {
            WebSocketHandler.OnConnectionFailure(listener);
        }

        public void OffConnectionFailure(Action<ConnectionError> listener)
        {
            WebSocketHandler.OffConnectionFailure(listener);
        }

        // ---- Validate Subscription ----
        public virtual async Task ValidateSubscription(string process)
        {
            try
            {
                if (ChannelConfig.ChannelName == "art_config" || ChannelConfig.ChannelName == "art_secure")
                    return;

                var channelName = ChannelConfig.ChannelName;
                if (!string.IsNullOrWhiteSpace(ChannelConfig.ChannelNamespace))
                    channelName += $":{ChannelConfig.ChannelNamespace}";

                try
                {
                    var config = await HelperFunctions.SubscribeToChannel(channelName, process, WebSocketHandler);
                    ChannelConfig = config;
                    if (process == "presence") IsListening = true;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[BaseSubscription] {ex}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BaseSubscription] {ex}");
            }
        }

        // ---- Presence ----
        /// <summary>
        /// Check for presence on the channel.
        /// Throws if not subscribed for presence.
        /// </summary>
        public async Task<PresenceSubscription> FetchPresence(
            Action<List<string>> callback,
            PresenceConfig options = null)
        {
            options = options ?? new PresenceConfig { Unique = true };

            var previousPresenceData = PresenceUsers;
            if (previousPresenceData.Count > 0)
                callback?.Invoke(previousPresenceData);

            await ValidateSubscription("presence");

            if (!IsListening)
                throw new InvalidOperationException("Not subscribed for presence");

            Emitter.On("art_presence", payload =>
            {
                if (payload is JObject data)
                {
                    // Kotlin: proceeds only when the "error" key is absent/null.
                    var err = data["error"];
                    if (err != null && err.Type != JTokenType.Null) return;

                    var usernames = data["usernames"]?.ToObject<List<string>>() ?? new List<string>();
                    PresenceUsers = new List<string>(usernames);

                    List<string> response;
                    if (options.Unique)
                    {
                        var seen = new HashSet<string>();
                        response = new List<string>();
                        foreach (var user in usernames)
                        {
                            var name = user.Split(':')[0];
                            if (seen.Add(name))
                                response.Add(name);
                        }
                    }
                    else
                    {
                        response = new List<string>(usernames);
                    }

                    MainThreadDispatcher.Enqueue(() => callback?.Invoke(response));
                }
            });

            // Fire-and-forget like Kotlin: scope.launch { runCatching { push(...) } }
            _ = Task.Run(async () =>
            {
                try { await Push("art_presence", new JObject()); }
                catch (Exception ex) { Debug.LogWarning($"[ART] art_presence push failed: {ex.Message}"); }
            });

            return new PresenceSubscription(async () =>
            {
                var success = await HelperFunctions.UnsubscribeFromChannel(
                    ChannelConfig.ChannelName,
                    ChannelConfig.SubscriptionID ?? "",
                    "presence",
                    WebSocketHandler);

                if (!success)
                    Debug.LogError($"[BaseSubscription] Failed to unsubscribe from channel {ChannelConfig.ChannelName}");
            });
        }

        // ---- ACK ----
        // Mirrors Kotlin: acks are only sent for "targeted" and "secure" channels,
        // and never for the reserved channels.
        public void Acknowledge(JObject request, string returnFlag)
        {
            if (ChannelConfig.ChannelType != "targeted" && ChannelConfig.ChannelType != "secure")
                return;

            var channel = request["channel"]?.ToString();
            if (channel == "art_config" || channel == "art_secure" || channel == "art_presence")
                return;

            var response = new JObject();
            var keys = new[] { "channel", "namespace", "id", "ref_id", "from", "to_username", "to" };
            foreach (var key in keys)
            {
                var v = request[key];
                if (v != null && v.Type != JTokenType.Null)
                    response[key] = v;
            }

            response["return_flag"] = returnFlag;

            var tail = new[] { "pipeline_id", "interceptor_name", "attempt_id" };
            foreach (var key in tail)
            {
                var v = request[key];
                if (v != null && v.Type != JTokenType.Null)
                    response[key] = v;
            }

            WebSocketHandler.SendMessage(response.ToString(Formatting.None));
        }

        public void HandleMessageAcks(string evt, string returnFlag, JObject data)
        {
            if (returnFlag != "SA") return;
            var refId = data["ref_id"]?.ToString();
            if (string.IsNullOrEmpty(refId)) return;

            (TaskCompletionSource<string> tcs, CancellationTokenSource cts) ack;
            lock (_pendingAcks)
            {
                if (!_pendingAcks.TryGetValue(refId, out ack)) return;
                _pendingAcks.Remove(refId);
            }
            ack.cts.Cancel();
            ack.tcs.TrySetResult(refId);
        }

        // ---- Subscribe ----
        public virtual async Task SubscribeAsync()
        {
            var name = ChannelConfig.ChannelName;
            if (name == "art_config" || name == "art_secure" || name == "art_notifications")
                return;

            IsSubscribed = true;

            try
            {
                var config = await HelperFunctions.SubscribeToChannel(
                    ChannelConfig.ChannelName, "subscribe", WebSocketHandler);
                ChannelConfig = config;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BaseSubscription] {ex}");
                IsSubscribed = false;
                throw;
            }
        }

        // ---- Unsubscribe ----
        public virtual async Task Unsubscribe()
        {
            var subId = ChannelConfig.SubscriptionID;
            if (string.IsNullOrEmpty(subId)) return;

            var ok = await HelperFunctions.UnsubscribeFromChannel(
                ChannelConfig.ChannelName,
                subId,
                "subscribe",
                WebSocketHandler);

            if (!ok)
                Debug.LogError($"[BaseSubscription] Failed to unsubscribe from channel {ChannelConfig.ChannelName}");
            else
                WebSocketHandler.RemoveSubscription(ChannelConfig.ChannelName);
        }

        // ---- Reconnect ----
        public virtual void Reconnect()
        {
            if (ChannelConfig.ChannelName == "art_config" || ChannelConfig.ChannelName == "art_secure")
                return;

            var isOrchestratorChannel = ChannelConfig.ChannelName != null &&
                                        ChannelConfig.ChannelName.StartsWith("orch_com_");
            if (isOrchestratorChannel)
                Debug.Log($"[{DateTime.Now:HH:mm:ss.fff}] [Connection] Reconnecting orchestrator channel '{ChannelConfig.ChannelName}'...");

            if (IsListening)
            {
                _ = Task.Run(async () =>
                {
                    try { await ValidateSubscription("presence"); }
                    catch (Exception ex) { Debug.LogError($"[BaseSubscription] Presence revalidation failed: {ex}"); }
                });
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await SubscribeAsync();
                    if (isOrchestratorChannel)
                        Debug.Log($"[{DateTime.Now:HH:mm:ss.fff}] [Connection] Reconnected orchestrator channel '{ChannelConfig.ChannelName}'.");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[BaseSubscription] Channel reconnection failed: {ex}");
                }
            });
        }

        // ---- Push ----
        /// <summary>
        /// Send an event to the channel, mirroring Kotlin BaseSubscription.push.
        /// Awaits the server "SA" ack (50s timeout) for targeted/secure channels only;
        /// other channels resolve immediately. A connection failure while waiting fails the push.
        /// Returns { "dispatched": true, "message": &lt;frame&gt;, "ref_id": string } once resolved.
        /// </summary>
        public virtual async Task<JObject> Push(string eventName, JToken data, PushOptions options = null)
        {
            await WebSocketHandler.WaitForConnection();
            await WebSocketHandler.EnsureAuthorization();

            var connection = WebSocketHandler.GetConnection();
            var to = options?.To ?? new List<string>();
            var threadId = options?.ThreadId;
            var messageStr = (data ?? new JObject()).ToString(Formatting.None);

            if (ChannelConfig.ChannelType == "secure" || ChannelConfig.ChannelType == "targeted")
            {
                if ((to.Count == 0 || to.Count != 1) && eventName != "art_presence")
                    throw new ArgumentException("Exactly one user must be specified for sending message.");
            }

            if (ChannelConfig.ChannelType == "secure" && eventName != "art_presence")
            {
                var secureResult = await WebSocketHandler.PushForSecureLine(
                    "secured_public_key",
                    new Dictionary<string, object> { { "username", to[0] } },
                    true) as JObject;

                var resData = secureResult?["data"] as JObject
                    ?? throw new ARTEncryptionException("Missing data in secure response");

                if (resData["status"]?.ToString() == "unsuccessfull")
                    throw new ARTEncryptionException(resData["error"]?.ToString() ?? "");

                var pubKey = resData["public_key"]?.ToString();
                if (string.IsNullOrEmpty(pubKey))
                    throw new ARTEncryptionException("Could not fetch public key");

                messageStr = await WebSocketHandler.Encrypt(messageStr, pubKey);
            }

            // ---- Ref ID & ACK promise ----
            string refId = null;
            var chName = ChannelConfig.ChannelName;
            var ackTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<ConnectionError> rejectAck = null;

            if (chName != "art_config" && chName != "art_secure" && chName != "art_presence")
            {
                var count = Interlocked.Increment(ref _messageCount);
                refId = $"{WebSocketHandler.GetConnection()?.ConnectionId}_{chName}_{count}";

                if (ChannelConfig.ChannelType == "targeted" || ChannelConfig.ChannelType == "secure")
                {
                    var capturedRefId = refId;
                    var cts = new CancellationTokenSource();
                    _ = Task.Delay(ACK_TIMEOUT_MS, cts.Token).ContinueWith(t =>
                    {
                        if (t.IsCanceled) return;
                        lock (_pendingAcks) { _pendingAcks.Remove(capturedRefId); }
                        ackTcs.TrySetException(new ConnectionError(
                            "The server has not acknowledged this request. Check its status before retrying.",
                            "ACK_TIMEOUT"));
                    }, TaskContinuationOptions.ExecuteSynchronously);

                    rejectAck = error => ackTcs.TrySetException(error);
                    lock (_pendingAcks) { _pendingAcks[refId] = (ackTcs, cts); }
                }
                else
                {
                    ackTcs.TrySetResult(refId);
                }
            }
            else
            {
                ackTcs.TrySetResult(null);
            }

            // Kotlin push includes the namespace suffix when one is set.
            var channelFull = chName;
            if (!string.IsNullOrWhiteSpace(ChannelConfig.ChannelNamespace))
                channelFull += $":{ChannelConfig.ChannelNamespace}";

            var message = new JObject
            {
                ["from"] = connection?.ConnectionId ?? "",
                ["to"] = JArray.FromObject(to),
                ["channel"] = channelFull,
                ["event"] = eventName,
                ["content"] = messageStr
            };

            if (refId != null) message["ref_id"] = refId;
            if (threadId != null) message["thread_id"] = threadId;
            if (options?.FileMeta != null && options.FileMeta.Count > 0)
                message["file_meta"] = JArray.FromObject(options.FileMeta);
            if (options?.ExecutionOrigin != null)
                message["execution_origin"] = options.ExecutionOrigin;

            Action<ConnectionError> onFailure = error => rejectAck?.Invoke(error);
            OnConnectionFailure(onFailure);
            try
            {
                var wire = message.ToString(Formatting.None);
                WebSocketHandler.SendMessage(wire);
                Debug.Log($"[ART] Push message '{eventName}' -> server: {wire}");

                // Throws ConnectionError (ACK_TIMEOUT or transport failure) if not acknowledged.
                await ackTcs.Task;
            }
            finally
            {
                OffConnectionFailure(onFailure);
                if (refId != null)
                {
                    (TaskCompletionSource<string> tcs, CancellationTokenSource cts) entry;
                    bool found;
                    lock (_pendingAcks)
                    {
                        found = _pendingAcks.TryGetValue(refId, out entry);
                        if (found) _pendingAcks.Remove(refId);
                    }
                    if (found) entry.cts.Cancel();
                }
            }

            var result = new JObject
            {
                ["dispatched"] = true,
                ["message"] = message
            };
            if (refId != null) result["ref_id"] = refId;
            return result;
        }

        // Not present in the Kotlin code; kept for existing Unity callers.
        public async Task PushArray(string eventName, JArray data)
        {
            await WebSocketHandler.WaitForConnection();

            var connection = WebSocketHandler.GetConnection();
            if (connection == null)
                throw new ARTNotConnectedException();

            var count = Interlocked.Increment(ref _messageCount);
            var refId = $"{connection.ConnectionId}_{ChannelConfig.ChannelName}_{count}";

            var channelFull = ChannelConfig.ChannelName;
            if (!string.IsNullOrEmpty(ChannelConfig.ChannelNamespace))
                channelFull += $":{ChannelConfig.ChannelNamespace}";

            var message = new JObject
            {
                ["from"] = connection.ConnectionId,
                ["to"] = new JArray(),
                ["channel"] = channelFull,
                ["event"] = eventName,
                ["content"] = data.ToString(Formatting.None),
                ["ref_id"] = refId
            };

            WebSocketHandler.SendMessage(message.ToString(Formatting.None));
        }

        // ---- HandleMessage (virtual, overridden by Subscription/SharedObjectChannel) ----
        public virtual Task HandleMessage(string evt, JObject payload)
        {
            return Task.CompletedTask;
        }

        // ---- Release ----
        /// <summary>
        /// Cancels all pending acks (failing their waiters), mirroring Kotlin release().
        /// </summary>
        public virtual void Release()
        {
            List<(TaskCompletionSource<string> tcs, CancellationTokenSource cts)> entries;
            lock (_pendingAcks)
            {
                entries = _pendingAcks.Values.ToList();
                _pendingAcks.Clear();
            }

            foreach (var (tcs, cts) in entries)
            {
                cts.Cancel();
                tcs.TrySetException(new Exception("Subscription released"));
            }
        }
    }
}