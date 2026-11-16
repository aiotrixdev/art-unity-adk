using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ART.ADK
{
    /// <summary>
    /// Helper functions for channel subscribe/unsubscribe/interceptor operations via the secure line.
    /// </summary>
    internal static class HelperFunctions
    {
        public static async Task<ChannelConfig> SubscribeToChannel(
            string channel, string process, IWebSocketHandler handler)
        {
            await handler.WaitForConnection();

            var eventName = process == "subscribe" ? "channel-subscribe" : "channel-presence";

            // [Request] outgoing WS frame (before send).
            Debug.Log($"[{DateTime.Now:HH:mm:ss.fff}] [Request] WS OUT '{eventName}' ->\n" +
                new JObject { ["channel"] = channel, ["process"] = process });

            var result = await handler.PushForSecureLine(eventName,
                new Dictionary<string, object> { { "channel", channel } }, true);

            // [Response] raw server response, exactly as received, BEFORE any parsing/validation.
            Debug.Log($"[{DateTime.Now:HH:mm:ss.fff}] [Response] WS IN (raw) for '{eventName}' on '{channel}':\n" +
                (result == null ? "null" : (result is JObject rjo ? rjo.ToString() : result.ToString())));

            if (result == null)
                throw new ARTChannelNotFoundException(channel);

            var wrapper = result as JObject ?? JObject.FromObject(result);
            var data = wrapper["data"] as JObject;
            if (data == null)
                throw new ARTServerException("Invalid subscribe response shape");

            if (data["status"]?.ToString() == "not-OK")
                throw new ARTServerException(data["error"]?.ToString() ?? "Unknown error");

            var rawData = data["channelConfig"] as JObject;
            if (rawData == null)
                throw new ARTChannelNotFoundException(channel);

            var presenceUsers = new List<string>();
            if (data["presenceUsers"] is JArray pu)
            {
                foreach (var u in pu)
                    presenceUsers.Add(u.ToString());
            }

            var config = new ChannelConfig
            {
                ChannelName = data["channel"]?.ToString() ?? channel,
                ChannelNamespace = data["channelNamespace"]?.ToString() ?? "",
                ChannelType = rawData["TypeofChannel"]?.ToString() ?? "default",
                PresenceUsers = presenceUsers,
                Snapshot = data["snapshot"],
                SubscriptionID = data["subscriptionID"]?.ToString(),
                OrchestratorEnabled = rawData["IsInterceptorEnabled"]?.ToObject<bool>() ?? false
            };

            // [Response] parsed/deserialized ChannelConfig that callers receive.
            Debug.Log($"[{DateTime.Now:HH:mm:ss.fff}] [Response] Parsed ChannelConfig for '{channel}':\n" +
                new JObject
                {
                    ["channelName"] = config.ChannelName,
                    ["channelType"] = config.ChannelType,
                    ["channelNamespace"] = config.ChannelNamespace,
                    ["subscriptionID"] = config.SubscriptionID,
                    ["orchestratorEnabled"] = config.OrchestratorEnabled,
                    ["presenceUsers"] = JArray.FromObject(presenceUsers)
                });

            return config;
        }

        public static async Task<bool> UnsubscribeFromChannel(
            string channel, string subscriptionID, string process, IWebSocketHandler handler)
        {
            await handler.WaitForConnection();

            var eventName = process == "subscribe" ? "channel-unsubscribe" : "presence-unsubscribe";

            var result = await handler.PushForSecureLine(eventName,
                new Dictionary<string, object>
                {
                    { "channel", channel },
                    { "subscriptionID", subscriptionID }
                }, true);

            if (result == null) return false;

            var wrapper = result as JObject ?? JObject.FromObject(result);
            var data = wrapper["data"] as JObject;
            if (data == null) return false;

            if (data["status"]?.ToString() == "not-OK")
                throw new ARTServerException(data["error"]?.ToString() ?? "Unknown error");

            return true;
        }

        public static async Task<object> GetInterceptorConfig(
            string interceptor, IWebSocketHandler handler)
        {
            await handler.WaitForConnection();

            var result = await handler.PushForSecureLine("interceptor-subscribe",
                new Dictionary<string, object> { { "interceptor", interceptor } }, true);

            if (result == null) 
            {
                return null;
            }

            var wrapper = result as JObject ?? JObject.FromObject(result);
            var data = wrapper["data"] as JObject;
            if (data == null) 
            {
                return null;
            }

            if (data["status"]?.ToString() == "not-OK")
            {
                var errMsg = data["error"]?.ToString() ?? "Unknown error";
                throw new ARTServerException(errMsg);
            }

            var config = data["interceptorConfig"];
            return config;
        }
    }
}
