using System;
using System.Threading;
using System.Threading.Tasks;

namespace ART.ADK
{
    /// <summary>
    /// Handle for a single logical conversation with the agent.
    ///
    /// <see cref="Done"/> resolves with the agent's <see cref="AgentOutput"/> on the terminal
    /// <c>agent_output</c> event, or faults with the <see cref="AgentError"/> payload on
    /// <c>agent_error</c>. <see cref="SendFeedback"/> answers the most recent
    /// <see cref="HumanInputRequest"/> for this run by sending a <c>user_reply</c> carrying
    /// the matching <c>reply_id</c>. Multi-turn HITL stays inside one <see cref="Run"/> until
    /// the agent emits a terminal event. Mirrors <c>js-adk-common/agentic/run.ts</c>.
    /// </summary>
    public sealed class Run
    {
        private readonly AgentThread _thread;

        private readonly TaskCompletionSource<AgentOutput> _done =
            new TaskCompletionSource<AgentOutput>(TaskCreationOptions.RunContinuationsAsynchronously);

        private string _latestHumanInputRef;
        private CancellationTokenSource _humanInputTimerCts;
        private bool _closed;

        /// <summary>Creates a <see cref="Run"/> for <paramref name="thread"/>.</summary>
        public Run(AgentThread thread)
        {
            _thread = thread;
        }

        /// <summary>The outbound <c>ref_id</c> generated when the originating <c>user_input</c> was pushed.</summary>
        public string RefId { get; private set; } = "";

        /// <summary>The <c>reply_id</c> of the most recent pending <see cref="HumanInputRequest"/>, or <c>null</c>.</summary>
        public string ReplyId => _latestHumanInputRef;

        /// <summary>Whether the run has terminated.</summary>
        public bool IsClosed => _closed;

        /// <summary>Resolves on the agent's terminal <c>agent_output</c>; faults with <see cref="AgentRunException"/>.</summary>
        public Task<AgentOutput> Done() => _done.Task;

        /// <summary>Internal — set by <see cref="AgentThread"/> once the push completes.</summary>
        internal void SetRefId(string value) => RefId = value ?? "";

        /// <summary>Internal — feed an inbound event to this run.</summary>
        internal void Push(AgentEventEnvelope envelope)
        {
            if (_closed) return;

            switch (envelope.Event)
            {
                case "human_input_request":
                {
                    var content = envelope.Content as HumanInputRequest;
                    if (content == null) break;

                    _latestHumanInputRef =
                        !string.IsNullOrEmpty(content.RefId) ? content.RefId
                        : (!string.IsNullOrEmpty(content.ReplyTo) ? content.ReplyTo : null);

                    ClearHumanInputTimer();
                    var timeoutSec = content.Timeout;
                    if (timeoutSec.HasValue && timeoutSec.Value > 0)
                        StartHumanInputTimer(content, timeoutSec.Value);

                    _thread.FireHumanInput(content, this);
                    break;
                }
                case "agent_output":
                {
                    ClearHumanInputTimer();
                    _closed = true;
                    var output = envelope.Content as AgentOutput ?? new AgentOutput();
                    _done.TrySetResult(output);
                    _thread.CloseRun(this);
                    break;
                }
                case "agent_error":
                {
                    ClearHumanInputTimer();
                    _closed = true;
                    var error = envelope.Content as AgentError
                                ?? new AgentError { Code = "AGENT_ERROR", Message = "Agent error" };
                    _done.TrySetException(new AgentRunException(error));
                    _thread.CloseRun(this);
                    break;
                }
                default:
                    // agent_wait / planner_correction / unknown — non-terminal.
                    break;
            }
        }

        private void StartHumanInputTimer(HumanInputRequest content, double timeoutSec)
        {
            var cts = new CancellationTokenSource();
            _humanInputTimerCts = cts;
            var token = cts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay((int)Math.Round(timeoutSec * 1000), token);
                }
                catch (OperationCanceledException)
                {
                    return; // cleared by next event / SendFeedback
                }

                if (token.IsCancellationRequested || _closed) return;

                MainThreadDispatcher.Enqueue(() =>
                {
                    if (_closed) return;
                    var err = new AgentError
                    {
                        Code = "HUMAN_INPUT_TIMEOUT",
                        Message = $"No response received within {timeoutSec}s for human_input_request.",
                        Details = new Newtonsoft.Json.Linq.JObject
                        {
                            ["prompt"] = content.Prompt,
                            ["timeout"] = timeoutSec,
                        },
                        ThreadId = content.ThreadId ?? "",
                        RefId = "",
                        AgentId = content.AgentId ?? "",
                        ReplyTo = _latestHumanInputRef ?? "",
                    };
                    _closed = true;
                    _done.TrySetException(new AgentRunException(err));
                    _thread.CloseRun(this);
                });
            });
        }

        private void ClearHumanInputTimer()
        {
            _humanInputTimerCts?.Cancel();
            _humanInputTimerCts?.Dispose();
            _humanInputTimerCts = null;
        }

        /// <summary>
        /// Send a reply to the most recent <see cref="HumanInputRequest"/>. Throws
        /// <see cref="InvalidOperationException"/> when no input is currently pending.
        /// </summary>
        public async Task SendFeedback(object value)
        {
            var replyId = _latestHumanInputRef;
            if (string.IsNullOrEmpty(replyId))
            {
                throw new InvalidOperationException(
                    "Run.SendFeedback: no pending human_input_request to respond to.");
            }

            _latestHumanInputRef = null;
            ClearHumanInputTimer();
            await _thread.SendReply(value, replyId);
        }

        /// <summary>Internal — called by <see cref="AgentThread"/> when the run is force-closed.</summary>
        internal void Close(string reason = "Run closed before terminal event")
        {
            if (_closed) return;
            ClearHumanInputTimer();
            _closed = true;
            _done.TrySetException(new InvalidOperationException(reason));
        }
    }

    /// <summary>
    /// Exception that faults <see cref="Run.Done"/> when the agent emits a terminal
    /// <c>agent_error</c> (or a synthesized terminal error such as
    /// <c>HUMAN_INPUT_TIMEOUT</c> / <c>TRANSPORT_ERROR</c> / <c>RUN_REJECTED</c>).
    /// The typed payload is carried on <see cref="Error"/>.
    /// </summary>
    public sealed class AgentRunException : ARTException
    {
        public AgentRunException(AgentError error)
            : base($"{error.Code}: {error.Message}")
        {
            Error = error;
        }

        /// <summary>The typed agent error content that terminated the run.</summary>
        public AgentError Error { get; }
    }
}
