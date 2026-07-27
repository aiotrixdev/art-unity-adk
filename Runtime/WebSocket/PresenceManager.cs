using System;
using System.Collections.Generic;

namespace ART.ADK
{
    /// <summary>
    /// Convenience wrapper for managing presence on a subscription.
    /// Delegates to BaseSubscription.FetchPresence() internally.
    /// </summary>
    public class PresenceManager
    {
        private readonly BaseSubscription _subscription;
        private Action _stopPresence;

        public PresenceManager(BaseSubscription subscription)
        {
            _subscription = subscription;
        }

        /// <summary>
        /// Start tracking presence. The callback is invoked each time the presence list changes.
        /// Returns an action to stop tracking.
        /// </summary>
        public async void StartTracking(bool unique = true, Action<List<string>> onPresenceChanged = null)
        {
            try
            {
                _stopPresence = await _subscription.FetchPresence(unique, onPresenceChanged);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.Log($"[ART] Presence tracking error: {ex.Message}");
            }
        }

        /// <summary>
        /// Stop tracking presence for this subscription.
        /// </summary>
        public void StopTracking()
        {
            _stopPresence?.Invoke();
            _stopPresence = null;
        }

        /// <summary>
        /// Get the current presence user list (cached).
        /// </summary>
        public List<string> GetCurrentUsers() => _subscription.PresenceUsers;
    }
}
