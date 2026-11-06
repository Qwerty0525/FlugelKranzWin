using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using FlugelKranz.Compass;
using FlugelKranz.Core;
using FlugelKranz.OpenVR;
using Valve.VR;
using Xunit;

namespace FlugelKranz.Tests;

/// <summary>
/// Against a running SteamVR, only when FLUGELKRANZ_STEAMVR_SELFTEST=1: creates a hidden compass
/// overlay under its own key (never shown, no flight, no driver), sets the side-by-side flag, reads
/// the eyes and submits 320 stereo images through the D3D11 texture at about 32 a second. Writes a
/// report to FLUGELKRANZ_SELFTEST_REPORT when set. Every SteamVR call must succeed.
/// </summary>
public class CompassSteamVrSelfTest
{
    [Fact]
    public void SubmitsStereoImagesToARunningSteamVrWhenAsked()
    {
        if (Environment.GetEnvironmentVariable("FLUGELKRANZ_STEAMVR_SELFTEST") != "1") return;
        var initError = EVRInitError.None;
        Valve.VR.OpenVR.Init(ref initError, EVRApplicationType.VRApplication_Overlay);
        Assert.Equal(EVRInitError.None, initError);
        var report = new List<string>();
        try
        {
            int adapter = -1;
            Valve.VR.OpenVR.System.GetDXGIOutputInfo(ref adapter);
            report.Add($"GetDXGIOutputInfo adapter index: {adapter}");
            using var api = new OpenVrCompassOverlayApi(new object(), OpenVrSession.AppKey + ".compass-selftest");
            Assert.Null(api.Create());
            api.SetStereo();
            report.Add("SetOverlayFlag(SideBySide_Parallel): ok");
            var (left, right) = api.ReadEyes();
            report.Add($"eyes in head: left {left}, right {right}, IPD {Vector3.Distance(left, right) * 1000:0.0} mm");
            api.SetWidth(0.0511f);
            using var renderer = new CompassRenderer();
            var pixels = new byte[renderer.Size * renderer.Size * 8];
            var head = new RigidPose(Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.6f), new(0, 1.6f, 0));
            var hand = new RigidPose(Quaternion.Identity, new(0.05f, 1.15f, -0.38f));
            var held = new List<double>();
            var draw = new List<double>();
            int failures = 0;
            string? last = null;
            var total = Stopwatch.StartNew();
            for (int i = 0; i < 320; i++)
            {
                var moved = head with { Position = head.Position + new Vector3(MathF.Sin(i * 0.05f) * 0.05f, 0, 0) };
                var view = CompassMath.Compute(RigidPose.Identity, moved, hand, CompassMath.HandOffset(CompassHand.Right),
                    stereo: new(left, right, 0.0225f));
                var watch = Stopwatch.StartNew();
                renderer.Render(view.Axes, CompassGlow.Teal, view.Eyes, pixels);
                draw.Add(watch.Elapsed.TotalMilliseconds);
                try { held.Add(api.Submit(pixels, 2 * renderer.Size, renderer.Size).TotalMilliseconds); }
                catch (InvalidOperationException exception) { failures++; last = exception.Message; }
                Thread.Sleep(30);
            }
            held.Sort();
            draw.Sort();
            string Ms(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);
            report.Add($"submits: {held.Count} ok, {failures} failed{(last is null ? "" : " (last: " + last + ")")} in {total.Elapsed.TotalSeconds:0.0} s");
            if (held.Count > 0)
                report.Add($"OpenVR lock held per SetOverlayTexture: mean {Ms(held.Average())} ms, p50 {Ms(held[held.Count / 2])}, p99 {Ms(held[(int)(held.Count * 0.99)])}, max {Ms(held[^1])} ms");
            report.Add($"draw per stereo image: mean {Ms(draw.Average())} ms, p99 {Ms(draw[(int)(draw.Count * 0.99)])}, max {Ms(draw[^1])} ms");
            Assert.Equal(0, failures);
        }
        finally
        {
            Valve.VR.OpenVR.Shutdown();
            if (Environment.GetEnvironmentVariable("FLUGELKRANZ_SELFTEST_REPORT") is { Length: > 0 } path)
                File.WriteAllLines(path, report);
        }
    }
}
