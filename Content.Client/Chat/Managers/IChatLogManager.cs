using Content.Shared.Chat;

namespace Content.Client.Chat.Managers;

/// <summary>
/// Manages client-side logging of received chatbox messages to per-session JSON Lines files in user data.
/// </summary>
public interface IChatLogManager
{
    /// <summary>
    /// Initializes configuration subscriptions and session lifecycle hooks.
    /// </summary>
    void Initialize();

    /// <summary>
    /// Flushes any pending log entries and unsubscribes from events.
    /// </summary>
    void Shutdown();

    /// <summary>
    /// Queues a chat message for logging if client chat logging is enabled.
    /// </summary>
    /// <param name="channel">The chat channel the message was received on.</param>
    /// <param name="message">The formatted message string displayed in the chatbox.</param>
    void LogMessage(ChatChannel channel, string message);

    /// <summary>
    /// Synchronously flushes all currently queued chat log entries to user data.
    /// </summary>
    void Flush();
}
