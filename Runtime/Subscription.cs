using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ART.ADK
{
    /// <summary>
    /// Standard channel subscription with event binding, listening, and message buffering.
    /// </summary>
    public sealed class Subscription : BaseSubscription
    {
        public Subscription(
            string connectionID,
            ChannelConfig channelConfig,
            IWebSocketHandler websocketHandler,
            string process = "subscribe")
            : base(connectionID, channelConfig, websocketHandler, process)
        {
        }

        /// <summary>
        /// Listen to ALL events on this channel. Buffered messages are replayed immediately.
        /// </summary>
        public void Listen(Action<JObject> callback)
        {
            // Replay buffered messages
            foreach (var kv in MessageBuffer)
            {
                foreach (var reqData in kv.Value)
                {
                    var result = new JObject
                    {
                        ["event"] = kv.Key,
                        ["content"] = reqData["content"]
                    };
                    MainThreadDispatcher.Enqueue(() => callback(result));
                    Acknowledge(reqData, "CA");
                }
            }
            MessageBuffer.Clear();

            Emitter.On("all", data =>
            {
                if (data is JObject obj)
                    MainThreadDispatcher.Enqueue(() => callback(obj));
            });
        }

        /// <summary>
        /// Bind to a specific event type on this channel.
        /// </summary>
        public void Bind(string eventName, Action<object> callback)
        {
            // Replay buffered messages for this event
            if (MessageBuffer.TryGetValue(eventName, out var msgs))
            {
                foreach (var reqData in msgs)
                {
                    var content = reqData["content"];
                    MainThreadDispatcher.Enqueue(() => callback(content));
                    Acknowledge(reqData, "CA");
                }
                MessageBuffer.Remove(eventName);
            }

            Emitter.On(eventName, data =>
            {
                MainThreadDispatcher.Enqueue(() => callback(data));
            });
        }

        /// <summary>
        /// Remove listeners for a specific event.
        /// </summary>
        public void Remove(string eventName)
        {
            Emitter.Off(eventName);
            MessageBuffer.Remove(eventName);
        }


        /// <summary>
        /// Handle incoming messages: decrypt if secure, parse content, emit to listeners or buffer.
        /// </summary>
        public override async Task HandleMessage(string evt, JObject payload)
        {
            var returnFlag = payload["return_flag"]?.ToString() ?? "";

            // Handle SA ack
            if (returnFlag == "SA")
            {
                HandleMessageAcks(evt, returnFlag, payload);
                return;
            }

            Acknowledge(payload, "MA");

            var mutablePayload = new JObject(payload);

            // Secure channel decrypt
            if (ChannelConfig.ChannelType == "secure")
            {
                //try
                //{
                Debug.Log($"PAYLOAD json '{payload}'");
                    Debug.Log($"PAYLOAD '{payload["from_username"]}'");
                    var secureResult = await WebSocketHandler.PushForSecureLine(
                        "secured_public_key",
                        new Dictionary<string, object>
                        {
                            { "username", !string.IsNullOrEmpty(payload["from_username"]?.ToString()) ? payload["from_username"].ToString() : payload["from"]?.ToString() ?? "" }
                        },
                        true) as JObject;
                    Debug.Log($"[ART] Decryption result....: {secureResult}");

                    var innerData = secureResult?["data"] as JObject;
                    var pubKey = innerData?["public_key"]?.ToString();
                    if (pubKey == null) return;
                    if (innerData?["status"]?.ToString() == "unsuccessfull") return;
                     Debug.Log($"[ART] Decryption result....inner data: {innerData}");
                     Debug.Log($"[ART] Decryption result....public key: {pubKey}");

                    var encryptedData = mutablePayload["data"]?.ToString();
                     Debug.Log($"[ART] Decryption result....encrypted key: {encryptedData}");
                    if (encryptedData != null)
                    {
                        Debug.Log($"[ART] Decryption result....encrypted key inside : {pubKey}");
                        var decrypted = await WebSocketHandler.Decrypt(encryptedData, pubKey);
                        mutablePayload["data"] = decrypted;
                        Debug.Log($"[ART] Decryption result....decrypt inside encrypted : {decrypted}");

                    }

                // }
                // catch (Exception ex)
                // {
                //     Debug.Log($"[ART] Decryption error: meant for another user or invalid key. {ex.Message}");
                //     return;
                // }
            }

            // Parse content
            object content = new JObject();
            var dataVal = mutablePayload["data"];
            if (dataVal != null)
            {
                if (dataVal.Type == JTokenType.String)
                {
                    try { content = JToken.Parse(dataVal.ToString()); }
                    catch { content = dataVal; }
                }
                else
                {
                    content = dataVal;
                }
            }

            // Presence event
            if (evt == "art_presence")
            {
                Emitter.Emit("art_presence", content);
                return;
            }

            if (!IsSubscribed) return;

            var hasSpecific = Emitter.ListenerCount(evt) > 0;
            var hasAll = Emitter.ListenerCount("all") > 0;

            if (hasSpecific || hasAll)
            {
                if (hasSpecific) Emitter.Emit(evt, content);
                if (hasAll)
                {
                    Emitter.Emit("all", new JObject
                    {
                        ["event"] = evt,
                        ["content"] = content is JToken jt ? jt : JToken.FromObject(content)
                    });
                }
                Acknowledge(mutablePayload, "CA");
            }
            else
            {
                // Buffer for later
                var entry = new JObject { ["content"] = content is JToken jt2 ? jt2 : JToken.FromObject(content) };
                var keys = new[] { "id", "from", "channel", "to", "pipeline_id",
                                  "attempt_id", "interceptor_name", "to_username" };
                foreach (var key in keys)
                {
                    if (mutablePayload[key] != null)
                        entry[key] = mutablePayload[key];
                }

                if (!MessageBuffer.ContainsKey(evt))
                    MessageBuffer[evt] = new List<JObject>();
                MessageBuffer[evt].Add(entry);
            }
        }
    }
}
