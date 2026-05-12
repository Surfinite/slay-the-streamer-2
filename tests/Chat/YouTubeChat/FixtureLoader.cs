using System.IO;

namespace SlayTheStreamer2.Tests.Chat.YouTubeChat;

internal static class FixtureLoader {
    public static string Load(string filename) =>
        File.ReadAllText(Path.Combine("Fixtures", filename));
}
