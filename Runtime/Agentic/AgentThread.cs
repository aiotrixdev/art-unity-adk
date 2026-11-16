using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ART.ADK
{
    /// <summary>
    /// Callback fired for every event delivered to this thread. The envelope's
    /// <see cref="AgentEventEnvelope.Content"/> is one of the typed variants when
    /// <see cref="AgentEventEnvelope.IsKnown"/> is <c>true</c>; an
    /// <see cref="UnknownAgentEvent"/> otherwise.
    /// </summary>
    public delegate void AgentUserListener(AgentEventEnvelope envelope);

    /// <summary>
    /// Callback fired when the agent emits a <see cref="HumanInputRequest"/> for the active
    /// <see cref="Run"/>. The handler collects input and calls <c>run.SendFeedback(value)</c>.
    /// May be synchronous (return <c>null</c>) or asynchronous (return the in-flight task).
    /// </summary>
    public delegate Task AgentHumanInputHandler(HumanInputRequest request, Run run);

    /// <summary>Optional dependencies supplied when starting a <see cref="AgentThread.Run"/>.</summary>
    public sealed class RunDeps
    {
        /// <summary>When set, the outbound event becomes <c>user_reply</c> and carries this <c>reply_id</c>.</summary>
        public string ReplyId { get; set; }

        /// <summary>Expected value: "playground". (JS: execution_origin)</summary>
        public string ExecutionOrigin { get; set; }

        /// <summary>Files attached to this run. (JS: file_meta)</summary>
        public List<MediaRef> FileMeta { get; set; }
    }

    /// <summary>
    /// Operational state of a thread (JS: <c>ThreadState</c>). Remove this class if you already
    /// have it in your events file.
    /// </summary>
    public sealed class ThreadState
    {
        [JsonProperty("thread_id")] public string ThreadId { get; set; }
        [JsonProperty("phase")] public string Phase { get; set; }
        [JsonProperty("source")] public string Source { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("occurred_at")] public string OccurredAt { get; set; }
        [JsonProperty("sequence")] public int? Sequence { get; set; }
        [JsonProperty("reason")] public string Reason { get; set; }
        [JsonProperty("workspace_id")] public string WorkspaceId { get; set; }
        [JsonProperty("agent_id")] public string AgentId { get; set; }

        /// <summary>Any other fields the server sends.</summary>
        [JsonExtensionData] public IDictionary<string, JToken> Extra { get; set; }

        public ThreadState Clone() => (ThreadState)MemberwiseClone();
    }

    /// <summary>Known <see cref="ThreadState.Phase"/> values.</summary>
    public static class ThreadPhase
    {
        public const string Idle = "idle";
        public const string Submitted = "submitted";
        public const string WaitingForApproval = "waiting_for_approval";
        public const string WaitingForWorkspace = "waiting_for_workspace";
        public const string WaitingForAgent = "waiting_for_agent";
        public const string Completed = "completed";
        public const string Failed = "failed";
    }

    /// <summary>Known <see cref="ThreadState.Source"/> values.</summary>
    public static class ThreadSource
    {
        public const string Client = "client";
        public const string Agent = "agent";
        public const string Workspace = "workspace";
    }

    /// <summary>
    /// A single conversation thread within an <see cref="Agent"/>. Dispatches events from
    /// the agent's channel through registered <c>Listen</c> callbacks and
    /// <c>FeedbackRequest</c> handlers. At most one <see cref="Run"/> is active at a time.
    /// Mirrors <c>js-adk-common/agentic/agentThread.ts</c>.
    /// </summary>
    public class AgentThread : IDisposable
    {
        public Agent Agent { get; }
        public string ThreadId { get; }

        private bool _disposed;
        private Subscription _subscription;
        private string _executionOrigin;

        private readonly object _masterListenerLock = new object();
        private Task _masterListenerTask;
        private readonly List<Action<AgentEventEnvelope>> _userListeners = new List<Action<AgentEventEnvelope>>();
        private readonly List<Action<ThreadState>> _stateListeners = new List<Action<ThreadState>>();
        private readonly List<Func<HumanInputRequest, Run, Task>> _humanInputHandlers = new List<Func<HumanInputRequest, Run, Task>>();
        private Run _activeRun = null;
        private ThreadState _currentState;
        private bool _traceAttached;

        // Terminates the active run when the shared transport fails before any channel event can exist.
        private bool _connectionListenerAttached;
        private bool _dispatchingConnectionError;
        private readonly Action<ConnectionError> _connectionListener;

        public bool IsDisposed => _disposed;

        public AgentThread(Agent agent, string threadId = null)
        {
            Agent = agent;
            ThreadId = string.IsNullOrWhiteSpace(threadId) ? GenerateThreadId() : threadId;
            _currentState = MakeState(ThreadPhase.Idle, ThreadSource.Client, "Ready");
            _connectionListener = OnConnectionError;
        }

        private static string GenerateThreadId() => Guid.NewGuid().ToString();

        private void EnsureActive()
        {
            if (_disposed) throw new InvalidOperationException("Agent thread is disposed");
        }

        /// <summary>
        /// Dispose only this thread's listeners; the shared channel remains connected.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _activeRun?.Close("Thread disposed");
            _activeRun = null;
            _userListeners.Clear();
            _stateListeners.Clear();
            _humanInputHandlers.Clear();

            if (_connectionListenerAttached)
            {
                Agent.OffConnectionFailure(_connectionListener);
                _connectionListenerAttached = false;
            }

            // Thread-scoped emitter keys can be removed precisely. The channel-level "all" listener
            // can't be removed per-callback in Unity's emitter, so Dispatch() ignores events once
            // disposed instead.
            _subscription?.DetachThreadListener(ThreadId, "all");
            if (_traceAttached) _subscription?.DetachThreadListener(ThreadId, "trace");
            _traceAttached = false;
        }

        // ---------------------------------------------------------------------
        // State
        // ---------------------------------------------------------------------

        private ThreadState MakeState(
            string phase, string source, string message,
            string reason = null, string workspaceId = null, string agentId = null)
        {
            return new ThreadState
            {
                ThreadId = ThreadId,
                Phase = phase,
                Source = source,
                Message = message,
                OccurredAt = DateTime.UtcNow.ToString("o"),
                Reason = reason,
                WorkspaceId = workspaceId,
                AgentId = agentId
            };
        }

        private void UpdateState(ThreadState state)
        {
            if (!string.IsNullOrEmpty(state.ThreadId) && state.ThreadId != ThreadId) return;

            var previousSequence = _currentState.Sequence ?? -1;
            var nextSequence = state.Sequence ?? previousSequence + 1;
            if (nextSequence < previousSequence) return;

            var next = state.Clone();
            next.ThreadId = ThreadId;
            next.Sequence = nextSequence;
            _currentState = next;

            foreach (var listener in _stateListeners.ToArray())
            {
                try { listener(_currentState.Clone()); }
                catch (Exception e) { Debug.LogError($"[adk] state listener threw: {e}"); }
            }
        }

        /// <summary>Latest operational state for this thread (a copy).</summary>
        public ThreadState GetState() => _currentState.Clone();

        /// <summary>
        /// Subscribe to the latest operational state for this thread. Returns an unsubscribe action.
        /// </summary>
        public async Task<Action> ListenState(Action<ThreadState> callback, bool emitCurrent = true)
        {
            await EnsureMasterListener();
            EnsureActive();
            _stateListeners.Add(callback);
            if (emitCurrent) callback(_currentState.Clone());
            return () => _stateListeners.Remove(callback);
        }

        // ---------------------------------------------------------------------
        // Storage (scoped to this thread)
        // ---------------------------------------------------------------------

        public async Task<FileRef> Upload(byte[] file, UploadOptions opts = null)
        {
            var subscription = await Agent.GetSubscription();
            opts = opts ?? new UploadOptions();
            opts.ConfigId = subscription.ConfigurationId;
            opts.ThreadId = ThreadId;
            return await new Storage().Upload(file, opts);
        }

        public async Task<ListFilesResult> ListFiles(ListOptions opts = null)
        {
            var subscription = await Agent.GetSubscription();
            opts = opts ?? new ListOptions();
            opts.ConfigId = subscription.ConfigurationId;
            opts.ThreadId = ThreadId;
            return await new Storage().ListFiles(opts);
        }

        // ---------------------------------------------------------------------
        // Listener plumbing
        // ---------------------------------------------------------------------

        /// <summary>
        /// Install the single subscription-level listener that fans out to all
        /// user Listen callbacks and the active Run. Idempotent - first caller
        /// wins; subsequent calls return the same task.
        /// </summary>
        private async Task EnsureMasterListener()
        {
            EnsureActive();

            Task listenerTask;
            lock (_masterListenerLock)
            {
                if (_masterListenerTask == null)
                    _masterListenerTask = InstallMasterListener();
                listenerTask = _masterListenerTask;
            }

            try
            {
                await listenerTask;
            }
            catch
            {
                // A failed subscription must not poison this thread permanently: later
                // Listen/Run calls should be able to retry.
                lock (_masterListenerLock)
                {
                    if (_masterListenerTask == listenerTask)
                        _masterListenerTask = null;
                }
                throw;
            }
        }

        private async Task InstallMasterListener()
        {
            var sub = await Agent.GetSubscription();
            EnsureActive();

            _subscription = sub;
            sub.Listen(raw => Dispatch(raw));
            sub.AttachThreadListener(ThreadId, Dispatch);

            if (!_connectionListenerAttached)
            {
                Agent.OnConnectionFailure(_connectionListener);
                _connectionListenerAttached = true;
            }
        }

        // JS: connectionListener - turns a transport failure into a typed agent_error for the active run.
        private void OnConnectionError(ConnectionError error)
        {
            if (_activeRun == null) return;

            var message = error.Message;
            var content = new JObject
            {
                ["type"] = "agent_error_response",
                ["status"] = "error",
                ["code"] = error.Code,
                ["message"] = message,
                ["details"] = new JObject
                {
                    ["reason"] = message,
                    ["source"] = "connection",
                    ["status"] = error.Status.HasValue ? (JToken)error.Status.Value : JValue.CreateNull(),
                    ["execution_status"] = "unknown"
                },
                ["thread_id"] = ThreadId,
                ["ref_id"] = "",
                ["agent_id"] = Agent.AgentId,
                ["reply_to"] = ""
            };

            _dispatchingConnectionError = true;
            try
            {
                Dispatch(new JObject { ["event"] = "agent_error", ["content"] = content });
            }
            finally
            {
                _dispatchingConnectionError = false;
            }
        }

        private void Dispatch(object raw)
        {
            if (_disposed) return;

            AgentEventEnvelope envelope;

            if (raw is ThreadEventEnvelope threadEnvelope)
            {
                envelope = AgentEventEnvelope.Parse(new JObject
                {
                    ["event"] = threadEnvelope.Event,
                    ["content"] = threadEnvelope.Content ?? new JObject()
                });
            }
            else if (raw is JObject jRaw)
            {
                envelope = AgentEventEnvelope.Parse(jRaw);
            }
            else if (raw is AgentEventEnvelope typedEnvelope)
            {
                envelope = typedEnvelope;
            }
            else
            {
                var content = JObject.FromObject(raw ?? new object());
                envelope = AgentEventEnvelope.Parse(new JObject
                {
                    ["event"] = "agent_output",
                    ["content"] = content
                });
            }

            // Surface wire-level transport errors as typed agent_error
            if (envelope.Event == "error" || envelope.Event == "transport_error")
            {
                var c = (envelope.Content as UnknownAgentEvent)?.Content
                        ?? envelope.Content as JObject
                        ?? new JObject();
                string message = c["message"]?.ToString()
                              ?? c["error"]?.ToString()
                              ?? "WebSocket error";

                envelope = AgentEventEnvelope.Parse(new JObject
                {
                    ["event"] = "agent_error",
                    ["content"] = new JObject
                    {
                        ["type"] = "agent_error_response",
                        ["status"] = "error",
                        ["code"] = "TRANSPORT_ERROR",
                        ["message"] = message,
                        ["details"] = c,
                        ["thread_id"] = ThreadId,
                        ["ref_id"] = "",
                        ["agent_id"] = Agent.AgentId,
                        ["reply_to"] = ""
                    }
                });
            }

            LogInboundEvent(envelope);

            if (!AgentEvents.IsKnownAgentEvent(envelope.Event))
            {
                Debug.LogWarning($"[adk] unknown agent event \"{envelope.Event}\" - passing through untyped.");
            }

            ApplyStateFromEvent(envelope);

            if (_activeRun != null)
            {
                try { _activeRun.Push(envelope); }
                catch (Exception e) { Debug.LogError($"[adk] active run dispatch failed: {e}"); }
            }

            foreach (var callback in _userListeners.ToArray())
            {
                try { callback(envelope); }
                catch (Exception e) { Debug.LogError($"[adk] user listen callback threw: {e}"); }
            }
        }

        // JS: the thread_state / human_input_request / agent_wait / agent_output / agent_error branches.
        // Routing is on the decoded content type (the envelope's Event can be the transport event).
        private void ApplyStateFromEvent(AgentEventEnvelope envelope)
        {
            switch (envelope.Content)
            {
                case HumanInputRequest req:
                    UpdateState(MakeState(
                        ThreadPhase.WaitingForApproval, ThreadSource.Agent, "Waiting for your input",
                        reason: req.Prompt));
                    break;

                case AgentWait wait:
                {
                    var workspaceId = TryGetWorkspaceId(wait);
                    var hasWorkspace = !string.IsNullOrEmpty(workspaceId);
                    UpdateState(MakeState(
                        hasWorkspace ? ThreadPhase.WaitingForWorkspace : ThreadPhase.WaitingForAgent,
                        hasWorkspace ? ThreadSource.Workspace : ThreadSource.Agent,
                        hasWorkspace ? "Waiting for workspace" : "Waiting for another agent",
                        reason: wait.Reason,
                        workspaceId: workspaceId,
                        agentId: wait.WaitingForAgentId));
                    break;
                }

                case AgentOutput _:
                    UpdateState(MakeState(ThreadPhase.Completed, ThreadSource.Agent, "Completed"));
                    break;

                case AgentError err:
                {
                    var fromConnection = _dispatchingConnectionError || IsConnectionSource(err);
                    UpdateState(MakeState(
                        ThreadPhase.Failed,
                        fromConnection ? ThreadSource.Client : ThreadSource.Agent,
                        fromConnection ? "Connection unavailable" : "Execution failed",
                        reason: err.Message));
                    break;
                }

                case UnknownAgentEvent unknown:
                {
                    var type = unknown.Content?["type"]?.ToString();
                    if (envelope.Event == "thread_state" || type == "thread_state")
                    {
                        try
                        {
                            var state = unknown.Content?.ToObject<ThreadState>();
                            if (state != null) UpdateState(state);
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"[adk] could not parse thread_state: {e.Message}");
                        }
                    }
                    break;
                }
            }
        }

        // AgentWait.progress.workspace_id - read defensively since the C# model shape may vary.
        private static string TryGetWorkspaceId(AgentWait wait)
        {
            try
            {
                var j = JObject.FromObject(wait);
                var progress = (j["progress"] ?? j["Progress"]) as JObject;
                var id = progress?["workspace_id"] ?? progress?["WorkspaceId"];
                var s = id?.ToString();
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch { return null; }
        }

        // agent_error.details.source === 'connection'
        private static bool IsConnectionSource(AgentError err)
        {
            try
            {
                var j = JObject.FromObject(err);
                var details = (j["details"] ?? j["Details"]) as JObject;
                var source = details?["source"] ?? details?["Source"];
                return source?.ToString() == "connection";
            }
            catch { return false; }
        }

        private void LogInboundEvent(AgentEventEnvelope envelope)
        {
            Debug.Log($"[adk] agent envelop {envelope}");

            switch (envelope.Content)
            {
                case AgentOutput output:
                    Debug.Log(
                        $"[adk] agent response received (thread={ThreadId}, ref_id={output.RefId})\n" +
                        $"{output.Message}\n" +
                        output.ToJson().ToString());
                    break;
                case AgentError error:
                    Debug.LogError(
                        $"[adk] agent error received (thread={ThreadId}, ref_id={error.RefId})\n" +
                        $"{error.Code}: {error.Message}\n" +
                        JObject.FromObject(error).ToString());
                    break;
                case UnknownAgentEvent unknown:
                    Debug.Log($"[adk] agent event unknown '{envelope.Event}' (thread={ThreadId})\n{unknown.Content}");
                    break;
                default:
                    Debug.Log($"[adk] agent event '{envelope.Event}' (thread={ThreadId})\n" +
                              JObject.FromObject(envelope.Content ?? new object()));
                    break;
            }
        }

        // ---------------------------------------------------------------------
        // Public listening API
        // ---------------------------------------------------------------------

        /// <summary>
        /// Subscribe to typed events from this agent's channel.
        /// Multiple Listen callbacks may be registered; each receives every event.
        /// </summary>
        public async Task Listen(Action<AgentEventEnvelope> callback)
        {
            await EnsureMasterListener();
            EnsureActive();
            _userListeners.Add(callback);
        }

        /// <summary>
        /// Deprecated: use <see cref="ListenState"/> for operational UI. Trace is reserved for diagnostics.
        /// The callback receives the trace content.
        /// </summary>
        public async Task ListenTrace(Action<object> callback)
        {
            EnsureActive();
            var sub = await Agent.GetSubscription();
            EnsureActive();
            _subscription = sub;
            _traceAttached = true;

            Action<object> wrapped = data =>
            {
                if (_disposed) return;
                callback(data is ThreadEventEnvelope te ? te.Content : data);
            };

            sub.Bind("trace", wrapped);
            sub.AttachThreadBind(ThreadId, "trace", wrapped);
        }

        /// <summary>
        /// Register a handler invoked whenever a HumanInputRequest arrives for
        /// an active run. Call run.SendFeedback(value) to continue the loop.
        /// </summary>
        public void FeedbackRequest(AgentHumanInputHandler handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _humanInputHandlers.Add(handler.Invoke);
        }

        /// <summary>
        /// Unity-compatible alias for <see cref="FeedbackRequest"/>, which mirrors Kotlin's
        /// <c>feedbackRequest</c> API.
        /// </summary>
        public void OnHumanInput(Func<HumanInputRequest, Run, Task> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _humanInputHandlers.Add(handler);
        }

        /// <summary>Internal - invoked by Run when a human_input_request arrives.</summary>
        public void FireHumanInput(HumanInputRequest req, Run run)
        {
            foreach (var handler in _humanInputHandlers.ToArray())
            {
                try
                {
                    var task = handler(req, run);
                    if (task != null)
                    {
                        task.ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                                Debug.LogError($"[adk] onHumanInput handler rejected: {t.Exception}");
                        }, TaskContinuationOptions.OnlyOnFaulted);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[adk] onHumanInput handler threw: {e}");
                }
            }
        }

        /// <summary>Kotlin-compatible name for <see cref="FireHumanInput"/>.</summary>
        public void FireRequestFeedback(HumanInputRequest req, Run run) => FireHumanInput(req, run);

        /// <summary>Internal - clear active run on terminal event.</summary>
        public void CloseRun(Run run)
        {
            if (_activeRun == run) _activeRun = null;
        }

        /// <summary>Internal - send a user_reply carrying reply_id.</summary>
        public async Task<string> SendReply(object value, string replyId)
        {
            EnsureActive();
            var sub = await Agent.GetSubscription();
            EnsureActive();

            var content = new JObject
            {
                ["user_input"] = value != null ? JToken.FromObject(value) : JValue.CreateNull(),
                ["thread_id"] = ThreadId,
                ["reply_id"] = replyId
            };

            var ack = await sub.Push("user_reply", content, new PushOptions
            {
                ThreadId = ThreadId,
                ExecutionOrigin = _executionOrigin
            });
            return ack?["ref_id"]?.ToString() ?? "";
        }

        /// <summary>
        /// Start a new run on this thread. Returns a Run handle whose Done()
        /// task resolves with the terminal AgentOutput or throws an AgentError.
        /// </summary>
        public async Task<Run> Run(object userInput, RunDeps deps = null)
        {
            _executionOrigin = deps?.ExecutionOrigin;
            await EnsureMasterListener();
            var sub = await Agent.GetSubscription();
            EnsureActive();

            if (_activeRun != null && !_activeRun.IsClosed)
            {
                Debug.LogWarning("[adk] starting new run while previous run is still active - closing previous");
                _activeRun.Close("Superseded by new run on the same thread");
            }

            var run = new Run(this);
            _activeRun = run;
            UpdateState(MakeState(ThreadPhase.Submitted, ThreadSource.Client, "Request submitted"));

            var replyId = deps?.ReplyId;
            string eventName = !string.IsNullOrEmpty(replyId) ? "user_reply" : "user_input";
            var content = new JObject
            {
                ["user_input"] = userInput != null ? JToken.FromObject(userInput) : JValue.CreateNull(),
                ["thread_id"] = ThreadId
            };
            if (!string.IsNullOrEmpty(replyId)) content["reply_id"] = replyId;

            try
            {
                var ack = await sub.Push(eventName, content, new PushOptions
                {
                    ThreadId = ThreadId,
                    FileMeta = deps?.FileMeta,
                    ExecutionOrigin = deps?.ExecutionOrigin
                });
                run.SetRefId(ack?["ref_id"]?.ToString() ?? "");
                return run;
            }
            catch (Exception error)
            {
                if (error is ConnectionError connectionError) OnConnectionError(connectionError);
                run.Close(string.IsNullOrEmpty(error.Message) ? "The request could not be sent." : error.Message);
                CloseRun(run);
                throw;
            }
        }

        /// <summary>
        /// Convenience overload retained for callers that supplied a reply id directly.
        /// Prefer <see cref="Run(object, RunDeps)"/> to match the Kotlin API.
        /// </summary>
        public Task<Run> Run(object userInput, string replyId)
            => Run(userInput, replyId == null ? null : new RunDeps { ReplyId = replyId });
    }
}