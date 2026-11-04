using Alcoler.Audio;
using Xunit;

namespace Alcoler.Tests;

public sealed class TaskAlertSoundsTests
{
    [Theory]
    [InlineData("TaskCompleted")]
    [InlineData("AwaitingConfirmation")]
    public void EmbeddedSound_IsAValidAudiblePcmWave(string name)
    {
        using var stream = typeof(TaskAlertSounds).Assembly
            .GetManifestResourceStream($"Alcoler.Assets.Sounds.{name}.wav");
        Assert.NotNull(stream);
        using var reader = new BinaryReader(stream);
        Assert.Equal("RIFF", new string(reader.ReadChars(4)));
        reader.ReadInt32();
        Assert.Equal("WAVE", new string(reader.ReadChars(4)));
        stream.Position = 36;
        Assert.Equal("data", new string(reader.ReadChars(4)));
        var length = reader.ReadInt32();
        Assert.True(length > 15000, $"{name} is too short to be audible");
        Assert.Equal(length, (int)stream.Length - 44);
    }
}
