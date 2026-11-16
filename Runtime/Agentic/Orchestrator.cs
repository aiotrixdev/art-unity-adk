using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace ART.ADK
{
    /// <summary>
    /// Top-level handle for an orchestrator-managed workflow.
    ///
    /// Each <see cref="Orchestrator"/> subscribes to its dedicated
    /// <c>orch_com_&lt;orchestratorId&gt;</c> channel and spawns <see cref="OrchestratorThread"/>s
    /// on it without requiring the channel-level <c>orchestratorEnabled</c> flag; the act of
    /// calling <c>adk.Orchestrator(id)</c> is itself the opt-in. Mirrors
    /// <c>js-adk-common/agentic/orchestrator.ts</c>.
    /// </summary>
    public sealed class Orchestrator : BaseWorkflow
    {
        /// <summary>Creates an <see cref="Orchestrator"/> bound to <paramref name="socket"/>.</summary>
        /// <exception cref="ArgumentException">Thrown when <paramref name="orchestratorId"/> is not a valid execution reference.</exception>
        public Orchestrator(string orchestratorId, Socket socket) : base(socket)
        {
            ExecutionReference.Validate(orchestratorId);
            OrchestratorId = orchestratorId;
        }

        /// <summary>The server-side identifier; the channel is <c>orch_com_&lt;orchestratorId&gt;</c>.</summary>
        public string OrchestratorId { get; }

        /// <inheritdoc/>
        protected override string ChannelName => $"orch_com_{OrchestratorId}";

        /// <summary>Connects the underlying workflow and returns this instance for fluent chaining.</summary>
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

private UploadOptions ScopeUploadOptions(UploadOptions opts)
{
    var src = opts ?? new UploadOptions();
    return new UploadOptions
    {
        Filename = src.Filename,
        ContentType = src.ContentType,
        ConfigType = src.ConfigType,
        ConfigId = OrchestratorId,   // always scoped to this orchestrator
        Scopes = src.Scopes != null ? new List<string>(src.Scopes) : new List<string>(),
        TtlSeconds = src.TtlSeconds,
        TimeoutMs = src.TimeoutMs,
        OnProgress = src.OnProgress,
        ThreadId = src.ThreadId
    };
}
        /// <summary>
        /// Uploads a file scoped to this orchestrator. The <c>ConfigId</c> in
        /// <paramref name="opts"/> is always set to <see cref="OrchestratorId"/>.
        /// </summary>
//       public Task<FileRef> Upload(byte[] file, UploadOptions opts = null)
// {
//     var scoped = opts?.Clone() ?? new UploadOptions();
//     scoped.ConfigId = OrchestratorId;
//     return new Storage().Upload(file, scoped);
// }

public Task<FileRef> Upload(byte[] file, UploadOptions opts = null)
{
    return new Storage().Upload(file, ScopeUploadOptions(opts));
}
private ListOptions ScopeListOptions(ListOptions opts)
{
    var src = opts ?? new ListOptions();
    return new ListOptions
    {
        // copy every field/property from src here
        ConfigId = OrchestratorId   // always scoped to this orchestrator
    };
}

public Task<ListFilesResult> ListFiles(ListOptions opts = null)
{
    return new Storage().ListFiles(ScopeListOptions(opts));
}
    }
}
