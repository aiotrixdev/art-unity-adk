using System;
using System.Threading.Tasks;
 
namespace ART.ADK
{
    /// <summary>
    /// Handle for talking to a single named agent over its <c>agent_com_&lt;agentId&gt;</c>
    /// channel. Subscription is lazy and idempotent - multiple <see cref="Thread"/> calls
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
        private readonly Socket _socket;
 
        /// <summary>Creates an <see cref="Agent"/> bound to <paramref name="socket"/>.</summary>
        public Agent(string agentId, Socket socket) : base(socket)
        {
            // JS: validateExecutionReference(agentId)
            ValidateExecutionReference(agentId);
 
            _socket = socket;
            AgentId = agentId;
        }
 
        /// <summary>The server-side identifier; the channel is <c>agent_com_&lt;agentId&gt;</c>.</summary>
        public string AgentId { get; }
 
        /// <inheritdoc/>
        protected override string ChannelName => $"agent_com_{AgentId}";
 
        /// <summary>
        /// Kicks off the (lazy) subscription to this agent's channel and returns the strongly
        /// typed <see cref="Agent"/> for chaining - e.g. <c>adk.Agent(id).Connect()</c>.
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
 
        /// <summary>
        /// Internal: <see cref="AgentThread"/> uses this to terminate active runs when the shared
        /// transport rejects the tenant before a channel event can exist.
        /// (JS: <c>_onConnectionFailure</c>)
        /// </summary>
        internal void OnConnectionFailure(Action<ConnectionError> listener)
        {
            _socket.OnConnectionFailure(listener);
        }
 
        /// <summary>Internal. (JS: <c>_offConnectionFailure</c>)</summary>
        internal void OffConnectionFailure(Action<ConnectionError> listener)
        {
            _socket.OffConnectionFailure(listener);
        }
 
        /// <summary>
        /// Upload a file scoped to this agent's configuration.
        /// (JS: <c>upload(file, opts)</c> with <c>configId</c> taken from the subscription.)
        /// </summary>
        public async Task<FileRef> Upload(byte[] file, UploadOptions opts = null)
        {
            opts = opts ?? new UploadOptions();
            opts.ConfigId = await GetConfigurationId();
            return await new Storage().Upload(file, opts);
        }
 
        /// <summary>
        /// List files scoped to this agent's configuration.
        /// (JS: <c>listFiles(opts)</c> with <c>configId</c> taken from the subscription.)
        /// </summary>
        public async Task<ListFilesResult> ListFiles(ListOptions opts = null)
        {
            opts = opts ?? new ListOptions();
            opts.ConfigId = await GetConfigurationId();
            return await new Storage().ListFiles(opts);
        }
 
        // JS: (await this._getSubscription()).configurationId
        // ASSUMPTION: BaseWorkflow exposes GetSubscription() returning an object with ConfigurationId.
        private async Task<string> GetConfigurationId()
        {
            var sub = await GetSubscription();
            return sub.ConfigurationId;
        }
 
        // JS: validateExecutionReference(agentId). Minimal placeholder check only - replace this
        // with a port of executionReference.ts so the exact same rules apply.
        private static void ValidateExecutionReference(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
                throw new ArgumentException("agentId must be a non-empty string.", nameof(reference));
        }
    }
}
 

