using System.Threading.Tasks;

namespace ART.ADK
{
    /// <summary>
    /// Abstract base for client-side workflow handles (e.g. <see cref="Agent"/>,
    /// <see cref="Orchestrator"/>) that own a single underlying <see cref="Subscription"/> to a
    /// dedicated server-side channel. Concentrates the lazy-idempotent subscribe pattern in one
    /// place: subclasses only override <see cref="ChannelName"/>. Mirrors
    /// <c>js-adk-common/agentic/BaseWorkflow.ts</c>.
    /// </summary>
    public abstract class BaseWorkflow
    {
        /// <summary>Underlying socket used to (lazily) open the workflow's subscription.</summary>
        protected readonly Socket Socket;

        /// <summary>Live subscription, populated once the lazy subscribe completes.</summary>
        protected Subscription Subscription;

        /// <summary>In-flight subscribe task used to coalesce concurrent callers.</summary>
        protected Task<Subscription> SubscribePromise;

        private readonly object _connectLock = new object();

        /// <summary>Creates a <see cref="BaseWorkflow"/> bound to <paramref name="socket"/>.</summary>
        protected BaseWorkflow(Socket socket)
        {
            Socket = socket;
        }

        /// <summary>The dedicated server-side channel this workflow subscribes to.</summary>
        protected abstract string ChannelName { get; }

        /// <summary>
        /// Kicks off the (lazy) subscription to <see cref="ChannelName"/> if it has not started
        /// yet, and returns <c>this</c> for chaining. Idempotent.
        /// </summary>
        public BaseWorkflow Connect()
        {
            lock (_connectLock)
            {
                if (SubscribePromise == null)
                    SubscribePromise = SubscribeInternal();
            }
            return this;
        }

        private async Task<Subscription> SubscribeInternal()
        {
            var sub = await Socket.Subscribe(ChannelName);
            Subscription = sub as Subscription;
            return Subscription;
        }

        /// <summary>
        /// Returns the active <see cref="Subscription"/>, kicking off the subscribe call if
        /// <see cref="Connect"/> has not yet been invoked. Used by <see cref="AgentThread"/> /
        /// <see cref="Orchestrator.Thread"/>.
        /// </summary>
        public Task<Subscription> GetSubscription()
        {
            if (Subscription != null) return Task.FromResult(Subscription);
            Task<Subscription> promise;
            lock (_connectLock)
            {
                if (SubscribePromise == null)
                    SubscribePromise = SubscribeInternal();
                promise = SubscribePromise;
            }
            return promise;
        }
    }
}
