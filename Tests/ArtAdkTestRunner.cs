using System.Threading.Tasks;
using ART.ADK;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ART.ADK.Tests
{
    /// <summary>
    /// Test scene script demonstrating the full ADK lifecycle:
    /// Connect -> Subscribe -> Push -> Listen -> Unsubscribe -> Disconnect.
    ///
    /// Attach this MonoBehaviour to a GameObject in a test scene and fill in
    /// the credentials in the Inspector.
    /// </summary>
    public class ArtAdkTestRunner : MonoBehaviour
    {
        [Header("ART Configuration")]
        [Tooltip("ART server URI (e.g. ws.arealtimetech.com)")]
        public string serverUri = "ws.arealtimetech.com";

        [Tooltip("Organization title")]
        public string orgTitle = "";

        [Tooltip("Environment (e.g. production, development)")]
        public string environment = "";

        [Tooltip("Project key")]
        public string projectKey = "";

        [Tooltip("Client ID for authentication")]
        public string clientID = "";

        [Tooltip("Client Secret for authentication")]
        public string clientSecret = "";

        [Tooltip("Channel name to test with")]
        public string testChannel = "test:general";

        private Adk _adk;
        private BaseSubscription _subscription;

        private async void Start()
        {
            Debug.Log("[TestRunner] Starting ART ADK test...");

            // 1. Initialize
            _adk = new Adk(new AdkConfig
            {
                Uri = serverUri,
                GetCredentials = () => new CredentialStore
                {
                    OrgTitle = orgTitle,
                    Environment = environment,
                    ProjectKey = projectKey,
                    ClientID = clientID,
                    ClientSecret = clientSecret
                }
            });

            // 2. Register event hooks
            _adk.OnConnected += conn =>
            {
                Debug.Log($"[TestRunner] Connected! ConnectionId: {conn.ConnectionId}");
            };

            _adk.OnClosed += () =>
            {
                Debug.Log("[TestRunner] Connection closed.");
            };

            _adk.OnError += err =>
            {
                Debug.Log($"[TestRunner] Error: {err}");
            };

            // 3. Connect
            try
            {
                Debug.Log("[TestRunner] Connecting...");
                await _adk.Connect();
                Debug.Log($"[TestRunner] State: {_adk.GetState()}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[TestRunner] Connection failed: {ex.Message}");
                return;
            }

            // 4. Subscribe to test channel
            try
            {
                Debug.Log($"[TestRunner] Subscribing to '{testChannel}'...");
                _subscription = await _adk.Subscribe(testChannel);
                Debug.Log("[TestRunner] Subscribed successfully!");

                // 5. Listen for all events
                if (_subscription is Subscription sub)
                {
                    sub.Listen(msg =>
                    {
                        Debug.Log($"[TestRunner] Received: event={msg["event"]}, content={msg["content"]}");
                    });

                    // 6. Bind to a specific event
                    sub.Bind("chat", data =>
                    {
                        Debug.Log($"[TestRunner] Chat event: {data}");
                    });
                }

                // 7. Push a test message
                Debug.Log("[TestRunner] Pushing test message...");
                await _subscription.Push("chat", new JObject
                {
                    ["message"] = "Hello from Unity ADK!",
                    ["timestamp"] = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
                Debug.Log("[TestRunner] Message pushed!");

                // 8. Wait a bit, then demonstrate presence
                await Task.Delay(2000);

                Debug.Log("[TestRunner] Fetching presence...");
                var presenceManager = new PresenceManager(_subscription);
                presenceManager.StartTracking(unique: true, onPresenceChanged: users =>
                {
                    Debug.Log($"[TestRunner] Presence: {string.Join(", ", users)}");
                });

                // 9. Wait, then clean up
                await Task.Delay(5000);

                Debug.Log("[TestRunner] Unsubscribing...");
                await _subscription.Unsubscribe();
                Debug.Log("[TestRunner] Unsubscribed.");

                // 10. Disconnect
                Debug.Log("[TestRunner] Disconnecting...");
                await _adk.Disconnect();
                Debug.Log($"[TestRunner] Disconnected. State: {_adk.GetState()}");
                Debug.Log("[TestRunner] Test complete!");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[TestRunner] Error during test: {ex.Message}");
            }
        }

        private async void OnDestroy()
        {
            if (_adk != null && _adk.GetState() != "stopped")
            {
                await _adk.Disconnect();
            }
        }
    }
}
