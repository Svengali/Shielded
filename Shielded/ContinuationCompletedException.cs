using System;

namespace Shielded
{
    /// <summary>
    /// Thrown by <see ref="CommitContinuation"/> methods which depend on the continuation
    /// not being completed. E.g. running <see ref="CommitContinuation.InContext"/> after
    /// the context has completed will throw this exception.
    /// </summary>
    public class ContinuationCompletedException : Exception
    {
    }
}
