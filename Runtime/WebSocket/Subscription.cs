using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ART.ADK
{
    /// <summary>
    /// Standard channel subscription with event binding, listening, message buffering,
    /// orchestrator thread routing and human-feedback (HITL) replies.
    /// Mirrors the Kotlin <c>Subscription.kt</c> / JS <c>subscription.ts</c>:
    ///  • outbound thread pushes carry a top-level <c>thread_id</c> frame field,
    ///  • inbound messages route on <c>payload.thread_id</c> to <c>{threadId}-{event}</c> /
    ///    <c>{threadId}-all</c> listeners,
    ///  • thread messages arriving before a listener attaches are buffered per-thread and
    ///    replayed on attach,
    ///  • human-feedback requests get a <c>Reply</c> callback injected that sends the raw
    ///    <c>return_flag: "HF"</c> frame echoing the original request's routing fields.
    /// </summary>
    public sealed class Subscription : BaseSubscription
    {
        private readonly Dictionary<string, OrchestratorThread> _threads = new Dictionary<string, OrchestratorThread>();

        /// <summary>
        /// Per-thread message buffers: threadId -> event -> pending messages. Mirrors the
        /// Kotlin <c>threadBuffers</c>. Drained (and CA-acked) when a thread listener attaches.
        /// </summary>
        private readonly Dictionary<string, Dictionary<string, List<BufferedThreadMessage>>> _threadBuffers
            = new Dictionary<string, Dictionary<string, List<BufferedThreadMessage>>>();

        private sealed class BufferedThreadMessage
        {
            public readonly JObject Payload;
            public readonly JToken Content;
            public readonly Action<object> Reply;

            public BufferedThreadMessage(JObject payload, JToken content, Action<object> reply)
            {
                Payload = payload;
                Content = content;
                Reply = reply;
            }
        }

        public Subscription(
            string connectionID,
            ChannelConfig channelConfig,
            IWebSocketHandler websocketHandler,
            string process = "subscribe")
            : base(connectionID, channelConfig, websocketHandler, process)
        {
        }

        // -------------------------------------------------------------------------
        // Thread management — mirrors Kotlin thread() / threadUnchecked() / getThread()
        // -------------------------------------------------------------------------

        /// <summary>
        /// Returns (or creates) an <see cref="OrchestratorThread"/>. When an explicit
        /// <paramref name="threadId"/> matches a live thread, that instance is reused.
        /// </summary>
        public OrchestratorThread ThreadUnchecked(string threadId = null)
        {
            if (threadId != null &&
                _threads.TryGetValue(threadId, out var existing) &&
                !existing.IsDisposed())
            {
                return existing;
            }

            var t = new OrchestratorThread(this, threadId);
            _threads[t.ThreadId] = t;
            return t;
        }

        public OrchestratorThread GetThread(string threadId)
            => _threads.TryGetValue(threadId, out var t) ? t : null;

        public void UnregisterThread(string threadId)
        {
            _threads.Remove(threadId);
            lock (_threadBuffers) { _threadBuffers.Remove(threadId); }
        }

        // -------------------------------------------------------------------------
        // Thread listeners — emitter keys are "{threadId}-{event}" / "{threadId}-all",
        // exactly like the Kotlin SDK.
        // -------------------------------------------------------------------------

        public void AttachThreadListener(string threadId, Action<object> callback)
        {
            // Replay everything buffered for this thread, then attach live.
            List<(BufferedThreadMessage msg, string evt)> pending = null;
            lock (_threadBuffers)
            {
                if (_threadBuffers.TryGetValue(threadId, out var buffer))
                {
                    pending = new List<(BufferedThreadMessage, string)>();
                    foreach (var kv in buffer)
                        foreach (var msg in kv.Value)
                            pending.Add((msg, kv.Key));
                    _threadBuffers.Remove(threadId);
                }
            }

            if (pending != null)
            {
                foreach (var (msg, evt) in pending)
                {
                    var envelope = MakeEnvelope(evt, msg.Payload, msg.Content, msg.Reply);
                    MainThreadDispatcher.Enqueue(() => callback(envelope));
                    Acknowledge(msg.Payload, "CA");
                }
            }

            Emitter.On($"{threadId}-all", data =>
            {
                MainThreadDispatcher.Enqueue(() => callback(data));
            });
        }

        public void AttachThreadBind(string threadId, string eventName, Action<object> callback)
        {
            List<BufferedThreadMessage> pending = null;
            lock (_threadBuffers)
            {
                if (_threadBuffers.TryGetValue(threadId, out var buffer) &&
                    buffer.TryGetValue(eventName, out var msgs))
                {
                    pending = new List<BufferedThreadMessage>(msgs);
                    buffer.Remove(eventName);
                }
            }

            if (pending != null)
            {
                foreach (var msg in pending)
                {
                    var envelope = MakeEnvelope(eventName, msg.Payload, msg.Content, msg.Reply);
                    MainThreadDispatcher.Enqueue(() => callback(envelope));
                    Acknowledge(msg.Payload, "CA");
                }
            }

            Emitter.On($"{threadId}-{eventName}", data =>
            {
                MainThreadDispatcher.Enqueue(() => callback(data));
            });
        }

        public void DetachThreadListener(string threadId, string eventName = null, Action<object> callback = null)
        {
            var evt = string.IsNullOrEmpty(eventName) ? "all" : eventName;
            Emitter.Off($"{threadId}-{evt}");
            lock (_threadBuffers)
            {
                if (_threadBuffers.TryGetValue(threadId, out var buffer))
                    buffer.Remove(evt);
            }
        }

        // -------------------------------------------------------------------------
        // Channel-level listeners
        // -------------------------------------------------------------------------

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

        // -------------------------------------------------------------------------
        // Inbound message handling — mirrors Kotlin Subscription.handleMessage step by step.
        // -------------------------------------------------------------------------

        public override async Task HandleMessage(string evt, JObject payload)
        {
            var returnFlag = payload["return_flag"]?.ToString() ?? "";

            // 1. Server ack — resolve the pending push and return early.
            if (returnFlag == "SA")
            {
                HandleMessageAcks(evt, returnFlag, payload);
                return;
            }

            // 2. Message ack.
            Acknowledge(payload, "MA");

            // 2b. Sender/receiver routing check. The server echoes channel broadcasts back
            //     to the sender connection, so a frame we originated comes right back to our
            //     own listeners. Drop it here (and CA-ack so the server does not re-deliver).
            //     Presence frames are exempt — the presence reply rides on our own push.
            if (evt != "art_presence" && !IsAddressedToUs(payload))
            {
                Acknowledge(payload, "CA");
                return;
            }

            var mutablePayload = new JObject(payload);

            // 3. Secure channel decrypt.
            if (ChannelConfig.ChannelType == "secure")
            {
                var secureResult = await WebSocketHandler.PushForSecureLine(
                    "secured_public_key",
                    new Dictionary<string, object>
                    {
                        { "username", !string.IsNullOrEmpty(payload["from_username"]?.ToString()) ? payload["from_username"].ToString() : payload["from"]?.ToString() ?? "" }
                    },
                    true) as JObject;

                var innerData = secureResult?["data"] as JObject;
                var pubKey = innerData?["public_key"]?.ToString();
                if (pubKey == null) return;
                if (innerData?["status"]?.ToString() == "unsuccessfull") return;

                var encryptedData = mutablePayload["data"]?.ToString();
                if (encryptedData != null)
                {
                    var decrypted = await WebSocketHandler.Decrypt(encryptedData, pubKey);
                    mutablePayload["data"] = decrypted;
                }
            }

            // 4. Parse content.
            JToken content = new JObject();
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

            // 5. Human-feedback detection — mirrors the Kotlin/JS humanFeedbackRequest block.
            //    The reply callback sends the raw HF frame echoing the original request.
            Action<object> reply = null;
            var contentObj = content as JObject;
            var isHumanFeedbackRequest =
                returnFlag == "requestFeedback" ||
                evt == "human_input_request" ||
                contentObj?["type"]?.ToString() == "human_input_request";
            if (isHumanFeedbackRequest)
            {
                var original = mutablePayload;
                reply = replyData => SendHumanFeedback(original, replyData);
            }

            // 6. Presence event.
            if (evt == "art_presence")
            {
                Emitter.Emit("art_presence", content);
                return;
            }

            // 7. Normal message flow.
            if (!IsSubscribed) return;

            var threadId = mutablePayload["thread_id"]?.ToString();
            if (string.IsNullOrWhiteSpace(threadId)) threadId = null;

            var specificEvent = threadId != null ? $"{threadId}-{evt}" : evt;
            var allEvent = threadId != null ? $"{threadId}-all" : "all";
            var hasSpecific = Emitter.ListenerCount(specificEvent) > 0;
            var hasAll = Emitter.ListenerCount(allEvent) > 0;

            if (hasSpecific || hasAll)
            {
                if (threadId != null)
                {
                    Debug.Log($"[{DateTime.Now:HH:mm:ss.fff}] [Response] Inbound '{evt}' on thread {threadId}\n" + mutablePayload);
                    var envelope = MakeEnvelope(evt, mutablePayload, content, reply);
                    if (hasSpecific) Emitter.Emit(specificEvent, envelope);
                    if (hasAll) Emitter.Emit(allEvent, envelope);
                }
                else
                {
                    if (hasSpecific) Emitter.Emit(evt, content);
                    if (hasAll)
                    {
                        Emitter.Emit("all", new JObject
                        {
                            ["event"] = evt,
                            ["content"] = content
                        });
                    }
                }
                Acknowledge(mutablePayload, "CA");
            }
            else
            {
                AddToBuffer(evt, mutablePayload, content, reply);
            }
        }

        /// <summary>
        /// Compares the frame's sender/receiver fields against our own identity.
        /// Returns <c>false</c> when the sender is us (self-echo of our own push) or when
        /// the frame carries an explicit recipient list that does not include us.
        /// </summary>
        private bool IsAddressedToUs(JObject payload)
        {
            var selfId = WebSocketHandler.GetConnection()?.ConnectionId ?? "";
            string selfName = null;
            try { selfName = Auth.GetInstance().Username; } catch { }

            bool IsSelf(string value) =>
                !string.IsNullOrEmpty(value) &&
                (value == selfId || (!string.IsNullOrEmpty(selfName) && value == selfName));

            // Sender check — drop our own messages echoed back by the server.
            if (IsSelf(payload["from"]?.ToString()) || IsSelf(payload["from_username"]?.ToString()))
            {
                Debug.Log($"[{DateTime.Now:HH:mm:ss.fff}] [Filter] Dropping self-sent message on '{ChannelConfig.ChannelName}' (from={payload["from"]}, from_username={payload["from_username"]})");
                return false;
            }

            // Receiver check — when the frame names explicit recipients, deliver only if
            // our connection id or username is among them. An empty "to" is a broadcast.
            if (payload["to"] is JArray to && to.Count > 0)
            {
                var addressed = IsSelf(payload["to_username"]?.ToString());
                foreach (var t in to)
                {
                    if (addressed) break;
                    addressed = IsSelf(t?.ToString());
                }
                if (!addressed)
                {
                    Debug.Log($"[{DateTime.Now:HH:mm:ss.fff}] [Filter] Dropping message not addressed to us on '{ChannelConfig.ChannelName}' (to={to.ToString(Formatting.None)}, to_username={payload["to_username"]})");
                    return false;
                }
            }

            return true;
        }

        private static ThreadEventEnvelope MakeEnvelope(string evt, JObject payload, JToken content, Action<object> reply)
        {
            return new ThreadEventEnvelope(
                evt,
                content,
                payload?["from"]?.ToString(),
                payload?["from_username"]?.ToString(),
                reply);
        }

        private void AddToBuffer(string evt, JObject payload, JToken content, Action<object> reply)
        {
            var threadId = payload["thread_id"]?.ToString();
            if (!string.IsNullOrWhiteSpace(threadId))
            {
                lock (_threadBuffers)
                {
                    if (!_threadBuffers.TryGetValue(threadId, out var buffer))
                    {
                        buffer = new Dictionary<string, List<BufferedThreadMessage>>();
                        _threadBuffers[threadId] = buffer;
                    }
                    if (!buffer.TryGetValue(evt, out var bucket))
                    {
                        bucket = new List<BufferedThreadMessage>();
                        buffer[evt] = bucket;
                    }
                    bucket.Add(new BufferedThreadMessage(payload, content, reply));
                }
                return;
            }

            // Channel-level buffer (mirrors the Kotlin addToBuffer field set).
            var entry = new JObject { ["content"] = content ?? new JObject() };
            var keys = new[] { "id", "from", "channel", "to", "pipeline_id", "thread_id",
                              "attempt_id", "interceptor_name", "to_user_id", "to_username" };
            foreach (var key in keys)
            {
                if (payload[key] != null)
                    entry[key] = payload[key];
            }

            if (!MessageBuffer.ContainsKey(evt))
                MessageBuffer[evt] = new List<JObject>();
            MessageBuffer[evt].Add(entry);
        }

        // -------------------------------------------------------------------------
        // Human feedback — mirrors Kotlin sendHumanFeedback() field for field.
        // -------------------------------------------------------------------------

        private void SendHumanFeedback(JObject originalReq, object replyData)
        {
            var conn = WebSocketHandler.GetConnection();

            JToken contentToken;
            if (replyData == null) contentToken = new JObject();
            else if (replyData is JToken jt) contentToken = jt;
            else contentToken = JToken.FromObject(replyData);

            var reply = new JObject
            {
                ["channel"] = originalReq["channel"],
                ["namespace"] = originalReq["namespace"],
                ["id"] = originalReq["id"],
                ["ref_id"] = originalReq["ref_id"],
                ["from"] = conn?.ConnectionId ?? "",
                ["to"] = originalReq["from"] != null && originalReq["from"].Type != JTokenType.Null
                    ? new JArray(originalReq["from"])
                    : new JArray(),
                ["to_username"] = originalReq["to_username"],
                ["from_username"] = originalReq["from_username"],
                ["return_flag"] = "HF",
                ["thread_id"] = originalReq["thread_id"],
                ["node_id"] = originalReq["node_id"],
                ["iteration_id"] = originalReq["iteration_id"],
                ["root_workflow_id"] = originalReq["root_workflow_id"],
                ["agent_node_id"] = originalReq["agent_node_id"],
                ["agent_id"] = originalReq["agent_id"],
                ["environment_id"] = originalReq["environment_id"],
                ["pipeline_id"] = originalReq["pipeline_id"],
                ["attempt_id"] = originalReq["attempt_id"],
                ["interceptor_name"] = originalReq["interceptor_name"],
                ["content"] = contentToken.ToString(Formatting.None)
            };

            var wire = reply.ToString(Formatting.None);
            Debug.Log($"[{DateTime.Now:HH:mm:ss.fff}] [Request] Human-feedback (HF) reply -> server\n{wire}");
            WebSocketHandler.SendMessage(wire);
        }
    }
}
