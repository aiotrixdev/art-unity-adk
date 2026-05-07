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
                    var error = data["error"]?.Value<bool>() ?? false;
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

            await Push("art_presence", new JObject());

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
        public void Acknowledge(JObject request, string returnFlag)
        {
            if (ChannelConfig.ChannelType != "targeted" && ChannelConfig.ChannelType != "secure")
                return;

            var channel = request["channel"]?.ToString();
            if (channel == "art_config" || channel == "art_secure" || channel == "art_presence")
                return;

            var response = new JObject
            {
                ["channel"] = channel,
                ["return_flag"] = returnFlag
            };

            var keys = new[] { "id", "ref_id", "from", "to_username", "to",
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

            if (_pendingAcks.TryGetValue(refId, out var ack))
            {
                ack.cts.Cancel();
                ack.tcs.TrySetResult(refId);
                _pendingAcks.Remove(refId);
            }
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

            _ = Task.Run(async () =>
            {
                if (IsListening)
                    await ValidateSubscription("presence");
                await SubscribeAsync();
            });
        }

        // ---- Push ----
        public virtual async Task Push(string eventName, JObject data, PushOptions options = null)
        {
            await WebSocketHandler.Wait();

            var connection = WebSocketHandler.GetConnection();
            if (connection == null)
                throw new ARTNotConnectedException();

            var to = options?.To ?? new List<string>();

            var messageStr = data.ToString(Formatting.None);

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
           // var fromName = connection.ConnectionId;
           // try { fromName = Auth.GetInstance().Username ?? fromName; } catch { }

            string refId = null;
            var chName = ChannelConfig.ChannelName;
            if (chName != "art_config" && chName != "art_secure" && chName != "art_presence")
            {
                _messageCount++;
                refId = $"{fromId}_{chName}_{_messageCount}";
            }

            var channelFull = chName;
            if (!string.IsNullOrEmpty(ChannelConfig.ChannelNamespace))
                channelFull += $":{ChannelConfig.ChannelNamespace}";

            var message = new JObject
            {
                ["from"] = connection.ConnectionId,
                ["to"] = JArray.FromObject(to),
                ["channel"] = channelFull,
                ["event"] = eventName,
                ["content"] = messageStr
            };

            if (refId != null)
                message["ref_id"] = refId;
            WebSocketHandler.SendMessage(message.ToString(Formatting.None));
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
