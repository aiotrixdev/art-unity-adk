using System;
using System.Collections.Generic;
using UnityEngine;

namespace ART.ADK
{
    /// <summary>
    /// Marshals callbacks from background threads onto Unity's main thread.
    /// Attach this component to a persistent GameObject in your scene, or call
    /// MainThreadDispatcher.Initialize() to auto-create one.
    /// </summary>
    public class MainThreadDispatcher : MonoBehaviour
    {
        private static MainThreadDispatcher _instance;
        private static readonly Queue<Action> _queue = new Queue<Action>();
        private static readonly object _lock = new object();

        public static void Initialize()
        {
            if (_instance != null) return;
            var go = new GameObject("[ART.ADK MainThreadDispatcher]");
            _instance = go.AddComponent<MainThreadDispatcher>();
            DontDestroyOnLoad(go);
        }

        /// <summary>Enqueue an action to be executed on the main thread.</summary>
        public static void Enqueue(Action action)
        {
            if (action == null) return;
            lock (_lock)
            {
                _queue.Enqueue(action);
            }
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            lock (_lock)
            {
                while (_queue.Count > 0)
                {
                    try { _queue.Dequeue()?.Invoke(); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }
            }
        }
    }
}
