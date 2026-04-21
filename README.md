# ART ADK Unity Package

Real-time WebSocket communication toolkit for Unity, providing channels, presence, encryption, interception, and CRDT-backed shared objects.

## Requirements

- Unity 2022.3 LTS or later
- Newtonsoft.Json for Unity (`com.unity.nuget.newtonsoft-json`)

## Installation

### Via Unity Package Manager (local)
1. Copy the `com.arealtimetech.adk` folder into your project's `Packages/` directory, or
2. Open **Window > Package Manager > + > Add package from disk** and select `package.json`.

### Dependencies
The package depends on `com.unity.nuget.newtonsoft-json`. Unity will resolve this automatically from the Package Manager.

## Quick Start

```csharp
using ART.ADK;
using Newtonsoft.Json.Linq;
using UnityEngine;

public class MyGame : MonoBehaviour
{
    private Adk _adk;

    async void Start()
    {
        // 1. Initialize
        _adk = new Adk(new AdkConfig
        {
            Uri = "ws.arealtimetech.com",
            GetCredentials = () => new CredentialStore
            {
                OrgTitle = "my-org",
                Environment = "production",
                ProjectKey = "my-project",
                ClientID = "my-client-id",
                ClientSecret = "my-secret"
            }
        });

        // 2. Connect
        await _adk.Connect();

        // 3. Subscribe to a channel
        var sub = await _adk.Subscribe("chat:general") as Subscription;

        // 4. Listen for messages
        sub.Listen(msg =>
        {
            Debug.Log($"Event: {msg["event"]}, Content: {msg["content"]}");
        });

        // 5. Push a message
        await sub.Push("message", new JObject
        {
            ["text"] = "Hello from Unity!"
        });
    }

    async void OnDestroy()
    {
        if (_adk != null)
            await _adk.Disconnect();
    }
}
```

## Architecture

| File | Purpose |
|------|---------|
| `Adk.cs` | Main entry point - init, connect, disconnect, subscribe, intercept |
| `Socket.cs` | WebSocket lifecycle, message routing, heartbeat, fallback to long-poll |
| `BaseSubscription.cs` | Shared subscription logic - push, ACK, presence, buffering |
| `Subscription.cs` | Standard channel - Listen(), Bind(), event-based messaging |
| `SharedObjectChannel.cs` | CRDT-backed shared objects for collaborative state |
| `Interceptor.cs` | Middleware-style request/response interception |
| `PresenceManager.cs` | Convenience wrapper for user presence tracking |
| `EncryptionHelper.cs` | AES-256-GCM encrypt/decrypt |
| `Auth.cs` | JWT authentication with token refresh |
| `EventEmitter.cs` | Thread-safe pub/sub event system |
| `MainThreadDispatcher.cs` | Marshals callbacks to Unity main thread |
| `LongPollClient.cs` | HTTP long-polling fallback |
| `CRDT/` | Full CRDT engine with RGA arrays and map/object support |

## Channel Namespaces

Use `:` separator for namespaced channels: `"chat:general"`, `"game:lobby"`.

## Targeted/Secure Channels

```csharp
await sub.Push("message", data, new PushOptions("target-user"));
```

## Presence

```csharp
var presence = new PresenceManager(subscription);
presence.StartTracking(unique: true, onPresenceChanged: users =>
{
    Debug.Log($"Online: {string.Join(", ", users)}");
});
```

## Interception

```csharp
await _adk.Intercept("my-interceptor", (request, resolve, reject) =>
{
    // Process, then resolve or reject
    resolve(new JObject { ["processed"] = true });
});
```

## Shared Objects (CRDT)

```csharp
var sharedChannel = await _adk.Subscribe("my-shared-obj") as SharedObjectChannel;
var state = sharedChannel.State();
state["score"].Set(100);
state["players"].Push("Alice");
await sharedChannel.Flush();
```

## Encryption

```csharp
var keyPair = await _adk.GenerateKeyPair();
// Encryption/decryption happens automatically on secure channels
```

## Platform Notes

- **Android/iOS/PC**: Full WebSocket support via `System.Net.WebSockets.ClientWebSocket`
- **WebGL**: `ClientWebSocket` is NOT supported in WebGL. A JavaScript WebSocket bridge plugin is required. This is a known Unity limitation.

## Thread Safety

All WebSocket callbacks are marshalled to Unity's main thread via `MainThreadDispatcher`. The dispatcher is auto-created when you instantiate `Adk`.

## Testing

Open the Unity Test Runner (**Window > General > Test Runner**) to run the included unit tests. For integration testing, attach `ArtAdkTestRunner` to a GameObject and configure credentials in the Inspector.
