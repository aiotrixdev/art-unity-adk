using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ART.ADK
{
    /// <summary>
    /// Envelope delivered to orchestrator thread listeners. The C# equivalent of the
    /// Kotlin <c>mapOf("event" to event, "content" to content)</c> map whose content
    /// carries an injected <c>reply</c> lambda for human-feedback requests — delegates
    /// cannot live inside a <see cref="JToken"/>, so the reply is a typed property here.
    /// </summary>
    public sealed class ThreadEventEnvelope
    {
        public ThreadEventEnvelope(string evt, JToken content, string from, string fromUsername, Action<object> reply)
        {
            Event = evt ?? "";
            Content = content;
            From = from;
            FromUsername = fromUsername;
            Reply = reply;
        }

        /// <summary>The wire event name.</summary>
        public string Event { get; }

        /// <summary>The parsed message content.</summary>
        public JToken Content { get; }

        /// <summary>Sender connection id, when present on the frame.</summary>
        public string From { get; }

        /// <summary>Sender username, when present on the frame.</summary>
        public string FromUsername { get; }

        /// <summary>
        /// Non-null when the server requested human feedback (<c>requestFeedback</c> /
        /// <c>human_input_request</c>). Invoke with the answer payload (e.g.
        /// <c>new JObject { ["message"] = value }</c>) to send the <c>HF</c> reply frame.
        /// </summary>
        public Action<object> Reply { get; }

        /// <summary>Whether this message is a human-feedback request.</summary>
        public bool IsHumanFeedbackRequest => Reply != null;
    }

    /// <summary>
    /// Per-thread handle for orchestrator-enabled channels.
    ///
    /// Wraps a <see cref="Subscription"/> and scopes every push and listen call to a single
    /// logical thread within the channel. Threads are first-class on the wire — every
    /// outbound frame carries a top-level <c>thread_id</c> (mirroring the Kotlin
    /// <c>PushConfig.threadId</c>) and every listener observes only events whose frame is
    /// tagged with the matching id. Created via <see cref="Subscription.ThreadUnchecked"/>.
    /// Mirrors <c>artlibrary/agentic/OrcThread.kt</c>.
    /// </summary>
    public class OrchestratorThread
    {
        private readonly Subscription _subscription;
        public string ThreadId { get; }
        private readonly HashSet<string> _attachedEvents = new HashSet<string>();
        private bool _disposed = false;

        public OrchestratorThread(Subscription subscription, string threadId = null)
        {
            _subscription = subscription;
            // Kotlin: threadId ?: UUID.randomUUID().toString()
            ThreadId = threadId ?? Guid.NewGuid().ToString();
        }

        private void EnsureActive()
        {
            if (_disposed)
                throw new InvalidOperationException($"OrchestratorThread {ThreadId} has been disposed");
        }

        /// <summary>
        /// Push an event scoped to this thread. The data payload is sent verbatim (any JSON
        /// value, like the Kotlin <c>Any?</c>); the thread id travels as a top-level frame
        /// field, NOT inside the data. Awaits the server "SA" ack like the Kotlin push.
        /// </summary>
        public async Task<JObject> Push(string eventName, object data, PushConfig options = null)
        {
            EnsureActive();

            JToken jData;
            if (data == null) jData = new JObject();
            else if (data is JToken jt) jData = jt;
            else jData = JToken.FromObject(data);

            var opts = new PushOptions
            {
                To = options?.To ?? new List<string>(),
                ThreadId = ThreadId
            };

            // [Request] complete JSON payload immediately before sending.
            Debug.Log($"[{DateTime.Now:HH:mm:ss.fff}] [Request] Pushing '{eventName}' on thread {ThreadId} -> [{string.Join(", ", opts.To)}]\n" + jData);

            var result = await _subscription.Push(eventName, jData, opts);

            // [Response] send result (after the server ack) for this push.
            Debug.Log($"[{DateTime.Now:HH:mm:ss.fff}] [Response] Push result for '{eventName}' on thread {ThreadId}\n" + result);
            return result;
        }

        /// <summary>
        /// Listen to every event on this thread. The callback receives a
        /// <see cref="ThreadEventEnvelope"/> (event + content + optional HITL reply).
        /// </summary>
        public void Listen(Action<object> callback)
        {
            EnsureActive();
            _subscription.AttachThreadListener(ThreadId, callback);
            _attachedEvents.Add("all");
        }

        /// <summary>Bind to a single event on this thread.</summary>
        public void Bind(string eventName, Action<object> callback)
        {
            EnsureActive();
            _subscription.AttachThreadBind(ThreadId, eventName, callback);
            _attachedEvents.Add(eventName);
        }

        public void Remove(string eventName, Action<object> callback = null)
        {
            if (_disposed) return;
            _subscription.DetachThreadListener(ThreadId, eventName, callback);
            if (callback == null)
                _attachedEvents.Remove(eventName);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var eventName in _attachedEvents)
            {
                _subscription.DetachThreadListener(ThreadId, eventName);
            }
            _attachedEvents.Clear();
            _subscription.UnregisterThread(ThreadId);
        }

        public bool IsDisposed() => _disposed;

        public string GetId() => ThreadId;
    }
}
