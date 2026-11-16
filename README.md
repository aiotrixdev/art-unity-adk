# ART - A Realtime Tech communication (Unity ADK)

The ART Unity ADK connects Unity apps to ART (A Realtime Tech), a realtime communication platform for building intelligent applications with WebSocket-based messaging, AI Agents, AI Orchestrators, presence tracking, end-to-end encrypted channels, and CRDT-backed shared objects.

With the ADK you can:

- Send and receive messages in real time
- See who is online in a channel
- Encrypt conversations end-to-end
- Keep shared data in sync across users and devices
- Check or block messages before they are delivered
- Chat with AI agents and run multi-agent workflows
- Upload files and share them with AI agents
- Receive in-app notifications  

## Contents

- [Requirements](#requirements)
- [Installation](#installation)
- [Getting started](#getting-started)
- [Connection](#connection)
- [Channels and messages](#channels-and-messages)
- [Presence](#presence)
- [Encrypted channels](#encrypted-channels)
- [Shared objects](#shared-objects)
- [Interceptors](#interceptors)
- [AI agents](#ai-agents)
- [AI orchestrators](#ai-orchestrators)
- [File storage](#file-storage)
- [Profiles and connectors](#profiles-and-connectors)
- [Notifications](#notifications)
- [Calling ART APIs](#calling-art-apis)
- [Errors](#errors)
- [Logging](#logging)
- [API reference](#api-reference)
- [Platform and runtime notes](#platform-and-runtime-notes)
- [Documentation](#documentation)
- [License](#license)
---


## Requirements

- A Unity version supported by the package (see the `unity` field in the package's `package.json`)
- An ART project with its environment, project key, organisation and client ID
 
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

## Getting started

### 1. Get a user passcode

ART signs users in with a short-lived passcode. Request it from ART with your client credentials and pass only the passcode to the connection. In the example app:

```csharp
string passcode = await AuthService.ConnectWithPasscode(
    username, username, username,
    credentials, $"https://{serverUri}");
```

> **Important:** Anything shipped inside a build can be extracted. Don't embed your client secret in a released app. Request the passcode from your own backend and return only the passcode to the app. Whether users can sign in using only a passcode depends on your ART project settings, so confirm this with the ART team before release.


### 2. Create credentials and connect

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
await adk.Connect();
```

### 3. Send and receive a message

```csharp
using Newtonsoft.Json.Linq;

BaseSubscription channel = await adk.Subscribe("CHANNEL_NAME");

channel.Emitter.On("message", (object data) =>
{
    Debug.Log($"Received: {data}");
});

await channel.Push("message", new JObject { ["text"] = "Hello ART!" });

```


### Other ways to provide credentials

`GetCredentials` in `AdkConfig` is the simplest option. You can also set credentials directly or load them from a JSON file.

| **Option** | **When to use it** |
|---|---|
| `AdkConfig.GetCredentials` | Your credentials change over time, for example when the passcode is refreshed. The ADK calls it each time it signs in. |
| `adk.SetCredentials(...)` | You already have a `CredentialStore` in hand. |
| `adk.LoadConfig()` | You keep credentials in a `config.json` file. |

**Set credentials directly**

```csharp
adk.SetCredentials(new CredentialStore
{
    Environment = "YOUR_ENV",
    ProjectKey  = "YOUR_PROJECT_KEY",
    OrgTitle    = "YOUR_ORG",
    ClientID    = "YOUR_CLIENT_ID",
    AccessToken = passcode
});
```

**Load credentials from a JSON file**

Create a configuration file from the [ART Live Dashboard](https://dev.arealtimetech.com) and save it at the path set in `ArtConstants.CONFIG_JSON_PATH` (default `config.json`):

```json
{
  "Environment": "YOUR_ENV_NAME",
  "ProjectKey": "YOUR_PROJECT_KEY",
  "OrgTitle": "YOUR_ORG",
  "ClientID": "xxxxxxxxxx",
  "ClientSecret": "xxxxxxxxxxx"
}
```

Then load it at runtime:

```csharp
CredentialStore credentials = await adk.LoadConfig();
adk.SetCredentials(credentials);
```

> Use a `ClientSecret` only in development or on a trusted server. See the note in [Get a user passcode](#1-get-a-user-passcode).


## Connection

`Connect()` opens the WebSocket connection, signs the user in, and returns once ART has accepted the connection. Wire the connection events before you connect:

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

| **Event** | **Raised when** |
|---|---|
| `OnConnected` | The connection is open and signed in. Carries a `ConnectionDetail`. |
| `OnClosed` | The connection closed |
| `OnError` | The connection reported an error |

<!-- The rest of this section is inferred from the Swift ADK. Verify names and behaviour against the Unity API before publishing. -->

`Connect()` throws a `ConnectionException` when it can't connect: `AUTHENTICATION_FAILED` when the credentials are missing or ART rejects them, or `CONNECTION_TIMEOUT` when the connection isn't ready in time. Calls made while a connection attempt is in progress share it.

By default the ADK waits up to 30 seconds for the connection, for a sign-in request and for ART's reply to a request such as a subscribe. To change this, set `ConnectionTimeoutMs` to a positive number. It doesn't limit how long an agent can take to answer.


```csharp
var adk = new Adk(new AdkConfig { Uri = "YOUR_WEBSOCKET_URI", ConnectionTimeoutMs = 15000 });
```

You can pause the connection and resume it later. A paused connection stays closed until you call `Resume()`.

```csharp
adk.Pause();
await adk.Resume();

await adk.Disconnect();   // close the connection
```

### When the connection fails

Sometimes the connection can't continue: the credentials were rejected, a limit was reached, you paused or closed the connection, or it couldn't be restored after five attempts. The ADK then raises `OnConnectionFailed` with a `ConnectionException` and stops reconnecting. Agent runs in progress end with an error, and later sends and subscribes throw the same `ConnectionException` until you call `Connect()` again.

```csharp
adk.OnConnectionFailed += (ConnectionException error) =>
{
    Debug.LogError($"Connection failed: {error.Code} {error.Message}");
};

adk.OnAuthenticationFailed += (AuthenticationFailedDetail detail) =>
{
    // Update the credentials, then call Connect() again.
    Debug.LogError($"Sign-in rejected: {detail.Status} {detail.Error}");
};
```

| **Code** | **Meaning** |
|---|---|
| `AUTHENTICATION_FAILED` | The credentials are missing or were rejected. `Status` holds the HTTP status when ART rejected them. |
| `CONNECTION_TIMEOUT` | The connection, or ART's reply to a request, didn't arrive in time |
| `CONNECTION_LOST` | The connection dropped before a message could be sent, or couldn't be restored |
| `CONNECTION_PAUSED`, `CONNECTION_CLOSED` | You called `Pause()` or `Disconnect()` |
| `CONNECTION_CANCELLED` | A newer connection attempt replaced this one |
| `CONNECTION_AUTHORIZATION_FAILED` | The connection's sign-in couldn't be renewed |
| `ACK_TIMEOUT` | ART didn't confirm a message |
| `CONNECTION_MINUTES_LIMIT_EXCEEDED`, `CONCURRENT_LIMIT_EXCEEDED` | Your project reached its billing or concurrency limit |

`adk.State` tells you where the connection stands:

| **State** | **Meaning** |
|---|---|
| `AdkState.Connecting` | Opening the connection, or reconnecting after it dropped |
| `AdkState.Connected` | Connected and signed in |
| `AdkState.Paused` | Paused with `Pause()` |
| `AdkState.Stopped` | Not connected |

If your project reaches its billing or concurrency limit, the ADK also raises `OnLimitExceeded`.


## Channels and messages

A channel is a named stream of messages. Subscribe to a channel to send and receive messages on it. `Subscribe` returns a `BaseSubscription`: cast it to `Subscription` for regular channels, or to `SharedObjectChannel` for [shared-object channels](#shared-objects).

```csharp
BaseSubscription sub = await adk.Subscribe("room-42");

// When you no longer need the channel:
await sub.Unsubscribe();
```

### Sending messages

```csharp
await sub.Push("message", new JObject { ["text"] = "Hello" });
```

To send a message to a specific user, pass their username in `PushOptions`. Secure and targeted channels require exactly one recipient; targeting zero or more than one throws an exception.

```csharp
await sub.Push(
    eventName: "message",
    data: new JObject { ["text"] = "Hi Bob" },
    options: new PushOptions("bob")
);
```

<!-- Inferred from the Swift ADK. Verify against the Unity API before publishing. -->

On targeted and encrypted channels, `Push` waits until ART confirms delivery. If no confirmation arrives within 50 seconds, it throws a `ConnectionException` with the code `ACK_TIMEOUT`.

The ADK never queues a message to send later. If there's no connection, `Push` throws a `ConnectionException` and the message isn't sent. A message is never resent automatically, so after an error, check whether it took effect before you send it again.

### Receiving messages

Listen for one event on the subscription's emitter:

```csharp
sub.Emitter.On("message", (object data) =>
{
    Debug.Log($"Got message: {data}");
});
```

On regular channels you can also bind to an event, listen to every event, and remove a listener:

```csharp
if (sub is Subscription standardSub)
{
    // One event
    standardSub.Bind("cursor-move", (object payload) =>
    {
        // payload is a JObject / JToken
    });

    // Every event
    standardSub.Listen((JObject data) =>
    {
        Debug.Log($"Event: {data["event"]} | Content: {data["content"]}");
    });

    // Remove a listener
    standardSub.Remove("cursor-move");
}
```

## Presence

Find out who is online in a channel. The callback runs again whenever someone joins or leaves.

```csharp
var stopPresence = await sub.FetchPresence(
    unique: true,
    callback: (List<string> users) =>
    {
        Debug.Log($"Online: {users.Count}");
        foreach (var user in users) Debug.Log($"User online: {user}");
    }
);

// Later, to stop receiving updates:
stopPresence();
```

## Encrypted channels

Messages on encrypted channels are encrypted on the sender's device, and only the recipients can read them. Before using an encrypted channel, create a key pair for the current user. `GenerateKeyPair()` creates the keys and saves the public key on the server.

```csharp
KeyPairType myKeyPair = await adk.GenerateKeyPair();

BaseSubscription secure = await adk.Subscribe("YOUR_SECURE_CHANNEL");

if (secure is Subscription secureSub)
{
    secureSub.Emitter.On("message", (object data) =>
    {
        Debug.Log($"Decrypted message: {data}");
    });

    await secureSub.Push(
        eventName: "message",
        data: new JObject { ["text"] = "This is private" },
        options: new PushOptions("bob") // exactly one recipient
    );
}
```

You can also push directly over the secure line:

```csharp
object response = await adk.PushForSecureLine(
    eventName: "CONFIDENTIAL_UPDATE",
    data: "My secret message",
    listen: true
);
```

## Shared objects

A shared-object channel holds a document that every subscriber can read and edit. Changes are merged automatically using CRDTs (conflict-free replicated data types), so everyone ends up with the same data.

```csharp
BaseSubscription sub = await adk.Subscribe("YOUR_CRDT_CHANNEL");

if (sub is SharedObjectChannel shared)
{
    CRDTProxy state = shared.State();

    // Edit the document, then send your changes.
    state["document"].Set("My Doc");
    await shared.Flush();

    // Read a value.
    var query = shared.Query("document");
    object snapshot = await query.execute();
    Debug.Log($"Snapshot: {snapshot}");

    // Watch for changes.
    Action stopWatching = query.listen(data =>
    {
        Debug.Log($"Document updated: {data}");
    });

    // Later:
    stopWatching();
}
```

Lists support the usual operations:

```csharp
CRDTProxy items = shared.State()["items"];

items.Push("alpha");                  // add to the end
items.Unshift("zero");                // add to the start
object popped = items.Pop();          // remove the last item
object removed = items.RemoveAt(2);   // remove the item at an index

await shared.Flush();

Debug.Log($"Length: {items.Length}");
```

<!-- Swift also offers items.splice(start:deleteCount:insert:). If the Unity CRDTProxy has an equivalent, add it here. -->

## Interceptors

An interceptor sees messages before they are delivered and decides what happens to each one. Call `resolve` to deliver the message, with or without changes, or `reject` to block it.

```csharp
await adk.Intercept("my-interceptor", (JObject request, Action<object> resolve, Action<string> reject) =>
{
    if (request["text"]?.ToString().Contains("badword") == true)
    {
        reject("Message blocked");
        return;
    }
    resolve(request);
});
```

The name must match an interceptor set up in your ART project.


## AI agents

Chat with an agent built on ART. Each agent has its own channel (`agent_com_<agentId>`), and each conversation takes place in a thread.

> Connect the `Adk` first. Agents and orchestrators run on top of an active session.

### Start a conversation

```csharp
Agent agent = adk.Agent("your-agent-id").Connect();
AgentThread thread = agent.Thread();   // each thread has a unique ThreadId

await thread.Listen((AgentEventEnvelope envelope) =>
{
    Debug.Log($"Event: {envelope.Event}");
});

try
{
    Run run = await thread.Run("Plan a 3-day trip to Dubai");
    AgentOutput output = await run.Done();   // waits for the agent's final answer
    Debug.Log($"Agent reply: {output.Message}");
}
catch (AgentRunException e)
{
    Debug.LogError($"The agent couldn't finish: {e.Message} (Code: {e.Error.Code})");
}
```

`run.Done()` waits for the agent's terminal `agent_output` event, and throws `AgentRunException` on `agent_error` or a transport failure. `Listen` receives every event in the thread as it happens, such as progress updates and questions from the agent. `envelope.Content` holds the fields the server sent with the event.

An agent ID is the agent's slug, such as `travel-agent`. To use a specific build, add its name: `travel-agent@release2`.

<!-- Inferred from the Swift ADK. Verify against the Unity API before publishing. -->

`Listen`, `ListenState` and `Run` subscribe to the agent's channel when needed, and throw if that fails, for example when the signed-in user has no access to the agent.

If the connection fails during a run, the run ends with an `AgentRunException` that has the connection error's code, and `Error.Details["source"]` is `"connection"`. The agent may still finish on the server, but the app can't receive the answer, so check the result before you run the request again. The thread's state becomes `failed` with the message "Connection unavailable".

When you no longer need a thread, call `thread.Dispose()`. It ends the thread's run and removes the thread's listeners, while other threads of the agent keep working. After `Dispose()`, `Run`, `Listen` and `ListenState` throw an error.

Runs started from the ART playground can say so with `thread.Run(input, executionOrigin: ExecutionOrigin.Playground)`.

### Listen to events

Handle each event type in the thread:

```csharp
await thread.Listen((AgentEventEnvelope envelope) =>
{
    switch (envelope.Event)
    {
        case "agent_output":        // final answer
            var output = envelope.Content as AgentOutput;
            Debug.Log($"Final output: {output.Message}");
            break;

        case "agent_error":         // the run failed
            var error = envelope.Content as AgentError;
            Debug.LogError($"Agent error: {error.Message}");
            break;

        case "human_input_request": // the agent is asking the user for input
            var hitl = envelope.Content as HumanInputRequest;
            Debug.Log($"Needs input: {hitl.Prompt}");
            break;

        case "agent_wait":          // waiting on another agent
            var wait = envelope.Content as AgentWait;
            Debug.Log($"Waiting: {wait.Reason}");
            break;

        case "planner_correction":  // the planner re-planned
            var correction = envelope.Content as PlannerCorrection;
            Debug.Log($"Correction: {correction.Reason}");
            break;

        default:                    // unknown events arrive as UnknownAgentEvent
            Debug.Log($"Event {envelope.Event}: {envelope.Content}");
            break;
    }
});
```

### Display progress

<!-- Inferred from the Swift ADK. Verify against the Unity API before publishing. -->

Use the thread's state to drive a status indicator, such as "Queued" or "Waiting for your input":

```csharp
Action stopUpdates = await thread.ListenState((AgentThreadState state) =>
{
    Debug.Log($"{state.Phase} {state.Message}");
});

Debug.Log(thread.GetState().Phase);   // the latest state
stopUpdates();                        // stop receiving updates
```

The phase is one of `idle`, `submitted`, `queued`, `running`, `waitingForAgent`, `waitingForApproval`, `waitingForWorkspace`, `completed`, `failed` or `cancelled`.

### Answer questions from the agent

<!-- Inferred from the Swift ADK. Verify against the Unity API before publishing. -->

An agent can pause and ask the user for more information. This is often called human-in-the-loop. Register a handler before you start the run, and send the answer with `SendFeedback`:

```csharp
thread.FeedbackRequest(async (HumanInputRequest request, Run run) =>
{
    Debug.Log($"The agent asks: {request.Prompt}");

    try
    {
        await run.SendFeedback("Budget is 50,000, travelling in December");
    }
    catch (Exception e)
    {
        Debug.LogError($"Couldn't send the answer: {e.Message}");
    }
});
```

### Share files with an agent

<!-- Inferred from the Swift ADK. Verify against the Unity API before publishing. -->

Upload the file to the thread first, then attach it to your message:

```csharp
FileRef file = await thread.Upload(fileBytes, new UploadOptions
{
    Filename = "report.pdf",
    ContentType = "application/pdf",
    OnProgress = fraction => Debug.Log($"Uploaded {(int)(fraction * 100)}%")
});

Run run = await thread.Run("Summarize this document", fileMeta: new[] { new MediaRef(file) });
```

The file belongs to the thread and is owned by the agent. `thread.ListFiles()` lists the thread's files.

## AI orchestrators

An orchestrator coordinates several agents to complete a larger task. It runs on a dedicated channel (`orch_com_<orchestratorId>`), and a thread is an event-driven channel that you push into and listen on directly.

### Start a thread

```csharp
Orchestrator orchestrator = adk.Orchestrator("your-orchestrator-id").Connect();

// Start a new thread, or pass a thread id to resume one.
OrchestratorThread thread = await orchestrator.Thread("optional-thread-id");
Debug.Log($"Thread ID: {thread.GetId()}");
```

### Listen and send input

```csharp
// Every event on this thread
thread.Listen((object envelope) =>
{
    if (envelope is ThreadEventEnvelope env)
        Debug.Log($"Thread event: {env.Event} | Content: {env.Content}");
});

// Or one event type
thread.Bind("agent_output", (object data) => Debug.Log($"Output: {data}"));

// Send the user's input
await thread.Push(
    eventName: "user_input",
    data: new JObject { ["user_input"] = "Plan a 3-day trip to Goa" }
);
```

To send to specific recipients, use `PushConfig`:

```csharp
await thread.Push(
    eventName: "user_input",
    data: new JObject { ["user_input"] = "hello" },
    options: new PushConfig { To = new List<string> { "kiran" } }
);
```

### Show thread progress

<!-- Inferred from the Swift ADK. Verify against the Unity API before publishing. -->

```csharp
thread.ListenState((OrchestratorThreadState state) =>
{
    Debug.Log(state.Phase);
});
```

If the connection fails while a request is in progress, `Listen` receives an `agent_error` event whose details have `source: connection`, and the thread's state becomes `failed`. Call `thread.Dispose()` when you no longer need the thread.

### Answer questions from the orchestrator

Human-feedback requests arrive with an SDK-injected `Reply` callback on the `ThreadEventEnvelope`. Call it to answer:

```csharp
thread.Listen((object raw) =>
{
    if (raw is ThreadEventEnvelope env && env.IsHumanFeedbackRequest)
    {
        Debug.Log($"Input requested: {env.Content}");
        env.Reply(new JObject { ["message"] = "Yes, proceed." });
    }
});
```

### Clean up

```csharp
thread.Remove("agent_output");   // remove one listener
thread.Dispose();                // end the thread and detach all listeners
```

## File storage

Files are temporary media that belong to a conversation thread. Upload a file, then list, fetch or delete it. `Storage` uses the authenticated ADK session: it requests a signed URL, uploads the bytes, then confirms the upload. Create the client only after you have set credentials and connected the ADK.

```csharp
using ART.ADK;
using UnityEngine;

var storage = new Storage();

var uploaded = await storage.Upload(fileBytes, new UploadOptions
{
    Filename = "concept.png",
    ContentType = "image/png",
    OnProgress = value => Debug.Log($"Upload: {value:P0}")
});

Debug.Log($"Uploaded {uploaded.Name}: {uploaded.FileId}");
Debug.Log(uploaded.ReadUrl);

ListFilesResult recent = await storage.ListFiles();
StorageFile details = await storage.GetFile(uploaded.FileId);
await storage.DeleteFile(uploaded.FileId);
```

On desktop and in the editor, `UploadFile(path)` reads a local file and uploads it. On Android and iOS, pick a document with the platform's native picker (or a picker plugin), read its bytes, then call `Upload(bytes, options)`. The ADK doesn't bundle a file picker because its implementation and permissions differ by platform.

The example project includes `Assets/StorageScreen.unity`, a ready-to-open storage screen. It provides editor file selection and exposes `SetSelectedFilePath(...)` and `SetSelectedFileBytes(...)` for a mobile picker callback.

<!-- The rest of this section is inferred from the Swift ADK. Verify names and behaviour against the Unity API before publishing. -->

Each upload returns a file reference with the file's ID, name, size, content type and a URL for reading it. `ListFiles` leaves out that URL, and `GetFile` includes it. To upload into a thread when you use `adk` directly, pass the thread ID in the options:

```csharp
var ref = await adk.Upload(pngBytes, new UploadOptions
{
    Filename = "chart.png",
    ContentType = "image/png",
    ThreadId = "THREAD_ID",
    AllowedMimeTypes = new[] { "image/png" }
});

var page = await adk.ListFiles(new ListOptions { ThreadId = "THREAD_ID", Page = 1, Limit = 20 });
var file = await adk.GetFile(ref.FileId, "THREAD_ID");   // includes a URL for reading the file
await adk.DeleteFile(ref.FileId);
```

Follow an upload's progress with `UploadOptions.OnProgress`: it reports `0` when the upload starts and `1` once ART has stored the file. Set `TtlSeconds` to choose how long the file is kept, and `AllowedMimeTypes` to limit the types ART accepts.

Every file has an owner, which depends on where you upload it from:

| **Uploaded with** | **Owner** |
|---|---|
| `adk.Upload` | Your project, or the configuration you pass in `UploadOptions.ConfigId` |
| `agent.Upload`, or `Upload` on an agent thread | The agent |
| `orchestrator.Upload`, or `Upload` on an orchestrator thread | The orchestrator |
| `Upload` on a channel | The channel's configuration (orchestrator-enabled channels only) |

When a storage request fails, the ADK throws an `UploadException`. Its `Step` tells you which stage failed (`Validate`, `Upload`, `List`, `Get` or `Delete`), and `Status` holds the HTTP status code. A `403` means the user's role doesn't have storage permission.

```csharp
try
{
    await adk.Upload(pngBytes, new UploadOptions { ThreadId = "THREAD_ID" });
}
catch (UploadException e) when (e.Step == UploadStep.Upload && e.Status == 403)
{
    Debug.LogError("This user's role doesn't allow uploads");
}
```

## Profiles and connectors

<!-- Inferred from the Swift ADK. Verify names and behaviour against the Unity API before publishing. -->

Update the signed-in user's profile. Only the fields you set are changed.

```csharp
await adk.UpdateProfile(new UpdateProfileData { FirstName = "Ada", Email = "ada@example.com" });
```

Each connector keeps its own profile for the user. Read it, or update the fields that the connector allows:

```csharp
var crm = adk.Connector("YOUR_CONNECTOR_ID");

var profile = await crm.Profile();                                      // the allowed fields and their current values
await crm.UpdateProfile(new Dictionary<string, object> { ["region"] = "eu" });   // only allowed fields can be changed
```

## Notifications

<!-- Inferred from the Swift ADK. Verify names and behaviour against the Unity API before publishing. -->

Notifications are an optional plugin. Add it to your `Adk` instance once and keep the object it returns:

```csharp
var inbox = adk.Use(Notifications.Create());

// Receive new notifications as they arrive.
Action stopListening = await inbox.OnNew(notification =>
{
    Debug.Log(notification.Title);
});

var unread = await inbox.List(new NotificationListParams { Status = NotificationStatus.Unread });
await inbox.MarkRead();   // marks every unread notification as read
```

You can also send notifications and count unread notifications. A notification you send is delivered in-app unless you set `Channels` on `SendNotificationInput` to `InApp`, `Email` or both. To get the same object elsewhere in your app, call `adk.Plugin<NotificationsApi>("notifications")`.

## Calling ART APIs

<!-- Inferred from the Swift ADK. Verify names and behaviour against the Unity API before publishing. -->

`Call` sends an authenticated request to an ART REST endpoint and returns the decoded JSON response:

```csharp
JObject result = await adk.Call("/v1/some-endpoint", new CallApiProps
{
    Method = "POST",
    Payload = new JObject { ["key"] = "value" }
});
```

To send a `multipart/form-data` request, pass a `FormDataPayload` as the payload:

```csharp
JObject response = await adk.Call("/v1/some-upload", new CallApiProps
{
    Method = "POST",
    Payload = new FormDataPayload
    {
        Fields = new Dictionary<string, string> { ["purpose"] = "avatar" },
        Files = new List<FormDataFile>
        {
            new FormDataFile { Field = "file", Data = pngBytes, Filename = "avatar.png", ContentType = "image/png" }
        }
    }
});
```

## Errors

<!-- Inferred from the Swift ADK. Verify exception names against the Unity API before publishing. -->

| **Error** | **Thrown when** |
|---|---|
| `ConnectionException` | The connection failed or isn't available. See [When the connection fails](#when-the-connection-fails). |
| `AuthHttpException` | ART rejected a sign-in request. `Status` holds the HTTP status, and `IsTerminal` is `true` for rejected credentials (401 or 403). |
| `AgentRunException` | An agent run failed, or ended because the connection failed |
| `RunLifecycleException` | `run.Done()` ended without an answer from the agent, because a newer run on the thread replaced it (`Superseded`) or its thread was disposed (`Closed`). `SendFeedback` also throws `Closed` when the agent isn't waiting for an answer. |
| `UploadException` | A storage request failed |
| `ConnectorException` | A connector ID is empty or can't be resolved, or you updated a field the connector doesn't allow |
| `HttpCallException` | An ART REST request failed in `UpdateProfile` or a plugin |
| `ArtJsonException` | Data you passed can't be converted to JSON |
| `ArtException` | Other errors, for example an invalid argument such as an agent ID that isn't a slug, or a server error when ART refuses a request or `adk.Call` gets an error response |

## Logging

<!-- Inferred from the Swift ADK. Verify names against the Unity API before publishing. -->

The ADK doesn't print anything by default. To see its warnings and errors, set a log handler:

```csharp
ArtLog.Handler = (level, message) =>
{
    Debug.Log($"[ART][{level}] {message}");
};
```

## API reference

| **Class / type** | **Purpose** |
|---|---|
| `Adk` | Top-level facade: connect, disconnect, subscribe, intercept, key management |
| `AdkConfig` | Connection configuration (URI, credentials, auth token) |
| `CredentialStore` | Holder for org, project and client credentials |
| `BaseSubscription` | Base type for all channel subscriptions: `Push`, `FetchPresence`, `Unsubscribe` |
| `Subscription` | Regular, secure, targeted and group channels: `Listen()`, `Bind()`, `Remove()`, `Emitter.On()` |
| `SharedObjectChannel` | CRDT-backed shared-object channel: `State()`, `Query()`, `Flush()` |
| `CRDTEngine` | The conflict-free replicated data engine behind shared objects |
| `CRDTProxy` | Indexer-based path accessor for reading and changing shared-object state |
| `PushOptions` | Push options for channels: `To`, `FromUsername`, `ThreadId` |
| `PushConfig` | Push options for threads: `To`, `ThreadID`, `TimeoutMs` |
| `KeyPairType` | Public/private key pair for encrypted channels |
| `ConnectionDetail` | Delivered by `OnConnected`: connection ID, instance ID and tenant information |
| `Agent` | Agent handle bound to `agent_com_<agentId>`: `Connect()`, `Thread()` |
| `AgentThread` | Agent thread: `Run()`, `Listen()`, `OnHumanInput()` |
| `Run` | An active agent run: `Done()`, `SendFeedback()` |
| `AgentRunException` | Thrown by `Run.Done()` when the run fails; `Error.Code` holds the code |
| `Orchestrator` | Orchestrator handle bound to `orch_com_<orchestratorId>`: `Connect()`, `Thread()` |
| `OrchestratorThread` | Orchestrator thread: `Push()`, `Listen()`, `Bind()`, `Remove()`, `Dispose()`, `GetId()` |
| `Storage` | File storage client: `Upload()`, `UploadFile()`, `ListFiles()`, `GetFile()`, `DeleteFile()` |

## Platform and runtime notes

### WebGL

- **Desktop and mobile:** use the native WebSocket support in C# `System.Net.WebSockets`.
- **WebGL:** `System.Net.WebSockets` isn't supported in WebGL builds. A JavaScript WebSocket bridge plugin is required to run in the browser.

### Threads and the main thread

Unity's engine APIs can only be called on the main thread. WebSocket and message callbacks that arrive on background threads are moved to the main thread for you with `MainThreadDispatcher.Enqueue()`, which starts automatically when you create an `Adk`.

### Testing

Use the Unity Test Runner (**Window › General › Test Runner**) to run the included unit tests. To test against a live environment, put your details in `config.json`.

## Documentation

For full guides, see the [ART ADK documentation](https://docs.arealtimetech.com/docs/adk/). For what's new in each release and how to upgrade, see the changelog in the package repository.

## License

Licensed under the Apache License 2.0.
