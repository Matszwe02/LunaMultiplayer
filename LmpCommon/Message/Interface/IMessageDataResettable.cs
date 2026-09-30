namespace LmpCommon.Message.Interface
{
    /// <summary>
    /// Opt-in contract for <see cref="IMessageData"/> needing a clear before reuse. <c>MessageStore</c>
    /// pools instances without touching their fields, so a type pairing a buffer with an explicit
    /// length can inherit a stale length and make serialisation throw, which reaches the player as a
    /// disconnect. Not a member of <see cref="IMessageData"/>, so existing classes still compile.
    /// </summary>
    public interface IMessageDataResettable
    {
        /// <summary>Restores the instance to its just-constructed state. Must be cheap.</summary>
        void Reset();
    }
}
