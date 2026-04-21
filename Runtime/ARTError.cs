using System;

namespace ART.ADK
{
    /// <summary>
    /// Base exception for all ART ADK errors.
    /// </summary>
    public class ARTException : Exception
    {
        public ARTException(string message) : base(message) { }
    }

    public class ARTForbiddenException : ARTException
    {
        public ARTForbiddenException(string message) : base($"Forbidden: {message}") { }
    }

    public class ARTAuthenticationException : ARTException
    {
        public ARTAuthenticationException(string message) : base($"Auth failed: {message}") { }
    }

    public class ARTInvalidPathException : ARTException
    {
        public ARTInvalidPathException(string message) : base($"Invalid path: {message}") { }
    }

    public class ARTNotConnectedException : ARTException
    {
        public ARTNotConnectedException() : base("Not connected") { }
    }

    public class ARTEncryptionException : ARTException
    {
        public ARTEncryptionException(string message) : base($"Encryption error: {message}") { }
    }

    public class ARTDecryptionException : ARTException
    {
        public ARTDecryptionException(string message) : base($"Decryption error: {message}") { }
    }

    public class ARTTimeoutException : ARTException
    {
        public ARTTimeoutException(string message) : base($"Timeout: {message}") { }
    }

    public class ARTServerException : ARTException
    {
        public ARTServerException(string message) : base($"Server error: {message}") { }
    }

    public class ARTChannelNotFoundException : ARTException
    {
        public ARTChannelNotFoundException(string channel) : base($"Channel not found: {channel}") { }
    }

    public class ARTAckTimeoutException : ARTException
    {
        public ARTAckTimeoutException() : base("ACK timeout") { }
    }
}
