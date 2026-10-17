using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Robust.Client;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;

namespace Content.Client.Chat.Managers;

/// <summary>
/// Logs client-received chatbox messages to per-session JSON Lines (<c>.jsonl</c>) files inside <see cref="IResourceManager.UserData"/>.
/// </summary>
internal sealed partial class ChatLogManager : IChatLogManager, IDisposable
{
    internal const int MaxServerNameSlugLength = 48;
    internal const string DefaultServerSlug = "server";
    internal const string FileExtension = ".jsonl";
    internal const string SessionTimestampFormat = "yyyy-MM-dd_HH-mm-ss";

    [Dependency] private IResourceManager _resourceManager = default!;
    [Dependency] private IConfigurationManager _configManager = default!;
    [Dependency] private IBaseClient _baseClient = default!;
    [Dependency] private ILogManager _logManager = default!;

    private readonly ConcurrentQueue<(ResPath Path, string Line)> _queue = new();
    private readonly object _writeLock = new();

    private ISawmill _sawmill = default!;
    private bool _initialized;
    private bool _enabled;
    private bool _recordAdmin;
    private int _retentionDays = 7;
    private string _logDirectory = "/chat-logs";

    private DateTime? _sessionStartTimeLocal;
    private ResPath? _currentSessionPath;
    private ResPath? _lastPrunedSessionPath;
    private int _isFlushing;

    public void Initialize()
    {
        if (_initialized)
            return;

        _initialized = true;
        _sawmill = _logManager.GetSawmill("chat.log");

        _configManager.OnValueChanged(CCVars.ChatLogEnabled, OnEnabledChanged, true);
        _configManager.OnValueChanged(CCVars.ChatLogDirectory, OnDirectoryChanged, true);
        _configManager.OnValueChanged(CCVars.ChatLogRetentionDays, OnRetentionDaysChanged, true);
        _configManager.OnValueChanged(CCVars.ChatLogRecordAdmin, OnRecordAdminChanged, true);

        _baseClient.PlayerJoinedServer += OnPlayerJoinedServer;
        _baseClient.PlayerLeaveServer += OnPlayerLeaveServer;
    }

    public void Shutdown()
    {
        if (!_initialized)
            return;

        _initialized = false;

        _configManager.UnsubValueChanged(CCVars.ChatLogEnabled, OnEnabledChanged);
        _configManager.UnsubValueChanged(CCVars.ChatLogDirectory, OnDirectoryChanged);
        _configManager.UnsubValueChanged(CCVars.ChatLogRetentionDays, OnRetentionDaysChanged);
        _configManager.UnsubValueChanged(CCVars.ChatLogRecordAdmin, OnRecordAdminChanged);

        _baseClient.PlayerJoinedServer -= OnPlayerJoinedServer;
        _baseClient.PlayerLeaveServer -= OnPlayerLeaveServer;

        Flush();
        _currentSessionPath = null;
        _sessionStartTimeLocal = null;
        _lastPrunedSessionPath = null;
    }

    public void Dispose()
    {
        Shutdown();
    }

    private void OnEnabledChanged(bool value)
    {
        _enabled = value;
        if (!value)
        {
            Flush();
        }
    }

    private void OnDirectoryChanged(string value)
    {
        _logDirectory = string.IsNullOrWhiteSpace(value) ? "/chat-logs" : value;
        _currentSessionPath = null;
    }

    private void OnRetentionDaysChanged(int value)
    {
        _retentionDays = value;
    }

    private void OnRecordAdminChanged(bool value)
    {
        _recordAdmin = value;
    }

    private void OnPlayerJoinedServer(object? sender, PlayerEventArgs args)
    {
        _sessionStartTimeLocal = DateTime.Now;
        _currentSessionPath = null;
    }

    private void OnPlayerLeaveServer(object? sender, PlayerEventArgs args)
    {
        Flush();
        _currentSessionPath = null;
        _sessionStartTimeLocal = null;
    }

    public void LogMessage(ChatChannel channel, string message)
    {
        if (!_enabled)
            return;

        if ((channel & ChatChannel.AdminRelated) != 0 && !_recordAdmin)
            return;

        if (string.IsNullOrEmpty(message))
            return;

        var nowUtc = DateTime.UtcNow;
        var sessionPath = GetOrCreateSessionPath(DateTime.Now);
        var jsonLine = FormatJsonLine(nowUtc, channel, message);

        _queue.Enqueue((sessionPath, jsonLine));
        ScheduleBackgroundFlush();
    }

    public void Flush()
    {
        DrainQueue();
    }

    private ResPath GetOrCreateSessionPath(DateTime nowLocal)
    {
        if (_currentSessionPath is { } existingPath)
            return existingPath;

        var baseDir = new ResPath(_logDirectory).ToRootedPath().Clean();
        var sessionStart = _sessionStartTimeLocal ?? nowLocal;
        var serverName = _baseClient.GameInfo?.ServerName;
        if (string.IsNullOrWhiteSpace(serverName))
        {
            serverName = _configManager.GetCVar(CVars.GameHostName);
        }

        var fileName = BuildSessionFileName(sessionStart, serverName);
        var fullPath = baseDir / fileName;
        _currentSessionPath = fullPath;

        return fullPath;
    }

    private void ScheduleBackgroundFlush()
    {
        if (Interlocked.CompareExchange(ref _isFlushing, 1, 0) != 0)
            return;

        Task.Run(() =>
        {
            try
            {
                DrainQueue();
            }
            finally
            {
                Interlocked.Exchange(ref _isFlushing, 0);
                if (!_queue.IsEmpty)
                {
                    ScheduleBackgroundFlush();
                }
            }
        });
    }

    private void DrainQueue()
    {
        if (_queue.IsEmpty)
            return;

        lock (_writeLock)
        {
            ResPath? currentPath = null;
            StreamWriter? writer = null;

            try
            {
                while (_queue.TryDequeue(out var entry))
                {
                    if (writer == null || currentPath != entry.Path)
                    {
                        writer?.Dispose();
                        writer = null;

                        var dir = entry.Path.Directory;
                        if (!_resourceManager.UserData.IsDir(dir))
                        {
                            _resourceManager.UserData.CreateDir(dir);
                        }

                        if (_lastPrunedSessionPath != entry.Path)
                        {
                            _lastPrunedSessionPath = entry.Path;
                            PruneOldLogs(dir, DateTime.Now, entry.Path);
                        }

                        var stream = _resourceManager.UserData.Open(
                            entry.Path,
                            FileMode.Append,
                            FileAccess.Write,
                            FileShare.ReadWrite);

                        writer = new StreamWriter(stream, EncodingHelpers.UTF8);
                        currentPath = entry.Path;
                    }

                    writer.WriteLine(entry.Line);
                }
            }
            catch (Exception e)
            {
                _sawmill.Warning($"Failed to write client chat log entry: {e.Message}");
            }
            finally
            {
                try
                {
                    writer?.Dispose();
                }
                catch (Exception e)
                {
                    _sawmill.Warning($"Failed to close client chat log stream: {e.Message}");
                }
            }
        }
    }

    internal void PruneOldLogs(ResPath logDir, DateTime nowLocal, ResPath? activeSessionPath = null)
    {
        if (_retentionDays <= 0)
            return;

        try
        {
            if (!_resourceManager.UserData.IsDir(logDir))
                return;

            var cutoff = nowLocal.AddDays(-_retentionDays);
            var entries = new List<string>(_resourceManager.UserData.DirectoryEntries(logDir));

            foreach (var entry in entries)
            {
                if (!entry.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!TryParseSessionTimestamp(entry, out var fileTimestamp))
                    continue;

                if (fileTimestamp >= cutoff)
                    continue;

                var candidatePath = logDir / entry;
                if (activeSessionPath is { } active && candidatePath == active)
                    continue;

                _resourceManager.UserData.Delete(candidatePath);
            }
        }
        catch (Exception e)
        {
            _sawmill.Warning($"Failed to prune old client chat logs: {e.Message}");
        }
    }

    /// <summary>
    /// Builds the per-session filename <c>yyyy-MM-dd_HH-mm-ss_&lt;sanitized-server-name&gt;.jsonl</c>.
    /// </summary>
    internal static string BuildSessionFileName(DateTime sessionStartLocal, string? serverName)
    {
        var timestamp = sessionStartLocal.ToString(
            SessionTimestampFormat,
            CultureInfo.InvariantCulture);
        var slug = SanitizeServerName(serverName);
        return $"{timestamp}_{slug}{FileExtension}";
    }

    /// <summary>
    /// Attempts to parse the <c>yyyy-MM-dd_HH-mm-ss</c> session timestamp prefix from a chat log filename.
    /// </summary>
    internal static bool TryParseSessionTimestamp(string fileName, out DateTime timestamp)
    {
        var prefixLength = SessionTimestampFormat.Length;
        if (string.IsNullOrEmpty(fileName) || fileName.Length < prefixLength)
        {
            timestamp = default;
            return false;
        }

        var prefix = fileName.Substring(0, prefixLength);
        return DateTime.TryParseExact(
            prefix,
            SessionTimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out timestamp);
    }

    /// <summary>
    /// Converts a server hostname into a lowercase ASCII filename slug (<c>[a-z0-9-]</c>).
    /// </summary>
    internal static string SanitizeServerName(string? serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName))
            return DefaultServerSlug;

        var lowered = serverName.ToLowerInvariant();
        var sb = new StringBuilder(Math.Min(lowered.Length, MaxServerNameSlugLength));
        var lastWasHyphen = false;

        foreach (var ch in lowered)
        {
            // Strip apostrophes so "Wizard's" becomes "wizards".
            if (ch is '\'' or '\u2019' or '\u2018')
                continue;

            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                sb.Append(ch);
                lastWasHyphen = false;

                if (sb.Length >= MaxServerNameSlugLength)
                    break;
            }
            else if (sb.Length > 0 && !lastWasHyphen)
            {
                sb.Append('-');
                lastWasHyphen = true;

                if (sb.Length >= MaxServerNameSlugLength)
                    break;
            }
        }

        while (sb.Length > 0 && sb[sb.Length - 1] == '-')
        {
            sb.Remove(sb.Length - 1, 1);
        }

        return sb.Length > 0 ? sb.ToString() : DefaultServerSlug;
    }

    /// <summary>
    /// Formats a single JSON Lines record with <c>timestamp</c>, <c>channel</c>, and <c>message</c>.
    /// </summary>
    internal static string FormatJsonLine(DateTime timestampUtc, ChatChannel channel, string message)
    {
        var timestamp = timestampUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var channelStr = channel.ToString();

        var sb = new StringBuilder(message.Length + 64);
        sb.Append("{\"timestamp\":\"");
        sb.Append(timestamp);
        sb.Append("\",\"channel\":\"");
        AppendEscapedJsonString(sb, channelStr);
        sb.Append("\",\"message\":\"");
        AppendEscapedJsonString(sb, message);
        sb.Append("\"}");
        return sb.ToString();
    }

    internal static void AppendEscapedJsonString(StringBuilder sb, string value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (ch < ' ')
                    {
                        sb.Append("\\u");
                        sb.Append(((int) ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }
    }
}
