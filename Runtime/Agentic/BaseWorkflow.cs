using System;
using System.Threading.Tasks;

namespace ART.ADK
{
    /// <summary>
    /// Shared base for workflow entities (Agent, Orchestrator, ...) that talk over a single
    /// named channel. Subscription is lazy and idempotent: many callers share one in-flight
    /// subscribe. Mirrors <c>agentic/channelEntity.ts</c> (BaseWorkflow).
    /// </summary>
    public abstract class BaseWorkflow
    {
        protected readonly Socket Socket;

        /// <summary>The resolved subscription (null until the first subscribe succeeds).</summary>
        protected Subscription ActiveSubscription;

        /// <summary>The in-flight / completed subscribe task (null when not started or after a failure).</summary>
        protected Task<Subscription> SubscribePromise;

        private readonly object _subscribeLock = new object();

        protected BaseWorkflow(Socket socket)
        {
            Socket = socket;
        }

        /// <summary>The channel this workflow subscribes to (JS: abstract channelName()).</summary>
        protected abstract string ChannelName { get; }

        /// <summary>
        /// Starts the subscription if it hasn't started yet and returns this instance for chaining.
        /// Idempotent. A failed subscribe clears the cached task so a later call can retry.
        /// </summary>
        public BaseWorkflow Connect()
        {
            lock (_subscribeLock)
            {
                if (SubscribePromise != null) return this;

                var promise = SubscribeCore();
                SubscribePromise = promise;

                // JS: .catch(error => { this.subscribePromise = undefined; throw error; })
                // Attached after assignment so a synchronous failure still clears it.
                _ = promise.ContinueWith(t =>
                {
                    if (t.IsFaulted || t.IsCanceled)
                    {
                        lock (_subscribeLock)
                        {
                            if (ReferenceEquals(SubscribePromise, promise))
                                SubscribePromise = null;
                        }
                        var _ = t.Exception; // observe so it isn't reported as unobserved
                    }
                }, TaskContinuationOptions.ExecuteSynchronously);
            }
            return this;
        }

        private async Task<Subscription> SubscribeCore()
        {
            var sub = await Socket.Subscribe(ChannelName);
            var typed = sub as Subscription
                ?? throw new InvalidOperationException(
                    $"Channel '{ChannelName}' did not produce a Subscription (got {sub?.GetType().Name ?? "null"}).");
            ActiveSubscription = typed;
            return typed;
        }

        /// <summary>
        /// Waits for the socket, then returns the subscription, starting it if needed.
        /// (JS: <c>_getSubscription()</c>, marked @internal.)
        /// </summary>
        internal async Task<Subscription> GetSubscription()
        {
            await Socket.WaitForConnection();

            if (ActiveSubscription != null) return ActiveSubscription;

            Task<Subscription> promise;
            lock (_subscribeLock)
            {
                if (SubscribePromise == null) Connect();
                promise = SubscribePromise;
            }
            return await promise;
        }
    }
}