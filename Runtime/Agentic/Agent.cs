namespace ART.ADK
{
    /// <summary>
    /// Handle for talking to a single named agent over its <c>agent_com_&lt;agentId&gt;</c>
    /// channel. Subscription is lazy and idempotent — multiple <see cref="Thread"/> calls
    /// share one underlying subscription. Mirrors <c>js-adk-common/agentic/agent.ts</c>.
    ///
    /// Typical use:
    /// <code>
    /// var agent = adk.Agent("my-agent");
    /// var thread = agent.Thread();
    /// await thread.Listen(evt => Debug.Log($"event: {evt.Event}"));
    /// var run = await thread.Run(new JObject { ["goal"] = "plan my trip" });
    /// var output = await run.Done();
    /// Debug.Log(output.Message);
    /// </code>
    /// </summary>
    public sealed class Agent : BaseWorkflow
    {
        /// <summary>Creates an <see cref="Agent"/> bound to <paramref name="socket"/>.</summary>
        public Agent(string agentId, Socket socket) : base(socket)
        {
            AgentId = agentId;
        }

        /// <summary>The server-side identifier; the channel is <c>agent_com_&lt;agentId&gt;</c>.</summary>
        public string AgentId { get; }

        /// <inheritdoc/>
        protected override string ChannelName => $"agent_com_{AgentId}";

        /// <summary>
        /// Kicks off the (lazy) subscription to this agent's channel and returns the strongly
        /// typed <see cref="Agent"/> for chaining — e.g. <c>adk.Agent(id).Connect()</c>.
        /// Mirrors the Kotlin <c>override fun connect(): Agent</c>. Idempotent.
        /// </summary>
        public new Agent Connect()
        {
            base.Connect();
            return this;
        }

        /// <summary>
        /// Returns a new <see cref="AgentThread"/> backed by this agent. When
        /// <paramref name="threadId"/> is omitted, <see cref="AgentThread"/> generates one.
        /// The same agent may host many concurrent threads.
        /// </summary>
        public AgentThread Thread(string threadId = null) => new AgentThread(this, threadId);
    }
}
