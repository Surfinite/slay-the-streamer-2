using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Game.Ui.Settings;
using SlayTheStreamer2.Ti.Chat;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.Ui.Settings;

public class SettingsWriterTests {
    private string TempPath() => Path.Combine(Path.GetTempPath(), $"sts2-writer-{System.Guid.NewGuid():N}.json");

    private static ChatSettings MakeSettings(int cardSkipsPerAct = 1, int voteDur = 30,
        bool voteAct = true, bool skipAsVote = true, bool showTag = false,
        bool forceL3 = false) {
        return new ChatSettings(
            Channel: "channel",
            Credentials: new ChatCredentials("user", "oauth:" + new string('a', 30)),
            CardSkipsPerAct: cardSkipsPerAct,
            YoutubeChannelId: null,
            VoteOnActVariant: voteAct,
            ForceL3PopupFallback: forceL3,
            VoteDurationSeconds: voteDur,
            CardSkipAsVoteOption: skipAsVote,
            ShowVoteTag: showTag);
    }

    [Fact]
    public void Write_FirstRun_NoExistingFile_CreatesWithFiveKeysPlusSchema() {
        var path = TempPath();
        Assert.False(File.Exists(path));

        try {
            SettingsWriter.Write(path, MakeSettings(voteDur: 45, skipAsVote: false));

            Assert.True(File.Exists(path));
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal(1, json["schemaVersion"]!.GetValue<int>());
            Assert.Equal(45, json["voteDurationSeconds"]!.GetValue<int>());
            Assert.False(json["cardSkipAsVoteOption"]!.GetValue<bool>());
        } finally {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }
}
