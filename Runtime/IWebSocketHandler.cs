using System.Threading.Tasks;

namespace ART.ADK
{
    /// <summary>
    /// Interface for WebSocket operations, used by subscriptions and interceptors.
    /// </summary>
    public interface IWebSocketHandler
    {
        Task Wait();
        bool SendMessage(string message);
        ConnectionDetail GetConnection();
        Task<string> Encrypt(string data, string recipientPublicKey);
        Task<string> Decrypt(string encryptedHash, string senderPublicKey);
        Task<object> PushForSecureLine(string eventName, object data, bool listen);
        void RemoveSubscription(string channel);
    }
}
