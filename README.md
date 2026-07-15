# ART - A Realtime Tech communication (Unity ADK)

A powerful, real-time Unity C# library for building highly collaborative, secure, and agentic applications. **ART ADK** provides a robust set of tools for WebSocket management, Conflict-free Replicated Data Types (CRDTs) for multi-user state synchronization, end-to-end encrypted messaging, and AI agentic workflows (incorporating single Agents and Orchestrators).

---

## Installation

### 1. Via Unity Package Manager (UPM)
To install the package via Git URL:
1. Open the Unity Editor and open the Package Manager (**Window > Package Manager**).
2. Click the **+** button in the top-left corner and select **Add package from git URL...**.
3. Enter the repository URL with the desired version tag or branch:
   ```
   https://github.com/aiotrixdev/art-unity-adk.git#1.0.0
   ```

### 2. Via manifest.json
Alternatively, add the dependency directly to your project's `Packages/manifest.json` under `dependencies`:
```json
{
  "dependencies": {
    "com.arealtimetech.adk": "https://github.com/aiotrixdev/art-unity-adk.git#1.0.0"
  }
}
```

### 3. Local Installation (Development)
Clone or copy the `com.arealtimetech.adk` package folder directly into your project's `Packages/` directory, or select its `package.json` file via **Add package from disk...** in the Package Manager.

---

## Configuration

### 1. Initialization
Configure the core ADK client with your environment credentials:

```csharp
using ART.ADK;

var adkConfig = new AdkConfig
{
    Uri = "your_server_webSocket.com",
    AuthToken = "YOUR_AUTH_TOKEN", // Optional initial token
    GetCredentials = () => new CredentialStore
    {
        Environment = "YOUR_ENVIRONMENT",
        ProjectKey = "YOUR_PROJECT_KEY",
        OrgTitle = "YOUR_ORG",
        ClientID = "CLIENT_ID",
        ClientSecret = "CLIENT_SECRET",
        AccessToken = "OPTIONAL_INITIAL_TOKEN"
    }
};

var adk = new Adk(adkConfig);
```

### 2. Generate Client Credentials
Create a configuration JSON file containing your client credentials from the [ART Live Dashboard](https://dev.arealtimetech.com). Save it under the path configured in `ArtConstants.CONFIG_JSON_PATH` (default is `config.json`):

```json
{
  "Environment": "YOUR_ENV_NAME",
  "ProjectKey": "YOUR_PROJECT_KEY",
  "OrgTitle": "YOUR_ORG",
  "ClientID": "xxxxxxxxxx",
  "ClientSecret": "xxxxxxxxxxx"
}
```

### 3. Load Credentials at Runtime
You can load the configuration file asynchronously using `adk.LoadConfig()`:

```csharp
CredentialStore credentials = await adk.LoadConfig();
adk.SetCredentials(credentials);
```

### 4. Authenticate & Obtain a Passcode
Generate an authentication passcode before connecting. In the example app:

```csharp
string passcode = await AuthService.ConnectWithPasscode(
    username, username, username,
    credentials, $"https://{serverUri}");
```

---

## Quick Start

### Connecting & Managing Lifecycle

Use asynchronous methods (`async/await`) to manage the WebSocket connections:

```csharp
using System;
using System.Threading.Tasks;
using ART.ADK;
using UnityEngine;

public class AdkConnectionManager : MonoBehaviour
{
    private Adk _adk;

    async void Start()
    {
        // 1. Initialize configuration and ADK
        var config = new AdkConfig
        {
            Uri = "ws.arealtimetech.com",
            AuthToken = "YOUR_PASSCODE",
            GetCredentials = () => new CredentialStore
            {
                Environment = "production",
                ProjectKey = "my-project",
                OrgTitle = "my-org",
                ClientID = "my-client-id",
                ClientSecret = "my-secret",
                AccessToken = "YOUR_PASSCODE"
            }
        };

        _adk = new Adk(config);

        // 2. Wire connection event handlers
        _adk.OnConnected += (ConnectionDetail connection) =>
        {
            Debug.Log($"Connected successfully! ConnectionID: {connection.ConnectionId}");
        };

        _adk.OnClosed += () =>
        {
            Debug.Log("Connection closed.");
        };

        _adk.OnError += (object error) =>
        {
            Debug.LogError($"ADK Error: {error}");
        };

        // 3. Connect to WebSockets
        try
        {
            await _adk.Connect();
        }
        catch (Exception ex)
        {
            Debug.LogError($"Connection failed: {ex.Message}");
        }
    }

    async void OnDestroy()
    {
        if (_adk != null)
        {
            await _adk.Disconnect();
        }
    }
}
```

### User Presence

Track presence changes using the `PresenceManager` or directly from your subscriptions:

```csharp
// Fetch and track active users on a subscription
var unsubscribePresence = await subscription.FetchPresence(
    unique: true,
    callback: (List<string> users) =>
    {
        Debug.Log($"Presence received. Active users: {users.Count}");
        foreach (var user in users)
        {
            Debug.Log($"User online: {user}");
        }
    }
);

// Call the returned action to stop tracking presence later
unsubscribePresence();
```

### Subscribing to a Channel

`adk.Subscribe` returns a `BaseSubscription`. For standard channels, cast it to `Subscription`. For shared-object CRDT channels, cast it to `SharedObjectChannel`.

```csharp
// Subscribe to a namespaced channel
BaseSubscription sub = await adk.Subscribe("live-cursor-channel");

if (sub is Subscription standardSub)
{
    // Listen to cursor-move events
    standardSub.Bind("cursor-move", (object payload) =>
    {
        // Handle cursor position updates (payload as JObject/JToken)
    });
}
```

Unsubscribe when you no longer need the channel:

```csharp
await sub.Unsubscribe();
```

### Pushing Messages

Push events to a channel with payload data:

```csharp
using Newtonsoft.Json.Linq;

// Push a text message
await sub.Push("message", new JObject { ["text"] = "Hello" });
```

Target a specific user (required for secure and targeted channels — exactly one recipient):

```csharp
await sub.Push(
    eventName: "message",
    data: new JObject { ["text"] = "Hi Bob" },
    options: new PushOptions("bob")
);
```

*Note: Attempting to target zero or more than one recipient on secure or targeted channels will throw an exception.*

### Receiving Messages

Bind to a specific event on the subscription's emitter:

```csharp
sub.Emitter.On("message", (object data) =>
{
    Debug.Log($"Got message: {data}");
});
```

Or, on standard channels, listen to every event:

```csharp
if (sub is Subscription standardSub)
{
    standardSub.Listen((JObject data) =>
    {
        Debug.Log($"Event: {data["event"]} | Content: {data["content"]}");
    });
}
```

Unbind a specific listener:

```csharp
if (sub is Subscription standardSub)
{
    standardSub.Remove("message");
}
```

### Encrypted Channels

You can automatically generate key pairs and encrypt payloads directly over the wire using the internal encryption engine:

```csharp
// Generate an asymmetric key-pair internally and save the public key on the server
KeyPairType myKeyPair = await adk.GenerateKeyPair();

// Encrypt and push secure data
object response = await adk.PushForSecureLine(
    eventName: "CONFIDENTIAL_UPDATE",
    data: "My secret message",
    listen: true
);
```

Then push to a secure channel. Encryption and decryption occur transparently:

```csharp
BaseSubscription secure = await adk.Subscribe("YOUR_SECURE_CHANNEL");

if (secure is Subscription secureSub)
{
    await secureSub.Push(
        eventName: "message",
        data: new JObject { ["text"] = "This is private" },
        options: new PushOptions("bob") // Secure channels require exactly one recipient
    );

    secureSub.Emitter.On("message", (object data) =>
    {
        Debug.Log($"Decrypted message: {data}");
    });
}
```

---

## Shared Object Channels (CRDT)

Shared-object channels expose a JSON-like document that every subscriber edits concurrently; conflicts are resolved automatically via the SDK's CRDT engine.

```csharp
BaseSubscription sub = await adk.Subscribe("YOUR_CRDT_CHANNEL");

if (sub is SharedObjectChannel sharedSub)
{
    CRDTEngine crdt = sharedSub.Crdt;
    CRDTProxy state = sharedSub.State();

    // 1. Write
    state["document"].Set("My Doc");
    await sharedSub.Flush();

    // 2. Read once
    var query = sharedSub.Query("document");
    object snapshot = await query.execute();
    Debug.Log($"Snapshot: {snapshot}");

    // 3. Observe updates
    Action dispose = query.listen(data =>
    {
        Debug.Log($"Document updated: {data}");
    });

    // Call dispose() when done observing
    dispose();
}
```

### Array Operations

```csharp
CRDTProxy items = sharedSub.State()["items"];

// Write operations
items.Push("alpha");                  // Append
items.Unshift("zero");                // Prepend
object popped = items.Pop();          // Remove last and return value
object removed = items.RemoveAt(2);   // Remove by index and return value

// Sync changes
await sharedSub.Flush();

Debug.Log($"Length: {items.Length}");
```

---

## Interceptors

Register middleware-style interceptors for request/response message processing before they are dispatched:

```csharp
await adk.Intercept("my-interceptor", (JObject request, Action<object> resolve, Action<string> reject) =>
{
    Debug.Log($"Intercepted message: {request}");
    
    // Resolve to pass through, or Reject to cancel
    resolve(request);
});
```

---

## Agents

Agents let you drive a single conversational AI agent over its own real-time channel (`agent_com_<agentId>`). Each agent exposes threads; a thread runs one logical conversation at a time and streams typed events, terminal responses, and human-in-the-loop (HITL) feedback.

*Note: Connect and authenticate the ADK client first. Agents and Orchestrators run on top of an active `Adk` session.*

### Connect an Agent

```csharp
// Opens the agent_com_<agentId> subscription
Agent agent = adk.Agent("agent-id").Connect();

// Open a conversation thread (each thread has a unique ThreadId)
AgentThread thread = agent.Thread();
```

### Run a Prompt

`thread.Run(...)` starts a run and returns a `Run` handle. `run.Done()` yields until the agent emits its terminal `agent_output` event, or throws `AgentRunException` on `agent_error` or transport failure.

```csharp
try
{
    Run run = await thread.Run("Summarise my last invoice");
    AgentOutput output = await run.Done(); // Suspends until final response
    Debug.Log($"Agent Reply: {output.Message}");
}
catch (AgentRunException e)
{
    Debug.LogError($"Agent failed: {e.Message} (Code: {e.Error.Code})");
}
```

### Listen to Streamed Events

Register one or more `Listen` callbacks to receive every typed event on the thread. The callback receives an `AgentEventEnvelope` object.

```csharp
await thread.Listen((AgentEventEnvelope envelope) =>
{
    switch (envelope.Event)
    {
        // Terminal success
        case "agent_output":
            var output = envelope.Content as AgentOutput;
            Debug.Log($"Final Output: {output.Message}");
            break;

        // Terminal failure
        case "agent_error":
            var error = envelope.Content as AgentError;
            Debug.LogError($"Agent Error: {error.Message}");
            break;

        // Mid-run prompt for the user (Human-in-the-loop request)
        case "human_input_request":
            var hitl = envelope.Content as HumanInputRequest;
            Debug.Log($"Needs input: {hitl.Prompt}");
            break;

        // Waiting on another agent
        case "agent_wait":
            var wait = envelope.Content as AgentWait;
            Debug.Log($"Waiting: {wait.Reason}");
            break;

        // Planner re-plan
        case "planner_correction":
            var correction = envelope.Content as PlannerCorrection;
            Debug.Log($"Correction: {correction.Reason}");
            break;

        // Unknown events arrive as UnknownAgentEvent
        default:
            Debug.Log($"Event {envelope.Event}: {envelope.Content}");
            break;
    }
});
```

---

## Orchestrator

Orchestrators coordinate multi-agent workflows over a dedicated channel (`orch_com_<orchestratorId>`). An orchestrator thread is a lightweight, event-driven channel you push into and listen on directly—ideal for fan-out / fan-in flows and HITL prompts routed through a coordinator.

### Connect and Open a Thread

```csharp
// Opens the orch_com_<orchestratorId> subscription
Orchestrator orchestrator = adk.Orchestrator("workflow-1").Connect();

// Start or resume a thread (pass an optional threadId to resume)
OrchestratorThread thread = await orchestrator.Thread("optional-thread-id");
Debug.Log($"Thread ID: {thread.GetId()}");
```

### Push and Receive

```csharp
// Stream every event on this thread
thread.Listen((object envelope) =>
{
    if (envelope is ThreadEventEnvelope env)
    {
        Debug.Log($"Thread event: {env.Event} | Content: {env.Content}");
    }
});

// Or bind to a single event type
thread.Bind("agent_output", (object data) =>
{
    Debug.Log($"Output: {data}");
});

// Kick off the workflow
await thread.Push(
    eventName: "user_input",
    data: new JObject { ["user_input"] = "Plan my week" }
);
```

Target specific recipients with `PushConfig`:

```csharp
await thread.Push(
    eventName: "user_input",
    data: new JObject { ["user_input"] = "hello" },
    options: new PushConfig { To = new List<string> { "kiran" } }
);
```

### Human-in-the-loop (HITL) Replies
In an orchestrator thread, human-feedback requests are delivered with an SDK-injected `Reply` callback on the `ThreadEventEnvelope`. To answer the prompt, call `env.Reply`:

```csharp
thread.Listen((object raw) =>
{
    if (raw is ThreadEventEnvelope env && env.IsHumanFeedbackRequest)
    {
        Debug.Log($"HITL requested: {env.Content}");
        
        // Respond to the request
        env.Reply(new JObject { ["message"] = "Yes, proceed." });
    }
});
```

### Clean Up

```csharp
// Remove a single listener
thread.Remove("agent_output");

// Tear down the thread and detach all listeners
thread.Dispose();
```

---

## API Reference

| Class/Type | Purpose |
| :--- | :--- |
| **Adk** | Top-level facade — connect, disconnect, subscribe, intercept, key management |
| **AdkConfig** | Connection configuration (URI, credentials, auth token) |
| **CredentialStore** | Immutable holder for org / project / client credentials |
| **BaseSubscription** | Base type for all channel subscriptions (push, fetchPresence, unsubscribe) |
| **Subscription** | Default / secure / targeted / group channel — Listen(), Bind(), emitter.on() |
| **SharedObjectChannel** | CRDT-backed shared-object channel — State(), Query(), Flush() |
| **CRDTEngine** | Underlying conflict-free replicated data engine |
| **CRDTProxy** | Indexer-based path accessor for reading / mutating CRDT state |
| **PushOptions** | Push options — `To`, `FromUsername`, `ThreadId` for targeted/secure channels |
| **PushConfig** | Thread-scoped push options — `To`, `ThreadID`, `TimeoutMs` |
| **KeyPairType** | KeyPair container (public/private key) for secure channels |
| **ConnectionDetail** | Emitted on 'connection' — contains connectionId, instanceId, tenant info |
| **Agent** | Agent handle bound to `agent_com_<agentId>` — Connect(), Thread() |
| **AgentThread** | Thread handle for agent — Run(), Listen(), OnHumanInput() |
| **Run** | Representation of an active conversation task — Done(), SendFeedback() |
| **Orchestrator** | Orchestrator handle bound to `orch_com_<orchestratorId>` — Connect(), Thread() |
| **OrchestratorThread** | Thread channel — Push(), Listen(), Bind(), Remove(), Dispose(), GetId() |

---

## Platform & Runtime Notes

### WebGL Compatibility
- **Desktop/Mobile**: Native WebSocket support via C# `System.Net.WebSockets`.
- **WebGL**: `System.Net.WebSockets` is **not** supported natively in WebGL builds. A custom JavaScript WebSocket bridge plugin is required for browser execution.

### Thread Safety & MainThreadDispatcher
Unity is fundamentally single-threaded for engine API calls. All WebSocket connection and message callbacks received on background threads are automatically marshaled to Unity's main thread via `MainThreadDispatcher.Enqueue()`. The dispatcher is initialized automatically upon `Adk` creation.

### Testing
Use the Unity Test Runner (**Window > General > Test Runner**) to run the included unit tests. To test live environments, configure your environment details in the `config.json` file.

---

## License

Licensed under the Apache License 2.0.
