using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ART.ADK
{
    /// <summary>
    /// Typed event envelopes emitted on the <c>agent_com_&lt;agentId&gt;</c> channel.
    ///
    /// The wire shape is <c>{ event: string, content: object }</c>. The <c>event</c>
    /// string is the discriminator; each variant's <c>content</c> mirrors the
    /// corresponding server-side type. Mirrors <c>js-adk-common/agentic/events.ts</c>.
    /// </summary>
    public static class AgentEvents
    {
        /// <summary>The known event names emitted on agent communication channels.</summary>
        public static readonly IReadOnlyList<string> Names = new List<string>
        {
            "agent_output",
            "agent_error",
            "human_input_request",
            "agent_wait",
            "planner_correction",
        };

        private static readonly HashSet<string> KnownSet = new HashSet<string>(Names);

        /// <summary>Returns <c>true</c> if <paramref name="name"/> is a known agent event name.</summary>
        public static bool IsKnown(string name) => name != null && KnownSet.Contains(name);

        public static bool IsKnownAgentEvent(string name) => name != null && KnownSet.Contains(name);
    }

    /// <summary>Modality the agent expects a human to respond with.</summary>
    public enum ExpectedResponseType
    {
        Text,
        Choice,
        Confirm,
        File,
        Structured,
    }

    /// <summary>
    /// Routing / correlation fields injected into every egress <c>content</c> body
    /// by the server. May be empty strings if the upstream task lacked them.
    /// </summary>
    public interface IEnvelopeMeta
    {
        string ThreadId { get; }
        string RefId { get; }
        string AgentId { get; }
        string ReplyTo { get; }
    }

    /// <summary>Successful terminal response from an agent run.</summary>
    public sealed class AgentOutput : IEnvelopeMeta
    {
        public string Type => "agent_general_response";
        public string Status => "final_response";

        /// <summary>The terminal natural-language message returned by the agent.</summary>
        public string Message { get; set; } = "";

        /// <summary>Optional structured payload returned alongside <see cref="Message"/>.</summary>
        public JObject Data { get; set; }

        /// <summary>Optional metadata bag (model name, token usage, etc.).</summary>
        public JObject Metadata { get; set; }

        public string ThreadId { get; set; } = "";
        public string RefId { get; set; } = "";
        public string AgentId { get; set; } = "";
        public string ReplyTo { get; set; } = "";

        public static AgentOutput FromJson(JObject m)
        {
            m ??= new JObject();
            return new AgentOutput
            {
                Message = m["message"]?.ToString() ?? "",
                Data = m["data"] as JObject,
                Metadata = m["metadata"] as JObject,
                ThreadId = m["thread_id"]?.ToString() ?? "",
                RefId = m["ref_id"]?.ToString() ?? "",
                AgentId = m["agent_id"]?.ToString() ?? "",
                ReplyTo = m["reply_to"]?.ToString() ?? "",
            };
        }

        public JObject ToJson()
        {
            var o = new JObject
            {
                ["thread_id"] = ThreadId,
                ["ref_id"] = RefId,
                ["agent_id"] = AgentId,
                ["reply_to"] = ReplyTo,
                ["type"] = Type,
                ["status"] = Status,
                ["message"] = Message,
            };
            if (Data != null) o["data"] = Data;
            if (Metadata != null) o["metadata"] = Metadata;
            return o;
        }
    }

    /// <summary>Error response from an agent run; terminal for the originating <see cref="Run"/>.</summary>
    public sealed class AgentError : IEnvelopeMeta
    {
        public string Type => "agent_error_response";
        public string Status => "error";

        /// <summary>Framework or agent-supplied error code.</summary>
        public string Code { get; set; } = "";

        /// <summary>Human-readable description of the failure.</summary>
        public string Message { get; set; } = "";

        /// <summary>Optional structured details bag.</summary>
        public JObject Details { get; set; }

        public string ThreadId { get; set; } = "";
        public string RefId { get; set; } = "";
        public string AgentId { get; set; } = "";
        public string ReplyTo { get; set; } = "";

        public static AgentError FromJson(JObject m)
        {
            m ??= new JObject();
            return new AgentError
            {
                Code = m["code"]?.ToString() ?? "",
                Message = m["message"]?.ToString() ?? "",
                Details = m["details"] as JObject,
                ThreadId = m["thread_id"]?.ToString() ?? "",
                RefId = m["ref_id"]?.ToString() ?? "",
                AgentId = m["agent_id"]?.ToString() ?? "",
                ReplyTo = m["reply_to"]?.ToString() ?? "",
            };
        }

        public override string ToString() => $"AgentError({Code}): {Message}";
    }

    /// <summary>An interactive prompt the agent is awaiting a human reply on.</summary>
    public sealed class HumanInputRequest : IEnvelopeMeta
    {
        public string Type => "human_input_request";
        public string Status => "awaiting_input";

        /// <summary>The question to present to the user.</summary>
        public string Prompt { get; set; } = "";

        /// <summary>Optional context bag passed alongside the prompt.</summary>
        public JObject Context { get; set; }

        /// <summary>Parsed expected modality (falls back to <see cref="ExpectedResponseType.Text"/>).</summary>
        public ExpectedResponseType ExpectedResponseType { get; set; } = ExpectedResponseType.Text;

        /// <summary>The raw <c>expected_response_type</c> string as received from the server.</summary>
        public string ExpectedResponseTypeRaw { get; set; } = "text";

        /// <summary>Optional client-side timeout in seconds (enforced by <see cref="Run"/>).</summary>
        public double? Timeout { get; set; }

        /// <summary>Optional JSON schema when <see cref="ExpectedResponseType"/> is <c>Structured</c>.</summary>
        public JToken Schema { get; set; }

        public string ThreadId { get; set; } = "";
        public string RefId { get; set; } = "";
        public string AgentId { get; set; } = "";
        public string ReplyTo { get; set; } = "";

        private static ExpectedResponseType ParseExpected(string raw)
        {
            switch (raw)
            {
                case "choice": return ExpectedResponseType.Choice;
                case "confirm": return ExpectedResponseType.Confirm;
                case "file": return ExpectedResponseType.File;
                case "structured": return ExpectedResponseType.Structured;
                case "text":
                default: return ExpectedResponseType.Text;
            }
        }

        public static HumanInputRequest FromJson(JObject m)
        {
            m ??= new JObject();
            var raw = m["expected_response_type"]?.ToString() ?? "text";
            double? timeout = null;
            var t = m["timeout"];
            if (t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float))
                timeout = t.Value<double>();

            return new HumanInputRequest
            {
                Prompt = m["prompt"]?.ToString() ?? "",
                Context = m["context"] as JObject,
                ExpectedResponseType = ParseExpected(raw),
                ExpectedResponseTypeRaw = raw,
                Timeout = timeout,
                Schema = m["schema"],
                ThreadId = m["thread_id"]?.ToString() ?? "",
                RefId = m["ref_id"]?.ToString() ?? "",
                AgentId = m["agent_id"]?.ToString() ?? "",
                ReplyTo = m["reply_to"]?.ToString() ?? "",
            };
        }
    }

    /// <summary>Notification that the agent is awaiting another agent's response.</summary>
    public sealed class AgentWait : IEnvelopeMeta
    {
        public string Type => "agent_wait_response";
        public string Status => "waiting_for_agent";

        public string WaitingForAgentId { get; set; } = "";
        public string InvocationId { get; set; }
        public string Reason { get; set; }
        public double? Timeout { get; set; }
        public JObject Progress { get; set; }

        public string ThreadId { get; set; } = "";
        public string RefId { get; set; } = "";
        public string AgentId { get; set; } = "";
        public string ReplyTo { get; set; } = "";

        public static AgentWait FromJson(JObject m)
        {
            m ??= new JObject();
            double? timeout = null;
            var t = m["timeout"];
            if (t != null && (t.Type == JTokenType.Integer || t.Type == JTokenType.Float))
                timeout = t.Value<double>();

            return new AgentWait
            {
                WaitingForAgentId = m["waiting_for_agent_id"]?.ToString() ?? "",
                InvocationId = m["invocation_id"]?.ToString(),
                Reason = m["reason"]?.ToString(),
                Timeout = timeout,
                Progress = m["progress"] as JObject,
                ThreadId = m["thread_id"]?.ToString() ?? "",
                RefId = m["ref_id"]?.ToString() ?? "",
                AgentId = m["agent_id"]?.ToString() ?? "",
                ReplyTo = m["reply_to"]?.ToString() ?? "",
            };
        }
    }

    /// <summary>Server-initiated request that the planner correct its course of action.</summary>
    public sealed class PlannerCorrection : IEnvelopeMeta
    {
        public string Type => "planner_correction_request";

        public bool CorrectionRequired { get; set; }
        public string Reason { get; set; } = "";
        public string NewGoal { get; set; }
        public List<string> SuggestedAgents { get; set; }

        public string ThreadId { get; set; } = "";
        public string RefId { get; set; } = "";
        public string AgentId { get; set; } = "";
        public string ReplyTo { get; set; } = "";

        public static PlannerCorrection FromJson(JObject m)
        {
            m ??= new JObject();
            List<string> suggested = null;
            if (m["suggested_agents"] is JArray arr)
            {
                suggested = new List<string>();
                foreach (var e in arr)
                    suggested.Add(e?.ToString() ?? "");
            }

            return new PlannerCorrection
            {
                CorrectionRequired = m["correction_required"]?.Type == JTokenType.Boolean
                    ? m["correction_required"].Value<bool>()
                    : false,
                Reason = m["reason"]?.ToString() ?? "",
                NewGoal = m["new_goal"]?.ToString(),
                SuggestedAgents = suggested,
                ThreadId = m["thread_id"]?.ToString() ?? "",
                RefId = m["ref_id"]?.ToString() ?? "",
                AgentId = m["agent_id"]?.ToString() ?? "",
                ReplyTo = m["reply_to"]?.ToString() ?? "",
            };
        }
    }

    /// <summary>
    /// Catch-all envelope when the server emits an event the SDK does not yet model.
    /// The raw <c>content</c> object is preserved verbatim.
    /// </summary>
    public sealed class UnknownAgentEvent
    {
        public UnknownAgentEvent(string evt, JObject content)
        {
            Event = evt;
            Content = content ?? new JObject();
        }

        public string Event { get; }
        public JObject Content { get; }
    }

    /// <summary>
    /// Discriminated envelope wrapping any inbound agent event.
    ///
    /// Use <see cref="IsKnown"/> to gate on whether <see cref="Content"/> is one of the typed
    /// variants (<see cref="AgentOutput"/>, <see cref="AgentError"/>,
    /// <see cref="HumanInputRequest"/>, <see cref="AgentWait"/>, <see cref="PlannerCorrection"/>)
    /// or an <see cref="UnknownAgentEvent"/> catch-all.
    /// </summary>
    public sealed class AgentEventEnvelope
    {
        public AgentEventEnvelope(string evt, object content)
        {
            Event = evt;
            Content = content;
        }

        /// <summary>The wire event name — matches one of <see cref="AgentEvents.Names"/> when known.</summary>
        public string Event { get; }

        /// <summary>The typed envelope when <see cref="Event"/> is recognised, else an <see cref="UnknownAgentEvent"/>.</summary>
        public object Content { get; }

        /// <summary>Whether <see cref="Content"/> is one of the typed variants.</summary>
        public bool IsKnown => AgentEvents.IsKnown(Event);

        /// <summary>
        /// Parses a raw <c>{event, content}</c> envelope into a typed
        /// <see cref="AgentEventEnvelope"/>. Unknown event names produce an
        /// <see cref="UnknownAgentEvent"/>.
        /// </summary>
        public static AgentEventEnvelope Parse(JObject raw)
        {
            raw ??= new JObject();
            var evt = raw["event"]?.ToString() ?? "";
            var contentMap = raw["content"] as JObject ?? new JObject();

            switch (evt)
            {
                case "agent_output":
                    return new AgentEventEnvelope(evt, AgentOutput.FromJson(contentMap));
                case "agent_error":
                    return new AgentEventEnvelope(evt, AgentError.FromJson(contentMap));
                case "human_input_request":
                    return new AgentEventEnvelope(evt, HumanInputRequest.FromJson(contentMap));
                case "agent_wait":
                    return new AgentEventEnvelope(evt, AgentWait.FromJson(contentMap));
                case "planner_correction":
                    return new AgentEventEnvelope(evt, PlannerCorrection.FromJson(contentMap));
                default:
                    return new AgentEventEnvelope(evt, new UnknownAgentEvent(evt, contentMap));
            }
        }
    }

  
public sealed class AgentEventOrUnknown 
{
    public string Event { get; }
    public object Content { get; }

     public AgentEventOrUnknown(string evt, JObject content)
        {
            Event = evt;
            Content = content ?? new JObject();
        }
}

public sealed class AgentErrorResponse
{
    public string Type { get; }
    public string Status { get; }
    public string Code { get; }
    public string Message { get; }

    public Dictionary<string, object> Details { get; }
    public string ThreadId { get; }
    public string RefId { get; }
    public string AgentId { get; }
    public string ReplyTo { get; }

    
    public AgentErrorResponse(
        string type,
        string status,
        string code,
        string message,
        string threadId,
        string refId,
        string agentId,
        string replyTo,
        Dictionary<string, object> details = null
    )
    {
        Type = type;
        Status = status;
        Code = code;
        Message = message;
        ThreadId = threadId;
        RefId = refId;
        AgentId = agentId;
        ReplyTo = replyTo;
       Details = details ?? new Dictionary<string, object>();

    }
}

}
