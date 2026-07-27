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
        public List<string> PresenceUsers { get; set; } = new List<string>();
        public EventEmitter Emitter { get; } = new EventEmitter();

        private readonly Dictionary<string, (TaskCompletionSource<string> tcs, CancellationTokenSource cts)> _pendingAcks
            = new Dictionary<string, (TaskCompletionSource<string>, CancellationTokenSource)>();
        private int _messageCount;

        /// <summary>Server-ack wait window, mirrors the Kotlin ACK_TIMEOUT (50s).</summary>
        private const int ACK_TIMEOUT_MS = 50_000;

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

        // ---- Validate Subscription ----
        public async Task ValidateSubscription(string process)
        {
            if (ChannelConfig.ChannelName == "art_config" || ChannelConfig.ChannelName == "art_secure")
                return;

            var channelName = ChannelConfig.ChannelName;
            if (!string.IsNullOrEmpty(ChannelConfig.ChannelNamespace))
                channelName += $":{ChannelConfig.ChannelNamespace}";

            try
            {
                var config = await HelperFunctions.SubscribeToChannel(channelName, process, WebSocketHandler);
                ChannelConfig = config;
                if (process == "presence") IsListening = true;
            }
            catch (Exception ex)
            {
            }
        }

        // ---- Presence ----
        public async Task<Action> FetchPresence(bool unique = true, Action<List<string>> callback = null)
        {
            if (PresenceUsers.Count > 0 && callback != null)
                callback(PresenceUsers);

            await ValidateSubscription("presence");

            if (!IsListening)
                throw new ARTServerException("Not subscribed for presence");

            Emitter.On("art_presence", payload =>
            {
                if (payload is JObject data)
                {
                    // Value<bool?> tolerates both an absent "error" key and an explicit
                    // JSON null (data["error"] as JValue(Null)); Value<bool> would throw
                    // InvalidCastException on the latter.
                    var error = data["error"]?.Value<bool?>() ?? false;
                    if (error) return;

                    var usernames = data["usernames"]?.ToObject<List<string>>() ?? new List<string>();
                    PresenceUsers = usernames;

                    List<string> response;
                    if (unique)
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
                        response = usernames;
                    }

                    MainThreadDispatcher.Enqueue(() => callback?.Invoke(response));
                }
            });

            // Fire-and-forget like the Kotlin fetchPresence (scope.launch { runCatching { push(...) } }):
            // the presence request must not block or fail the caller on a missing ack.
            _ = Task.Run(async () =>
            {
                try { await Push("art_presence", new JObject()); }
                catch (Exception ex) { Debug.LogWarning($"[ART] art_presence push failed: {ex.Message}"); }
            });

            return async () =>
            {
                await HelperFunctions.UnsubscribeFromChannel(
                    ChannelConfig.ChannelName,
                    ChannelConfig.SubscriptionID ?? "",
                    "presence",
                    WebSocketHandler);
            };
        }

        // ---- ACK ----
        // Mirrors Kotlin BaseSubscription.acknowledge: acks are sent for EVERY channel
        // except the reserved ones — orchestrator flows depend on MA/CA acks.
        public void Acknowledge(JObject request, string returnFlag)
        {
            var chName = ChannelConfig.ChannelName;
            if (chName == "art_config" || chName == "art_secure" || chName == "art_presence")
                return;

            var channel = request["channel"]?.ToString();
            if (string.IsNullOrEmpty(channel))
                return;

            var response = new JObject
            {
                ["channel"] = channel,
                ["return_flag"] = returnFlag
            };

            var keys = new[] { "id", "ref_id", "from", "to_user_id", "to_username", "to",
                              "pipeline_id", "interceptor_name", "attempt_id" };
            foreach (var key in keys)
            {
                if (request[key] != null)
                    response[key] = request[key];
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
        public async Task SubscribeAsync()
        {
            if (ChannelConfig.ChannelName == "art_config" || ChannelConfig.ChannelName == "art_secure")
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
                IsSubscribed = false;
            }
        }

        // ---- Unsubscribe ----
        public async Task Unsubscribe()
        {
            if (string.IsNullOrEmpty(ChannelConfig.SubscriptionID)) return;

            try
            {
                var ok = await HelperFunctions.UnsubscribeFromChannel(
                    ChannelConfig.ChannelName,
                    ChannelConfig.SubscriptionID,
                    "subscribe",
                    WebSocketHandler);

                if (ok)
                    WebSocketHandler.RemoveSubscription(ChannelConfig.ChannelName);
            }
            catch (Exception ex)
            {
            }
        }

        // ---- Reconnect ----
        public void Reconnect()
        {
            if (ChannelConfig.ChannelName == "art_config" || ChannelConfig.ChannelName == "art_secure")
                return;

            // Scope reconnect logging to orchestrator channels so the [Connection] lifecycle is
            // visible without spamming for every channel type.
            var isOrchestratorChannel = ChannelConfig.ChannelName != null &&
                                        ChannelConfig.ChannelName.StartsWith("orch_com_");
            if (isOrchestratorChannel)
                Debug.Log($"[{DateTime.Now:HH:mm:ss.fff}] [Connection] Reconnecting orchestrator channel '{ChannelConfig.ChannelName}'...");

            _ = Task.Run(async () =>
            {
                if (IsListening)
                    await ValidateSubscription("presence");
                await SubscribeAsync();

                if (isOrchestratorChannel)
                    Debug.Log($"[{DateTime.Now:HH:mm:ss.fff}] [Connection] Reconnected orchestrator channel '{ChannelConfig.ChannelName}'.");
            });
        }

        // ---- Push ----
        /// <summary>
        /// Send an event to the channel and await the server's "SA" ack (50s timeout), mirroring
        /// the Kotlin <c>BaseSubscription.push</c>. Reserved channels (art_config / art_secure /
        /// art_presence) resolve immediately without an ack. Returns
        /// <c>{ "dispatched": bool, "message": &lt;full server-bound message&gt;, "ref_id": string }</c>.
        /// When <see cref="PushOptions.ThreadId"/> is set it is serialized as a top-level
        /// <c>thread_id</c> field on the frame (orchestrator thread scoping).
        /// </summary>
        public virtual async Task<JObject> Push(string eventName, JToken data, PushOptions options = null)
        {
            await WebSocketHandler.Wait();

            var connection = WebSocketHandler.GetConnection();
            if (connection == null)
                throw new ARTNotConnectedException();

            var to = options?.To ?? new List<string>();

            var messageStr = (data ?? new JObject()).ToString(Formatting.None);

            // Targeted/secure validation
            if (ChannelConfig.ChannelType == "secure" || ChannelConfig.ChannelType == "targeted")
            {
                if (to.Count != 1 && eventName != "art_presence")
                    throw new ARTServerException("Exactly one user must be specified for targeted/secure channel");
            }

            // Encrypt for secure channels
            if (ChannelConfig.ChannelType == "secure" && eventName != "art_presence")
            {
                var secureResult = await WebSocketHandler.PushForSecureLine(
                    "secured_public_key",
                    new Dictionary<string, object> { { "username", to[0] } },
                    true) as JObject;

                var innerData = secureResult?["data"] as JObject;
                var pubKey = innerData?["public_key"]?.ToString();
                
                if (pubKey == null || innerData?["status"]?.ToString() == "unsuccessfull")
                {
                    throw new ARTEncryptionException(innerData?["error"]?.ToString() ?? "Could not fetch public key");
                }

                messageStr = await WebSocketHandler.Encrypt(messageStr, pubKey);
            }

            var fromId = connection.ConnectionId;

            // ---- Ref ID & ACK promise (mirrors Kotlin: pendingAcks + 50s timeout) ----
            string refId = null;
            var chName = ChannelConfig.ChannelName;
            TaskCompletionSource<string> ackTcs;

            if (chName != "art_config" && chName != "art_secure" && chName != "art_presence")
            {
                _messageCount++;
                refId = $"{fromId}_{chName}_{_messageCount}";

                ackTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var cts = new CancellationTokenSource();
                var capturedRefId = refId;
                _ = Task.Delay(ACK_TIMEOUT_MS, cts.Token).ContinueWith(t =>
                {
                    if (t.IsCanceled) return;
                    lock (_pendingAcks) { _pendingAcks.Remove(capturedRefId); }
                    ackTcs.TrySetException(new ARTServerException("ACK timeout"));
                }, TaskContinuationOptions.ExecuteSynchronously);

                lock (_pendingAcks) { _pendingAcks[refId] = (ackTcs, cts); }
            }
            else
            {
                ackTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                ackTcs.TrySetResult(null);
            }

            // Kotlin push sends the plain channel name (no namespace suffix).
            var message = new JObject
            {
                ["from"] = connection.ConnectionId,
                ["to"] = JArray.FromObject(to),
                ["channel"] = chName,
                ["event"] = eventName,
                ["content"] = messageStr
            };

            if (refId != null)
                message["ref_id"] = refId;

            // Orchestrator thread scoping — top-level frame field, like the Kotlin SDK.
            if (!string.IsNullOrEmpty(options?.ThreadId))
                message["thread_id"] = options.ThreadId;

            var wire = message.ToString(Formatting.None);
            var dispatched = WebSocketHandler.SendMessage(wire);
            Debug.Log($"[ART] Push message '{eventName}' -> server ({(dispatched ? "sent" : "queued (socket not open)")}): {wire}");

            // Await the server ack (Kotlin: ackDeferred.await()). Throws ARTServerException
            // ("ACK timeout") if the server does not acknowledge within the timeout.
            await ackTcs.Task;

            var result = new JObject
            {
                ["dispatched"] = dispatched,
                ["message"] = message
            };
            if (refId != null) result["ref_id"] = refId;
            return result;
        }

        public async Task PushArray(string eventName, JArray data)
        {
            await WebSocketHandler.Wait();

            var connection = WebSocketHandler.GetConnection();
            if (connection == null)
                throw new ARTNotConnectedException();

            _messageCount++;
            var refId = $"{connection.ConnectionId}_{ChannelConfig.ChannelName}_{_messageCount}";

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
    }
}
