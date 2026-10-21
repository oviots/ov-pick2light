using P2L.CLI.Infrastructure;

namespace P2L.UnitTests;

public sealed class LoggerTests
{
    [Fact]
    public void SizeLimitCreatesNewSegmentsAndPreservesEveryLine()
    {
        var path = Path.Combine(Path.GetTempPath(), "p2l-log-test-" + Guid.NewGuid());
        try
        {
            using (var log = new RotatingFileLoggerProvider(path, 100))
                for (int i = 0; i < 12; i++) log.Write(new string('x', 40));
            var files = Directory.GetFiles(path);
            Assert.True(files.Length > 1);
            Assert.Equal(12, files.Sum(f => File.ReadAllLines(f).Length));
            Assert.All(files, f => Assert.True(new FileInfo(f).Length <= 141));
            using (var log = new RotatingFileLoggerProvider(path, 100)) log.Write("after restart");
            Assert.Equal(13, Directory.GetFiles(path).Sum(f => File.ReadAllLines(f).Length));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
