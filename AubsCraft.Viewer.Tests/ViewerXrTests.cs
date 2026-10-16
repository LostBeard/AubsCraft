using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace AubsCraft.Viewer.Tests;

/// <summary>
/// The viewer in a headset, against a MOCK WebXR device (no headset here): a session whose animation frames hand out two eyes with real projections, a head that can be moved and a
/// thumbstick. Everything on the viewer's side is real: the session, the page-thread world with WebGPU, the copy to a WebGL context, the locomotion. The mock's layer draws into a plain
/// canvas, which a script mirrors onto the page so the test can look at what a headset would have shown.
///
///   AUBS_VIEWER_PUBLISH  folder from `dotnet publish AubsCraft.Admin.Server -c Release -o ...` (required)
///   AUBS_VIEWER_WORLD    Minecraft server folder with world/ (default M:/opt/minecraft/server)
/// </summary>
[NonParallelizable]
public class ViewerXrTests
{
    private ViewerRig _rig = null!;
    private string _outDir = null!;

    // A WebXR device: isSessionSupported, requestSession (immersive-vr / immersive-ar), XRWebGLLayer, frames at ~60 Hz with a head at (0, 1.7, 0) and eyes 64 mm apart.
    // window.__xr.left / .right / .exit steer the controllers; window.__xr.frames counts frames. Each frame is mirrored onto #xr-mirror after the page drew it.
    private const string MockXr = """
        (() => {
          const xr = window.__xr = { left: [0, 0], right: [0, 0], exit: false, frames: 0, ended: false, mode: null, blend: 'opaque' };
          const f = 1 / Math.tan(Math.PI / 4); // 90 degree vertical field of view, square eyes
          const near = 0.05, far = 3000;
          const proj = new Float32Array([f,0,0,0, 0,f,0,0, 0,0,-(far+near)/(far-near),-1, 0,0,-2*far*near/(far-near),0]);
          class Layer {
            constructor(session, gl) {
              this.session = session; this.gl = gl;
              gl.canvas.width = 2048; gl.canvas.height = 1024;
              this.framebuffer = null; this.framebufferWidth = 2048; this.framebufferHeight = 1024;
              this.antialias = false; this.ignoreDepthValues = false;
            }
            getViewport(view) { return view.eye === 'left' ? { x: 0, y: 0, width: 1024, height: 1024 } : { x: 1024, y: 0, width: 1024, height: 1024 }; }
          }
          window.XRWebGLLayer = Layer;
          const mirror = () => {
            let m = document.getElementById('xr-mirror');
            if (!m) {
              m = document.createElement('canvas'); m.id = 'xr-mirror';
              m.style.cssText = 'position:fixed;left:0;top:0;width:1024px;height:512px;z-index:99999;background:#f0f';
              document.body.appendChild(m);
            }
            const gl = xr.glCanvas;
            if (gl && gl.width) { m.width = gl.width; m.height = gl.height; m.getContext('2d').drawImage(gl, 0, 0); }
          };
          const pose = (x, eye) => ({ eye, projectionMatrix: proj, transform: { position: { x, y: 1.7, z: 0, w: 1 }, orientation: { x: 0, y: 0, z: 0, w: 1 } }, isFirstPersonObserver: false });
          const makeSession = (mode) => {
            const s = new EventTarget();
            xr.mode = mode; xr.ended = false;
            s.environmentBlendMode = mode === 'immersive-ar' ? 'alpha-blend' : 'opaque';
            xr.blend = s.environmentBlendMode;
            s.renderState = {};
            s.requestReferenceSpace = async () => ({});
            s.updateRenderState = (init) => { Object.assign(s.renderState, init); if (init.baseLayer) xr.glCanvas = init.baseLayer.gl.canvas; };
            let handle = 0, timer = 0;
            s.requestAnimationFrame = (cb) => { handle++; timer = setTimeout(() => {
              if (xr.ended) return;
              xr.frames++;
              const frame = {
                session: s,
                getViewerPose: () => ({ transform: { position: { x: 0, y: 1.7, z: 0, w: 1 }, orientation: { x: 0, y: 0, z: 0, w: 1 } }, views: [pose(-0.032, 'left'), pose(0.032, 'right')] }),
              };
              cb(performance.now(), frame);
              mirror();
            }, 16); return handle; };
            const pad = (stick, extraPressed) => ({ axes: [0, 0, stick[0], stick[1]], buttons: [0,1,2,3,4,5].map(i => ({ pressed: i === 5 && extraPressed, touched: false, value: 0 })) });
            Object.defineProperty(s, 'inputSources', { get: () => [
              { handedness: 'left', gamepad: pad(xr.left, false) },
              { handedness: 'right', gamepad: pad(xr.right, xr.exit) },
            ] });
            s.end = async () => { xr.ended = true; clearTimeout(timer); s.dispatchEvent(new Event('end')); };
            return s;
          };
          Object.defineProperty(navigator, 'xr', { configurable: true, value: {
            isSessionSupported: async () => true,
            requestSession: async (mode) => makeSession(mode),
            addEventListener() {}, removeEventListener() {},
          } });
        })();
        """;

    [OneTimeSetUp]
    public async Task StartRigAsync()
    {
        var publish = Environment.GetEnvironmentVariable("AUBS_VIEWER_PUBLISH");
        Assert.That(publish, Is.Not.Null.And.Not.Empty, "Set AUBS_VIEWER_PUBLISH to a `dotnet publish AubsCraft.Admin.Server -c Release` folder");
        var world = Environment.GetEnvironmentVariable("AUBS_VIEWER_WORLD") ?? "M:/opt/minecraft/server";
        var tag = Environment.GetEnvironmentVariable("AUBS_VIEWER_TAG") ?? "xr";
        _outDir = Path.Combine(ViewerShotTests.RepoRoot(), "screenshots", "viewer", tag);
        Directory.CreateDirectory(_outDir);
        _rig = await ViewerRig.StartAsync(publish!, world);
    }

    [OneTimeTearDown]
    public async Task StopRigAsync()
    {
        if (_rig != null) await _rig.DisposeAsync();
    }

    private static Bitmap Crop(string file, Rectangle r)
    {
        using var all = new Bitmap(file);
        return all.Clone(r, all.PixelFormat);
    }

    // Distinct colours on a coarse grid: a blank or flat frame has one or two
    private static double Spread(Bitmap bmp)
    {
        double sum = 0, sumSq = 0; long n = 0;
        for (int y = 0; y < bmp.Height; y += 4)
            for (int x = 0; x < bmp.Width; x += 4)
            {
                var p = bmp.GetPixel(x, y);
                double l = 0.3 * p.R + 0.59 * p.G + 0.11 * p.B;
                sum += l; sumSq += l * l; n++;
            }
        double mean = sum / n;
        return Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));
    }

    [Test]
    public async Task EnteringVr_ShowsTwoEyesOfTheWorld_AndTheThumbstickFliesIt()
    {
        var page = await _rig.Context.NewPageAsync();
        var log = new List<string>();
        var errors = new List<string>();
        page.Console += (_, m) => { log.Add(m.Text); if (m.Type == "error") errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        await page.AddInitScriptAsync(MockXr);
        try
        {
            await page.GotoAsync(_rig.BaseUrl + "/map?hud=0&time=6000&cam=0,120,0,-15,0", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 100, quietSeconds: 5, maxSeconds: 90);

            var enter = page.GetByRole(AriaRole.Button, new() { Name = "Enter VR" });
            await enter.WaitForAsync(new() { Timeout = 30_000 });
            Assert.That(await page.GetByRole(AriaRole.Button, new() { Name = "Enter AR" }).CountAsync(), Is.EqualTo(1), "the mock supports AR too");
            await enter.ClickAsync();

            // The world starts on the page thread and the first eyes come after its columns
            for (int i = 0; i < 90 && !log.Any(l => l.Contains("[Xr] world started")); i++) await Task.Delay(1000);
            Assert.That(log.Any(l => l.Contains("[Xr] immersive-vr session started")), Is.True, "the session started:\n" + string.Join("\n", log.Where(l => l.Contains("Xr") || l.Contains("Map"))));
            Assert.That(log.Any(l => l.Contains("[Xr] world started")), Is.True, "the headset world started");
            await Task.Delay(12_000);   // columns stream in around the head

            var still = Path.Combine(_outDir, "vr-still.png");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = still, Clip = new Clip { X = 0, Y = 0, Width = 1024, Height = 512 } });
            using (var left = Crop(still, new Rectangle(0, 0, 512, 512)))
            using (var right = Crop(still, new Rectangle(512, 0, 512, 512)))
            {
                TestContext.Progress.WriteLine($"[xr] eye spread left {Spread(left):F1}, right {Spread(right):F1}");
                Assert.That(Spread(left), Is.GreaterThan(12), "the left eye shows the world, not a flat colour");
                Assert.That(Spread(right), Is.GreaterThan(12), "the right eye shows the world, not a flat colour");
            }

            // Left stick forward: the head flies forward (the camera started facing +X)
            await page.EvaluateAsync("window.__xr.left = [0, -1]");
            await Task.Delay(4000);
            await page.EvaluateAsync("window.__xr.left = [0, 0]");
            var heads = log.Select(l => Regex.Match(l, @"head at <([-\d.,\s]+)>")).Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
            TestContext.Progress.WriteLine("[xr] heads: " + string.Join(" | ", heads));
            Assert.That(await page.EvaluateAsync<double>("window.__xr.frames"), Is.GreaterThan(100), "frames ran");
            Assert.That(heads.Count, Is.GreaterThanOrEqualTo(2), "the host logs the head position");
            float X(string s) => float.Parse(s.Split(',')[0].Trim(), CultureInfo.InvariantCulture);
            Assert.That(X(heads[^1]) - X(heads[0]), Is.GreaterThan(5f), "the stick flew the head forward along +X");

            // B / Y leaves
            await page.EvaluateAsync("window.__xr.exit = true");
            for (int i = 0; i < 20 && !log.Any(l => l.Contains("[Xr] session ended")); i++) await Task.Delay(500);
            await page.EvaluateAsync("window.__xr.exit = false");
            Assert.That(log.Any(l => l.Contains("[Xr] session ended")), Is.True, "the exit button ended the session");

            // The desktop view carries on from where the head was
            await Task.Delay(3000);
            var after = await ViewerStats.ReadAsync(page);
            Assert.That(after, Is.Not.Null);
            TestContext.Progress.WriteLine($"[xr] after: camera ({after!.X:F0}, {after.Y:F0}, {after.Z:F0}), {after.Fps} FPS");
            Assert.That(after.X, Is.GreaterThan(5), "the desktop camera took over at the head's place");
            Assert.That(errors, Is.Empty, "The page logged errors");
        }
        finally
        {
            try { File.WriteAllLines(Path.Combine(_outDir, "console-vr.log"), log); } catch { }
            await page.CloseAsync();
        }
    }

    [Test]
    public async Task EnteringAr_LeavesTheSkyTransparent_SoThePassThroughShowsThrough()
    {
        var page = await _rig.Context.NewPageAsync();
        var log = new List<string>();
        var errors = new List<string>();
        page.Console += (_, m) => { log.Add(m.Text); if (m.Type == "error") errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        await page.AddInitScriptAsync(MockXr);
        try
        {
            await page.GotoAsync(_rig.BaseUrl + "/map?hud=0&time=6000&cam=0,120,0,-15,0", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 100, quietSeconds: 5, maxSeconds: 90);
            var enter = page.GetByRole(AriaRole.Button, new() { Name = "Enter AR" });
            await enter.WaitForAsync(new() { Timeout = 30_000 });
            await enter.ClickAsync();
            for (int i = 0; i < 90 && !log.Any(l => l.Contains("[Xr] world started")); i++) await Task.Delay(1000);
            Assert.That(log.Any(l => l.Contains("[Xr] immersive-ar session started (blend mode alpha-blend)")), Is.True, "an AR session with a passthrough blend mode started");
            await Task.Delay(12_000);

            var shot = Path.Combine(_outDir, "ar-still.png");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = shot, Clip = new Clip { X = 0, Y = 0, Width = 1024, Height = 512 } });
            using var bmp = new Bitmap(shot);
            // The mirror's own background is magenta: where the headset layer is transparent it shows. The sky (top rows) must be see-through, the terrain (bottom) opaque.
            var sky = bmp.GetPixel(256, 40);
            var ground = bmp.GetPixel(256, 500);
            TestContext.Progress.WriteLine($"[xr] AR sky pixel {sky}, ground pixel {ground}");
            Assert.That((sky.R, sky.G, sky.B), Is.EqualTo((255, 0, 255)), "the sky shows what is behind the layer (magenta here)");
            Assert.That(ground.G, Is.GreaterThan(60), "the terrain is drawn, not see-through");
            await page.EvaluateAsync("window.__xr.exit = true");
            for (int i = 0; i < 20 && !log.Any(l => l.Contains("[Xr] session ended")); i++) await Task.Delay(500);
            Assert.That(errors, Is.Empty, "The page logged errors");
        }
        finally
        {
            try { File.WriteAllLines(Path.Combine(_outDir, "console-ar.log"), log); } catch { }
            await page.CloseAsync();
        }
    }
}
