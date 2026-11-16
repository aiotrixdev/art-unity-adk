using System;
using System.Text.RegularExpressions;

namespace ART.ADK
{
    /// <summary>Validation helpers for execution references (orchestrator / agent ids).</summary>
    internal static class ExecutionReference
    {
        // Lowercase slug, optionally followed by @buildname (1-64 lowercase alphanumerics).
        // \z is used instead of $ because in .NET, $ also matches before a trailing "\n",
        // which would let "my-slug\n" pass. Kotlin's matches() has no such loophole.
        private static readonly Regex Pattern =
            new Regex(@"^[a-z0-9]+(?:-[a-z0-9]+)*(?:@[a-z0-9]{1,64})?\z", RegexOptions.CultureInvariant);

        /// <summary>
        /// Validates that <paramref name="reference"/> is a lowercase slug or <c>slug@buildname</c>.
        /// </summary>
        /// <returns>The same <paramref name="reference"/> when valid.</returns>
        /// <exception cref="ArgumentException">Thrown when the reference is null or malformed.</exception>
        public static string Validate(string reference)
        {
            if (reference == null || !Pattern.IsMatch(reference))
            {
                throw new ArgumentException("Expected a lowercase slug or slug@buildname", nameof(reference));
            }

            return reference;
        }
    }
}