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
            await handler.Wait();

            var eventName = process == "subscribe" ? "channel-subscribe" : "channel-presence";

            var result = await handler.PushForSecureLine(eventName,
                new Dictionary<string, object> { { "channel", channel } }, true);

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

            return new ChannelConfig
            {
                ChannelName = data["channel"]?.ToString() ?? channel,
                ChannelNamespace = data["channelNamespace"]?.ToString() ?? "",
                ChannelType = rawData["TypeofChannel"]?.ToString() ?? "default",
                PresenceUsers = presenceUsers,
                Snapshot = data["snapshot"],
                SubscriptionID = data["subscriptionID"]?.ToString()
            };
        }

        public static async Task<bool> UnsubscribeFromChannel(
            string channel, string subscriptionID, string process, IWebSocketHandler handler)
        {
            await handler.Wait();

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
            Debug.Log($"[ART-Helper] Fetching interceptor config for: {interceptor}");
            await handler.Wait();

            var result = await handler.PushForSecureLine("interceptor-subscribe",
                new Dictionary<string, object> { { "interceptor", interceptor } }, true);

            Debug.Log($"[ART-Helper] interceptor-subscribe response: {result ?? "NULL"}");

            if (result == null) 
            {
                Debug.LogWarning($"[ART-Helper] Timeout or null response for interceptor: {interceptor}");
                return null;
            }

            var wrapper = result as JObject ?? JObject.FromObject(result);
            var data = wrapper["data"] as JObject;
            if (data == null) 
            {
                Debug.LogError($"[ART-Helper] Response missing 'data' field: {wrapper}");
                return null;
            }

            if (data["status"]?.ToString() == "not-OK")
            {
                var errMsg = data["error"]?.ToString() ?? "Unknown error";
                Debug.LogError($"[ART-Helper] Server rejected interceptor '{interceptor}': {errMsg}");
                throw new ARTServerException(errMsg);
            }

            var config = data["interceptorConfig"];
            Debug.Log($"[ART-Helper] Successfully parsed config for {interceptor}: {config}");
            return config;
        }
    }
}
