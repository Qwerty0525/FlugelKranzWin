using Xunit;

namespace FlugelKranz.Tests;

public class AppIconTests
{
    [Fact]
    public void EmbeddedIconHasTheSizesWindowsNeeds()
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream(App.IconResource);
        Assert.NotNull(stream);
        using var reader = new BinaryReader(stream);
        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16()); // icon, not cursor
        int count = reader.ReadUInt16();
        var sizes = new List<int>();
        for (int i = 0; i < count; i++)
        {
            int width = reader.ReadByte();
            reader.ReadBytes(5);
            Assert.Equal(32, reader.ReadUInt16()); // 32-bit colour
            int length = reader.ReadInt32();
            int offset = reader.ReadInt32();
            Assert.True(length > 0 && offset + length <= stream.Length);
            sizes.Add(width == 0 ? 256 : width);
        }
        Assert.Superset(new HashSet<int> { 16, 24, 32, 48, 256 }, sizes.ToHashSet());
    }
}
