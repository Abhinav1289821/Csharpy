using MK.Hq.Engine.Capture;
using Xunit.Abstractions;

namespace MK.Hq.Tests.Capture;

public sealed class OrientationProbe(ITestOutputHelper output)
{
    [Fact]
    public void Text_direction_scores_for_every_scan()
    {
        foreach (var f in Directory.GetFiles(@"C:\MK_WORKSPACE\scans").Order())
        {
            var jpeg = ImageNormalizer.Normalize(File.ReadAllBytes(f));
            var (rows, cols) = ImageNormalizer.LineStructure(jpeg);
            output.WriteLine($"{Path.GetFileName(f),-44} rows {rows:0.000} cols {cols:0.000} → {(ImageNormalizer.LooksSideways(jpeg) ? "SIDEWAYS" : "upright")}");
        }
    }
}
