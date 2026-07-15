using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
    }

    /// <summary>
    /// A single conversation thread within an <see cref="Agent"/>. Dispatches events from
    /// the agent's channel through registered <c>Listen</c> callbacks and
    /// <c>FeedbackRequest</c> handlers. At most one <see cref="Run"/> is active at a time.
    /// Mirrors <c>js-adk-common/agentic/agentThread.ts</c>.
    /// </summary>
 public class AgentThread
{
    public Agent Agent { get; }
    public string ThreadId { get; }

    private bool _masterListenerInstalled = false;
    private Task _masterListenerTask;
   private readonly List<Action<AgentEventEnvelope>> _userListeners = new List<Action<AgentEventEnvelope>>();
    private readonly List<Func<HumanInputRequest, Run, Task>> _humanInputHandlers = new List<Func<HumanInputRequest, Run, Task>>();
    private Run _activeRun = null;

    public AgentThread(Agent agent)
    {
        Agent = agent;
        ThreadId = GenerateThreadId();
    }

    private string GenerateThreadId()
    {
        return $"thread_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
    }

    /// <summary>
    /// Install the single subscription-level listener that fans out to all
    /// user Listen callbacks and the active Run. Idempotent — first caller
    /// wins; subsequent calls return the same task.
    /// </summary>
    private Task EnsureMasterListener()
    {
        if (_masterListenerTask != null) return _masterListenerTask;
        _masterListenerTask = Task.Run(async () =>
        {
            var sub = await Agent.GetSubscription();
            sub.Listen((raw) => Dispatch(raw));
            _masterListenerInstalled = true;
        });
        return _masterListenerTask;
    }

  private void Dispatch(object raw)
{
    AgentEventEnvelope envelope;

    if (raw is JObject jRaw)
    {
        envelope = AgentEventEnvelope.Parse(jRaw);
    }
    else if (raw is AgentEventEnvelope typedEnvelope)
    {
        envelope = typedEnvelope;
    }
    else
    {
        var content = raw is JObject j ? j : JObject.FromObject(raw ?? new object());
        envelope = AgentEventEnvelope.Parse(new JObject
        {
            ["event"] = "agent_output",
            ["content"] = content
        });
    }

    // Surface wire-level transport errors as typed agent_error
    if (envelope.Event == "error" || envelope.Event == "transport_error")
    {
        var c = envelope.Content as JObject ?? new JObject();
        string message = c["message"]?.ToString()
                      ?? c["error"]?.ToString()
                      ?? "WebSocket error";

        envelope = AgentEventEnvelope.Parse(new JObject
        {
            ["event"] = "agent_error",
            ["content"] = new JObject
            {
                ["code"] = "TRANSPORT_ERROR",
                ["message"] = message,
                ["thread_id"] = ThreadId,
                ["ref_id"] = "",
                ["agent_id"] = Agent.AgentId,
                ["reply_to"] = ""
            }
        });
    }

    if (!AgentEvents.IsKnownAgentEvent(envelope.Event))
    {
        Debug.LogWarning($"[adk] unknown agent event \"{envelope.Event}\" — passing through untyped.");
    }

    if (_activeRun != null)
    {
        try { _activeRun.Push(envelope); }
        catch (Exception e) { Debug.LogError($"[adk] active run dispatch failed: {e}"); }
    }

    foreach (var callback in _userListeners)
    {
        try { callback(envelope); }
        catch (Exception e) { Debug.LogError($"[adk] user listen callback threw: {e}"); }
    }
}
    /// <summary>
    /// Subscribe to typed events from this agent's channel.
    /// Multiple Listen callbacks may be registered; each receives every event.
    /// </summary>
   public async Task Listen(Action<AgentEventEnvelope> callback)
{
    await EnsureMasterListener();
    _userListeners.Add(callback);
}
    /// <summary>
    /// Register a handler invoked whenever a HumanInputRequest arrives for
    /// an active run. Call run.RespondToHumanInput(value) to continue the loop.
    /// </summary>
    public void OnHumanInput(Func<HumanInputRequest, Run, Task> handler)
    {
        _humanInputHandlers.Add(handler);
    }

    /// <summary>
    /// Internal — invoked by Run when a human_input_request arrives.
    /// </summary>
    public void FireHumanInput(HumanInputRequest req, Run run)
    {
        foreach (var handler in _humanInputHandlers)
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

    /// <summary>
    /// Internal — clear active run on terminal event.
    /// </summary>
    public void CloseRun(Run run)
    {
        if (_activeRun == run) _activeRun = null;
    }

    /// <summary>
    /// Internal — send a user_reply carrying reply_id.
    /// </summary>
   public async Task<string> SendReply(object value, string replyId)
{
    var sub = await Agent.GetSubscription();
    var content = new JObject
    {
        ["user_input"] = value != null ? JToken.FromObject(value) : JValue.CreateNull(),
        ["thread_id"] = ThreadId,
        ["reply_id"] = replyId
    };
    await sub.Push("user_reply", content);
    return "";
}

    /// <summary>
    /// Start a new run on this thread. Returns a Run handle whose Done()
    /// task resolves with the terminal AgentOutput or throws an AgentError.
    /// </summary>
public async Task<Run> Run(object userInput, string replyId = null)
{
    await EnsureMasterListener();

    if (_activeRun != null && !_activeRun.IsClosed)
    {
        Debug.LogWarning("[adk] starting new run while previous run is still active — closing previous");
        _activeRun.Close("Superseded by new run on the same thread");
    }

    var run = new Run(this);
    _activeRun = run;

    var sub = await Agent.GetSubscription();
    string eventName = replyId != null ? "user_reply" : "user_input";
    var content = new JObject
    {
        ["user_input"] = userInput != null ? JToken.FromObject(userInput) : JValue.CreateNull(),
        ["thread_id"] = ThreadId
    };
    if (replyId != null) content["reply_id"] = replyId;

    await sub.Push(eventName, content);
    run.SetRefId("");
    return run;
}
}
}

