using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Content.Client.Chat.Managers;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Moq;
using NUnit.Framework;
using Robust.Client;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.IoC;
using Robust.Shared.Utility;
using Robust.UnitTesting;

namespace Content.Tests.Client.Chat;

[TestFixture]
[TestOf(typeof(ChatLogManager))]
public sealed class ChatLogManagerTest : ContentUnitTest
{
    protected override void OverrideIoC()
    {
        base.OverrideIoC();

        var deps = IoCManager.Instance!;
        var mockClient = new Mock<IBaseClient>();
        mockClient.Setup(c => c.GameInfo).Returns(new ServerInfo("[EN] Test Station 14", 32));
        deps.RegisterInstance<IBaseClient>(mockClient.Object, overwrite: true);
        deps.Register<IChatLogManager, ChatLogManager>(overwrite: true);
    }

    [SetUp]
    public void SetupUserData()
    {
        var resMan = IoCManager.Resolve<IResourceManager>();
        if (resMan.UserData == null)
        {
            resMan.GetType().GetMethod("Initialize", [typeof(string), typeof(bool)])
                ?.Invoke(resMan, [null, false]);
        }

        var cfg = IoCManager.Resolve<IConfigurationManager>();
        if (!cfg.IsCVarRegistered(CCVars.ChatLogEnabled.Name))
            cfg.RegisterCVar(CCVars.ChatLogEnabled.Name, CCVars.ChatLogEnabled.DefaultValue, CVar.NONE);
        if (!cfg.IsCVarRegistered(CCVars.ChatLogDirectory.Name))
            cfg.RegisterCVar(CCVars.ChatLogDirectory.Name, CCVars.ChatLogDirectory.DefaultValue, CVar.NONE);
        if (!cfg.IsCVarRegistered(CCVars.ChatLogRetentionDays.Name))
            cfg.RegisterCVar(CCVars.ChatLogRetentionDays.Name, CCVars.ChatLogRetentionDays.DefaultValue, CVar.NONE);
        if (!cfg.IsCVarRegistered(CCVars.ChatLogRecordAdmin.Name))
            cfg.RegisterCVar(CCVars.ChatLogRecordAdmin.Name, CCVars.ChatLogRecordAdmin.DefaultValue, CVar.NONE);
    }

    [Test]
    [TestCase("[EN] Wizard's Den Lizard [US East]", "en-wizards-den-lizard-us-east")]
    [TestCase("Wizard’s Den — Salamander!", "wizards-den-salamander")]
    [TestCase("  ---Delta-V : Aegis---  ", "delta-v-aegis")]
    [TestCase("", "server")]
    [TestCase("   ", "server")]
    [TestCase("[]!@#$%^&*()", "server")]
    public void TestSanitizeServerName(string input, string expected)
    {
        var actual = ChatLogManager.SanitizeServerName(input);
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void TestSanitizeServerName_TruncatesAndTrimsTrailingHyphen()
    {
        var longName = new string('a', 47) + " bcdefghijk";
        var slug = ChatLogManager.SanitizeServerName(longName);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slug.Length, Is.LessThanOrEqualTo(ChatLogManager.MaxServerNameSlugLength));
            Assert.That(slug.EndsWith('-'), Is.False);
            Assert.That(slug, Is.EqualTo(new string('a', 47)));
        }
    }

    [Test]
    public void TestBuildSessionFileName_AndParseTimestamp()
    {
        var sessionStart = new DateTime(2026, 10, 16, 21, 5, 30, DateTimeKind.Local);
        var fileName = ChatLogManager.BuildSessionFileName(sessionStart, "[EN] Wizard's Den Leviathan");

        Assert.That(fileName, Is.EqualTo("2026-10-16_21-05-30_en-wizards-den-leviathan.jsonl"));

        var parsedOk = ChatLogManager.TryParseSessionTimestamp(fileName, out var parsedTime);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsedOk, Is.True);
            Assert.That(parsedTime, Is.EqualTo(new DateTime(2026, 10, 16, 21, 5, 30)));
        }
    }

    [Test]
    public void TestFormatJsonLine_EscapesSpecialCharactersAndIsCultureInvariant()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fi-FI");

            var timestamp = new DateTime(2026, 10, 16, 9, 5, 12, DateTimeKind.Utc);
            var rawWrapped = "[color=#408040]\\[Science\\] [textlink=\"Urist \\\"The Mad\\\" McHands\" entity=\"123\"] says, \"Line1\nLine2\t\u0007\"[/color]";

            var line = ChatLogManager.FormatJsonLine(timestamp, ChatChannel.Radio, rawWrapped);

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(root.EnumerateObject().Count(), Is.EqualTo(3));
                Assert.That(root.GetProperty("timestamp").GetString(), Is.EqualTo("2026-10-16T09:05:12Z"));
                Assert.That(root.GetProperty("channel").GetString(), Is.EqualTo("Radio"));
                Assert.That(root.GetProperty("message").GetString(), Is.EqualTo(rawWrapped));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Test]
    public void TestLogMessage_RespectsEnabledAndAdminFlags_AndWritesSessionFile()
    {
        var cfg = IoCManager.Resolve<IConfigurationManager>();
        var resMan = IoCManager.Resolve<IResourceManager>();
        var chatLog = (ChatLogManager) IoCManager.Resolve<IChatLogManager>();

        cfg.SetCVar(CCVars.ChatLogDirectory, "/test-chat-logs");
        cfg.SetCVar(CCVars.ChatLogEnabled, false);
        cfg.SetCVar(CCVars.ChatLogRecordAdmin, false);

        chatLog.Initialize();
        try
        {
            // Logging while disabled should not create the directory or write files.
            chatLog.LogMessage(ChatChannel.Local, "[Name]Alice[/Name] says, \"Ignored\"");
            chatLog.Flush();

            var logDir = new ResPath("/test-chat-logs");
            Assert.That(resMan.UserData.IsDir(logDir), Is.False);

            // Enable logging and log a Local message + an AdminChat message (which should be filtered by default).
            cfg.SetCVar(CCVars.ChatLogEnabled, true);
            chatLog.LogMessage(ChatChannel.Local, "[Name]Alice[/Name] says, \"Hello world!\"");
            chatLog.LogMessage(ChatChannel.AdminChat, "ADMIN: Bob: Secret admin text");
            chatLog.Flush();

            Assert.That(resMan.UserData.IsDir(logDir), Is.True);
            var files = resMan.UserData.DirectoryEntries(logDir).ToList();
            Assert.That(files, Has.Count.EqualTo(1));
            Assert.That(files[0], Does.EndWith("_en-test-station-14.jsonl"));

            var filePath = logDir / files[0];
            var lines = resMan.UserData.ReadAllText(filePath)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            Assert.That(lines, Has.Length.EqualTo(1));
            using (var doc = JsonDocument.Parse(lines[0]))
            {
                Assert.That(doc.RootElement.GetProperty("channel").GetString(), Is.EqualTo("Local"));
                Assert.That(doc.RootElement.GetProperty("message").GetString(), Is.EqualTo("[Name]Alice[/Name] says, \"Hello world!\""));
            }

            // Enabling ChatLogRecordAdmin should allow admin channels to be logged.
            cfg.SetCVar(CCVars.ChatLogRecordAdmin, true);
            chatLog.LogMessage(ChatChannel.AdminChat, "ADMIN: Bob: Visible admin text");
            chatLog.Flush();

            lines = resMan.UserData.ReadAllText(filePath)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.That(lines, Has.Length.EqualTo(2));
        }
        finally
        {
            chatLog.Shutdown();
        }
    }

    [Test]
    public void TestPruneOldLogs_DeletesExpiredFilesAndKeepsRecentOnes()
    {
        var cfg = IoCManager.Resolve<IConfigurationManager>();
        var resMan = IoCManager.Resolve<IResourceManager>();
        var chatLog = (ChatLogManager) IoCManager.Resolve<IChatLogManager>();

        var logDir = new ResPath("/prune-chat-logs");
        resMan.UserData.CreateDir(logDir);

        var expiredFile = logDir / "2026-10-01_12-00-00_old-server.jsonl";
        var recentFile = logDir / "2026-10-14_12-00-00_recent-server.jsonl";

        resMan.UserData.WriteAllText(expiredFile, "{}\n");
        resMan.UserData.WriteAllText(recentFile, "{}\n");

        cfg.SetCVar(CCVars.ChatLogRetentionDays, 7);
        chatLog.Initialize();
        try
        {
            var nowLocal = new DateTime(2026, 10, 16, 12, 0, 0);
            chatLog.PruneOldLogs(logDir, nowLocal);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(resMan.UserData.Exists(expiredFile), Is.False);
                Assert.That(resMan.UserData.Exists(recentFile), Is.True);
            }
        }
        finally
        {
            chatLog.Shutdown();
        }
    }
}
