using System;
using System.Collections.Generic;
using System.Linq;

namespace ART.ADK
{
    /// <summary>
    /// Thread-safe pub/sub event emitter with UUID-based handler tracking.
    /// </summary>
    public class EventEmitter
    {
        private readonly Dictionary<string, List<(string id, Action<object> handler)>> _listeners
            = new Dictionary<string, List<(string, Action<object>)>>();
        private readonly object _lock = new object();

        /// <summary>Register a handler for an event. Returns a unique ID for later removal.</summary>
        public string On(string eventName, Action<object> handler)
        {
            lock (_lock)
            {
                var id = Guid.NewGuid().ToString();
                if (!_listeners.ContainsKey(eventName))
                    _listeners[eventName] = new List<(string, Action<object>)>();
                _listeners[eventName].Add((id, handler));
                return id;
            }
        }

        /// <summary>Remove a specific handler by event name and ID.</summary>
        public void Off(string eventName, string id)
        {
            lock (_lock)
            {
                if (_listeners.TryGetValue(eventName, out var list))
                    list.RemoveAll(x => x.id == id);
            }
        }

        /// <summary>Remove all handlers for an event.</summary>
        public void Off(string eventName)
        {
            lock (_lock)
            {
                _listeners.Remove(eventName);
            }
        }

        /// <summary>Remove all listeners for all events.</summary>
        public void RemoveAllListeners()
        {
            lock (_lock)
            {
                _listeners.Clear();
            }
        }

        /// <summary>Emit an event, invoking all registered handlers.</summary>
        public void Emit(string eventName, object data = null)
        {
            List<(string id, Action<object> handler)> handlers;
            lock (_lock)
            {
                if (!_listeners.TryGetValue(eventName, out var list))
                    return;
                handlers = new List<(string, Action<object>)>(list);
            }
            foreach (var h in handlers)
            {
                try { h.handler(data); }
                catch (Exception ex) { UnityEngine.Debug.LogException(ex); }
            }
        }

        /// <summary>Returns the number of listeners for an event.</summary>
        public int ListenerCount(string eventName)
        {
            lock (_lock)
            {
                return _listeners.TryGetValue(eventName, out var list) ? list.Count : 0;
            }
        }
    }
}
