using System.Threading.Tasks;

namespace ART.ADK
{
    /// <summary>
    /// Top-level handle for an orchestrator-managed workflow.
    ///
    /// Each <see cref="Orchestrator"/> subscribes to its dedicated
    /// <c>orch_com_&lt;orchestratorId&gt;</c> channel and spawns <see cref="OrchestratorThread"/>s
    /// on it without requiring the channel-level <c>orchestratorEnabled</c> flag — the act of
    /// calling <c>adk.Orchestrator(id)</c> is itself the opt-in. Mirrors
    /// <c>js-adk-common/agentic/orchestrator.ts</c>.
    /// </summary>
    public sealed class Orchestrator : BaseWorkflow
    {
        /// <summary>Creates an <see cref="Orchestrator"/> bound to <paramref name="socket"/>.</summary>
        public Orchestrator(string orchestratorId, Socket socket) : base(socket)
        {
            OrchestratorId = orchestratorId;
        }

        /// <summary>The server-side identifier; the channel is <c>orch_com_&lt;orchestratorId&gt;</c>.</summary>
        public string OrchestratorId { get; }

        /// <inheritdoc/>
        protected override string ChannelName => $"orch_com_{OrchestratorId}";

         public new Orchestrator Connect()
        {
            base.Connect();
            return this;
        }

        /// <summary>
        /// Returns an <see cref="OrchestratorThread"/> for this orchestrator. Awaits the lazy
        /// subscription on first call. Unlike <see cref="Subscription.Thread"/> this <b>bypasses</b>
        /// the <c>orchestratorEnabled</c> gate because the orchestrator channel is, by
        /// construction, an orchestrator workflow.
        /// </summary>
        public async Task<OrchestratorThread> Thread(string threadId = null)
        {
            var sub = await GetSubscription();
            return sub.ThreadUnchecked(threadId);
        }
    }
}
