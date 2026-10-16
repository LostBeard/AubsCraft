using System.Text;
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

    // Where the head starts, facing +X: 120 up over the real world's spawn; AUBS_XR_CAM = x,y,z,pitch,yaw places it over some other world (the scene world's platform: -10,-52,30,-20,0)
    private static string Cam => Environment.GetEnvironmentVariable("AUBS_XR_CAM") ?? "0,120,0,-15,0";

    // A WebXR device: isSessionSupported, requestSession (immersive-vr / immersive-ar), XRWebGLLayer, frames at ~60 Hz with a head at (0, 1.7, 0) and eyes 64 mm apart.
    // window.__xr.left / .right / .exit steer the controllers; window.__xr.frames counts frames. Each frame is mirrored onto #xr-mirror after the page drew it.
    private const string MockXr = """
        (() => {
          const xr = window.__xr = { jitter: 0, left: [0, 0], right: [0, 0], exit: false, x: false, trigger: false, leftTrigger: false, leftThumb: false, rightThumb: false, rightA: false, frames: 0, ended: false, mode: null, blend: 'opaque',
            rayRight: { p: [0.25, 1.3, -0.2], q: [0, 0, 0, 1] }, rayLeft: { p: [-0.25, 1.3, -0.2], q: [-0.7071068, 0, 0, 0.7071068] } };
          // The right controller points from where it is at a point of the room (the left one points at the floor)
          xr.aimAt = (tx, ty, tz) => {
            const p = xr.rayRight.p; let dx = tx - p[0], dy = ty - p[1], dz = tz - p[2];
            const len = Math.hypot(dx, dy, dz); dx /= len; dy /= len; dz /= len;
            // the rotation taking (0,0,-1) to d: axis = (0,0,-1) x d = (dy, -dx, 0), angle = acos(-dz)
            const angle = Math.acos(Math.max(-1, Math.min(1, -dz))); const ax = dy, ay = -dx; const al = Math.hypot(ax, ay) || 1;
            const s = Math.sin(angle / 2);
            xr.rayRight.q = [ax / al * s, ay / al * s, 0, Math.cos(angle / 2)];
          };
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
            m.style.display = 'block';
            const gl = xr.glCanvas;
            if (gl && gl.width) { m.width = gl.width; m.height = gl.height; m.getContext('2d').drawImage(gl, 0, 0); }
          };
          const wob = () => { const t = xr.frames * 0.37; return { x: xr.jitter * Math.cos(t * 1.3), y: xr.jitter * Math.sin(t), z: xr.jitter * Math.sin(t * 0.7) }; };
          const pose = (x, eye) => { const w = wob(); return { eye, projectionMatrix: proj, transform: { position: { x: x + w.x, y: 1.7 + w.y, z: w.z, w: 1 }, orientation: { x: 0, y: 0, z: 0, w: 1 } }, isFirstPersonObserver: false }; };
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
                getPose: (space) => { const r = space.hand === 'left' ? xr.rayLeft : xr.rayRight; return { transform: { position: { x: r.p[0], y: r.p[1], z: r.p[2], w: 1 }, orientation: { x: r.q[0], y: r.q[1], z: r.q[2], w: r.q[3] } } }; },
                getViewerPose: () => { const w = wob(); return { transform: { position: { x: w.x, y: 1.7 + w.y, z: w.z, w: 1 }, orientation: { x: 0, y: 0, z: 0, w: 1 } }, views: [pose(-0.032, 'left'), pose(0.032, 'right')] }; },
              };
              cb(performance.now(), frame);
              mirror();
            }, 16); return handle; };
            const pad = (stick, down) => ({ axes: [0, 0, stick[0], stick[1]], buttons: [0,1,2,3,4,5].map(i => ({ pressed: !!down[i], touched: false, value: 0 })) });
            Object.defineProperty(s, 'inputSources', { get: () => [
              { handedness: 'left', targetRaySpace: { hand: 'left' }, gamepad: pad(xr.left, { 4: xr.x, 0: xr.leftTrigger, 3: xr.leftThumb }) },
              { handedness: 'right', targetRaySpace: { hand: 'right' }, gamepad: pad(xr.right, { 5: xr.exit, 0: xr.trigger, 3: xr.rightThumb, 4: xr.rightA }) },
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
            await page.GotoAsync(_rig.BaseUrl + "/map?hud=0&time=6000&cam=" + Cam, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
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
            await page.GotoAsync(_rig.BaseUrl + "/map?hud=0&time=6000&cam=" + Cam, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
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

    // The panel is in the room: 1.3 m in front of the head (which the mock puts at (0, 1.7, 0) facing -Z), 12 cm below the eyes, 1.5 x 0.9375 m, 800 x 500 pixels
    private static (double X, double Y, double Z) PanelSpot(double px, double py)
        => ((px / 800 - 0.5) * 1.5, 1.58 + (0.5 - py / 500) * 0.9375, -1.3);

    // Where a room point falls in the left eye of the mock (90 degrees, square, 1024 px, the eye 3.2 cm left of the head)
    private static (int X, int Y) InLeftEye(double x, double y, double z)
        => ((int)((1 + (x + 0.032) / -z) * 512), (int)((1 - (y - 1.7) / -z) * 512));

    private static string Num(double v) => v.ToString(CultureInfo.InvariantCulture);

    private async Task<Bitmap> LeftEyeAsync(IPage page, string name)
    {
        var url = await page.EvaluateAsync<string>("document.getElementById('xr-mirror').toDataURL('image/png')");
        var file = Path.Combine(_outDir, name + ".png");
        File.WriteAllBytes(file, Convert.FromBase64String(url[(url.IndexOf(',') + 1)..]));
        using var all = new Bitmap(file);
        return all.Clone(new Rectangle(0, 0, 1024, 1024), all.PixelFormat);
    }

    // The share of sampled pixels in a box of an eye whose brightness differs by more than 25 between two shots
    private static double Changed(Bitmap a, Bitmap b, Rectangle box)
    {
        int n = 0, d = 0;
        for (int y = box.Top; y < box.Bottom; y += 2)
            for (int x = box.Left; x < box.Right; x += 2)
            {
                var p = a.GetPixel(x, y); var q = b.GetPixel(x, y);
                if (Math.Abs((0.3 * p.R + 0.59 * p.G + 0.11 * p.B) - (0.3 * q.R + 0.59 * q.G + 0.11 * q.B)) > 25) d++;
                n++;
            }
        return (double)d / n;
    }

    private static async Task AimAsync(IPage page, (double X, double Y, double Z) at)
        => await page.EvaluateAsync($"window.__xr.aimAt({Num(at.X)}, {Num(at.Y)}, {Num(at.Z)})");

    private static async Task PressAsync(IPage page, string what)
    {
        await page.EvaluateAsync($"window.__xr.{what} = true");
        await Task.Delay(500);
        await page.EvaluateAsync($"window.__xr.{what} = false");
        await Task.Delay(800);
    }

    [Test]
    public async Task TheHud_FloatsInFrontInVr_XHidesIt_AndTheControllerPointsAtIt()
    {
        var page = await _rig.Context.NewPageAsync();
        var log = new List<string>();
        var errors = new List<string>();
        page.Console += (_, m) => { log.Add(m.Text); if (m.Type == "error") errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        await page.AddInitScriptAsync(MockXr);
        try
        {
            await page.GotoAsync(_rig.BaseUrl + "/map?hud=0&hudfeed=demo&time=6000&cam=" + Cam, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 100, quietSeconds: 5, maxSeconds: 90);
            var enter = page.GetByRole(AriaRole.Button, new() { Name = "Enter VR" });
            await enter.WaitForAsync(new() { Timeout = 30_000 });
            await enter.ClickAsync();
            for (int i = 0; i < 90 && !log.Any(l => l.Contains("[Xr] world started")); i++) await Task.Delay(1000);
            Assert.That(log.Any(l => l.Contains("[Xr] world started")), Is.True, "the headset world started");
            await Task.Delay(12_000);

            // The right controller points at the sky above the panel: no dot
            await page.EvaluateAsync("window.__xr.aimAt(0.25, 3.5, -1.3)");
            await Task.Delay(1000);
            using var shown = await LeftEyeAsync(page, "hud-shown");

            // X hides it
            await PressAsync(page, "x");
            Assert.That(log.Any(l => l.Contains("[Xr] HUD panel hidden")), Is.True, "X hid the panel");
            using var hidden = await LeftEyeAsync(page, "hud-hidden");

            var tl = InLeftEye(-0.75, 2.049, -1.3); var br = InLeftEye(0.75, 1.111, -1.3);
            var panelBox = new Rectangle(tl.X + 4, tl.Y + 4, br.X - tl.X - 8, br.Y - tl.Y - 8);
            double inside = Changed(shown, hidden, panelBox);
            double below = Changed(shown, hidden, new Rectangle(0, br.Y + 40, 1024, 1024 - br.Y - 60));
            TestContext.Progress.WriteLine($"[xr] panel box {panelBox}; changed inside {inside:P1}, below it {below:P1}");
            Assert.That(inside, Is.GreaterThan(0.05), "the panel is drawn where the person looks (the picture changes inside it when it is hidden)");
            Assert.That(inside, Is.GreaterThan(below * 3), "and not elsewhere");

            // X again: it comes back
            await PressAsync(page, "x");
            Assert.That(log.Count(l => l.Contains("[Xr] HUD panel shown")), Is.EqualTo(1), "X showed it again");
            using var back = await LeftEyeAsync(page, "hud-back");
            Assert.That(Changed(back, hidden, panelBox), Is.GreaterThan(0.05), "the panel is back");

            // The controller points at the middle of the panel: a dot shows there
            var middle = PanelSpot(400, 250);
            await AimAsync(page, middle);
            await Task.Delay(1000);
            using var pointing = await LeftEyeAsync(page, "hud-pointing");
            var dot = InLeftEye(middle.X, middle.Y, middle.Z);
            double dotChange = Changed(pointing, back, new Rectangle(dot.X - 10, dot.Y - 10, 20, 20));
            TestContext.Progress.WriteLine($"[xr] dot at {dot}: {dotChange:P1} of its box changed");
            Assert.That(dotChange, Is.GreaterThan(0.08), "a dot shows where the controller points");

            // The trigger on the minimap cycles its zoom, and says so
            await AimAsync(page, PanelSpot(704, 96));
            await Task.Delay(500);
            await PressAsync(page, "trigger");
            Assert.That(log.Any(l => Regex.IsMatch(l, @"\[Xr\] HUD trigger at \(\d{3}, \d+\) on the minimap")), Is.True, "the trigger landed on the minimap:\n" + string.Join("\n", log.Where(l => l.Contains("HUD"))));
            using var zoomed = await LeftEyeAsync(page, "hud-minimap-zoom");

            // A player in the list: the trigger opens its menu
            await AimAsync(page, PanelSpot(700, 272));
            await Task.Delay(500);
            await PressAsync(page, "trigger");
            using var menu = await LeftEyeAsync(page, "hud-menu");

            await page.EvaluateAsync("window.__xr.exit = true");
            for (int i = 0; i < 20 && !log.Any(l => l.Contains("[Xr] session ended")); i++) await Task.Delay(500);
            Assert.That(log.Any(l => l.Contains("[Xr] session ended")), Is.True, "the session ended");
            Assert.That(errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", errors));
        }
        finally
        {
            try { File.WriteAllLines(Path.Combine(_outDir, "console-hud.log"), log); } catch { }
            await page.CloseAsync();
        }
    }

    [Test]
    public async Task InVr_TheControllersBuild_AndTheToolbarOnThePanelReachesTheRest()
    {
        var plugin = new FakePlugin(_rig);
        plugin.Start();
        var page = await _rig.Context.NewPageAsync();
        var log = new List<string>();
        var errors = new List<string>();
        page.Console += (_, m) => { lock (log) log.Add(m.Text); if (m.Type == "error") errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        await page.AddInitScriptAsync(MockXr);
        try
        {
            await plugin.PostWorldAsync();
            await page.GotoAsync(_rig.BaseUrl + "/map?hud=0&time=6000&dd=6&light=0&cam=-8.5,-58.0,8.5,-90,0", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 8, quietSeconds: 3, maxSeconds: 60);
            var enter = page.GetByRole(AriaRole.Button, new() { Name = "Enter VR" });
            await enter.WaitForAsync(new() { Timeout = 30_000 });
            await enter.ClickAsync();
            for (int i = 0; i < 90 && !log.ToArray().Any(l => l.Contains("[Xr] world started")); i++) await Task.Delay(1000);
            await Task.Delay(6000);

            // The right controller points down at the floor; the right thumbstick pressed in turns edit mode on
            await page.EvaluateAsync("window.__xr.aimAt(0.25, -0.5, -1.0)");
            await PressAsync(page, "rightThumb");
            for (int i = 0; i < 20 && !log.ToArray().Any(l => l.Contains("[Edit] edit mode on")); i++) await Task.Delay(300);
            Assert.That(log.ToArray().Any(l => l.Contains("[Edit] edit mode on")), Is.True, "edit mode is on:\n" + string.Join("\n", log.ToArray().Where(l => l.Contains("Edit") || l.Contains("Xr")).TakeLast(8)));
            await Task.Delay(2500);   // what the controller points at is asked of the panel
            using var pointing = await LeftEyeAsync(page, "vr-edit-pointing");

            // The right trigger places the block in hand on the floor, in front of the head
            await PressAsync(page, "trigger");
            for (int i = 0; i < 30 && plugin.Received.Count == 0; i++) await Task.Delay(200);
            Assert.That(plugin.Received.Count, Is.GreaterThanOrEqualTo(1), "the trigger placed a block:\n" + string.Join("\n", log.ToArray().Where(l => l.Contains("Edit")).TakeLast(8)));
            var placed = plugin.Received[0];
            TestContext.Progress.WriteLine($"[xr-edit] placed {placed.Block} at {string.Join(",", placed.At)}");
            Assert.That((placed.Kind, placed.Block, placed.At[1]), Is.EqualTo(("set", "minecraft:stone", -60)), "stone on top of the grass");
            Assert.That(placed.At[0], Is.InRange(-16, -1));
            await Task.Delay(2500);
            using var built = await LeftEyeAsync(page, "vr-edit-placed");

            // A breaks what is pointed at (the stone just put there), the left thumbstick pressed undoes it
            await PressAsync(page, "rightA");
            for (int i = 0; i < 30 && plugin.Received.Count < 2; i++) await Task.Delay(200);
            Assert.That(plugin.Received.Count, Is.GreaterThanOrEqualTo(2));
            Assert.That(plugin.Received[1].Block, Is.EqualTo("minecraft:air"));
            Assert.That(plugin.Received[1].At, Is.EqualTo(placed.At), "the block that was put there");
            await Task.Delay(2000);
            await PressAsync(page, "leftThumb");
            for (int i = 0; i < 30 && plugin.Received.Count < 3; i++) await Task.Delay(200);
            Assert.That(plugin.Received.Count, Is.GreaterThanOrEqualTo(3));
            Assert.That(plugin.Received[2].Kind, Is.EqualTo("undo"));

            // The toolbar on the panel: the controller points at "Blocks" and the trigger presses it
            await AimAsync(page, PanelSpot(70, 131));
            await Task.Delay(800);
            await PressAsync(page, "trigger");
            for (int i = 0; i < 20 && !log.ToArray().Any(l => l.Contains("[Edit] tool blocks")); i++) await Task.Delay(300);
            Assert.That(log.ToArray().Any(l => l.Contains("[Edit] tool blocks")), Is.True, "the toolbar's Blocks button was pressed:\n" + string.Join("\n", log.ToArray().Where(l => l.Contains("Edit") || l.Contains("HUD")).TakeLast(8)));
            await Task.Delay(1000);
            using var palette = await LeftEyeAsync(page, "vr-edit-palette");

            // No keyboard in a headset: a chip fills the search in ("glass"), the first block of the list is picked, and the right trigger then places it
            await AimAsync(page, PanelSpot(400, 94));
            await Task.Delay(700);
            await PressAsync(page, "trigger");
            await Task.Delay(800);
            using var chipped = await LeftEyeAsync(page, "vr-edit-chip");
            await AimAsync(page, PanelSpot(300, 152));
            await Task.Delay(700);
            await PressAsync(page, "trigger");
            await Task.Delay(1000);
            await page.EvaluateAsync("window.__xr.aimAt(0.25, -0.5, -1.0)");
            await Task.Delay(2500);
            int beforeGlass = plugin.Received.Count;
            await PressAsync(page, "trigger");
            for (int i = 0; i < 30 && plugin.Received.Count == beforeGlass; i++) await Task.Delay(200);
            Assert.That(plugin.Received.Count, Is.GreaterThan(beforeGlass), "a block was placed after choosing from the palette");
            Assert.That(plugin.Received[^1].Block, Does.Contain("glass"), "the chip's search and the list's first block: some glass");
            // The thumbstick pressed again leaves edit mode; the session ends cleanly
            await page.EvaluateAsync("window.__xr.aimAt(0.25, 3.5, -1.3)");
            await PressAsync(page, "rightThumb");
            for (int i = 0; i < 20 && !log.ToArray().Any(l => l.Contains("[Edit] edit mode off")); i++) await Task.Delay(300);
            Assert.That(log.ToArray().Any(l => l.Contains("[Edit] edit mode off")), Is.True);
            await page.EvaluateAsync("window.__xr.exit = true");
            for (int i = 0; i < 20 && !log.ToArray().Any(l => l.Contains("[Xr] session ended")); i++) await Task.Delay(500);
            Assert.That(errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", errors));
        }
        finally
        {
            await plugin.StopAsync();
            try { File.WriteAllLines(Path.Combine(_outDir, "console-vr-edit.log"), log.ToArray()); } catch { }
            await page.CloseAsync();
        }
    }

    // Pixels of the left eye that show the world, not the passthrough (transparent in the saved picture), in a band of rows
    private static int WorldPixels(Bitmap eye, int fromY, int toY)
    {
        int n = 0;
        for (int y = fromY; y < toY; y += 3)
            for (int x = 0; x < eye.Width; x += 3)
            {
                var p = eye.GetPixel(x, y);
                if (p.A > 8) n++;   // the headset layer is transparent where nothing is drawn
            }
        return n;
    }

    [Test]
    public async Task InAr_TheWorldIsAModelOnATable_AndLifeSizeStepsIntoIt()
    {
        var page = await _rig.Context.NewPageAsync();
        var log = new List<string>();
        var errors = new List<string>();
        page.Console += (_, m) => { lock (log) log.Add(m.Text); if (m.Type == "error") errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        await page.AddInitScriptAsync(MockXr);
        try
        {
            await page.GotoAsync(_rig.BaseUrl + "/map?hud=0&time=6000&cam=" + Cam, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 100, quietSeconds: 5, maxSeconds: 90);
            var enter = page.GetByRole(AriaRole.Button, new() { Name = "Enter AR" });
            await enter.WaitForAsync(new() { Timeout = 30_000 });
            await enter.ClickAsync();
            for (int i = 0; i < 90 && !log.ToArray().Any(l => l.Contains("[Xr] world started")); i++) await Task.Delay(1000);
            await Task.Delay(10_000);

            // The model: the upper part of the view is the passthrough (nothing drawn), the world is a small thing low on the table
            using var model = await LeftEyeAsync(page, "ar-table-model");
            int modelUp = WorldPixels(model, 0, 380), modelDown = WorldPixels(model, 540, 700);
            TestContext.Progress.WriteLine($"[ar] the model: world pixels above the horizon {modelUp}, below {modelDown}");
            Assert.That(WorldPixels(model, 700, 1024), Is.GreaterThan(500), "the model is there on the table");

            // "On the table" is the button's name at life size and "Life size" on the table: it steps into the model
            await AimAsync(page, PanelSpot(292, 131));
            await Task.Delay(800);
            await PressAsync(page, "trigger");
            await Task.Delay(6000);
            using var inside = await LeftEyeAsync(page, "ar-life-size");
            int insideUp = WorldPixels(inside, 0, 380), insideDown = WorldPixels(inside, 540, 700);
            TestContext.Progress.WriteLine($"[ar] life size: world pixels above the horizon {insideUp}, below {insideDown}");
            Assert.That(insideDown, Is.GreaterThan(modelDown + 3000), "life size: the ground reaches up to the horizon, the model only fills the table");

            // And back to the table
            await PressAsync(page, "trigger");
            await Task.Delay(5000);
            using var again = await LeftEyeAsync(page, "ar-table-again");
            Assert.That(WorldPixels(again, 540, 700), Is.LessThan(insideDown), "the model again: less of the view is world");

            await page.EvaluateAsync("window.__xr.exit = true");
            for (int i = 0; i < 20 && !log.ToArray().Any(l => l.Contains("[Xr] session ended")); i++) await Task.Delay(500);
            Assert.That(errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", errors));
        }
        finally
        {
            try { File.WriteAllLines(Path.Combine(_outDir, "console-ar-table.log"), log.ToArray()); } catch { }
            await page.CloseAsync();
        }
    }

    [Test]
    public async Task InVr_AQuickPhraseOnThePanelIsSaidInChat()
    {
        var page = await _rig.Context.NewPageAsync();
        var log = new List<string>();
        var errors = new List<string>();
        page.Console += (_, m) => { lock (log) log.Add(m.Text); if (m.Type == "error") errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        await page.AddInitScriptAsync(MockXr);
        try
        {
            await page.GotoAsync(_rig.BaseUrl + "/map?hud=0&hudfeed=demo&time=6000&cam=" + Cam, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 100, quietSeconds: 5, maxSeconds: 90);
            var enter = page.GetByRole(AriaRole.Button, new() { Name = "Enter VR" });
            await enter.WaitForAsync(new() { Timeout = 30_000 });
            await enter.ClickAsync();
            for (int i = 0; i < 90 && !log.ToArray().Any(l => l.Contains("[Xr] world started")); i++) await Task.Delay(1000);
            await Task.Delay(6000);

            // "Say something" is the first button of the toolbar; the first phrase is the first button of its panel
            await AimAsync(page, PanelSpot(64, 131));
            await Task.Delay(800);
            await PressAsync(page, "trigger");
            await Task.Delay(1000);
            using var open = await LeftEyeAsync(page, "vr-phrases");
            await AimAsync(page, PanelSpot(400, 147));
            await Task.Delay(800);
            await PressAsync(page, "trigger");
            for (int i = 0; i < 20 && !log.ToArray().Any(l => l.Contains("said from the headset")); i++) await Task.Delay(300);
            Assert.That(log.ToArray().Any(l => l.Contains("[Map] said from the headset: Hi everyone!")), Is.True, string.Join("\n", log.ToArray().Where(l => l.Contains("Map") || l.Contains("HUD")).TakeLast(8)));

            await page.EvaluateAsync("window.__xr.exit = true");
            for (int i = 0; i < 20 && !log.ToArray().Any(l => l.Contains("[Xr] session ended")); i++) await Task.Delay(500);
            Assert.That(errors, Is.Empty, "The page logged errors:\n" + string.Join("\n", errors));
        }
        finally { await page.CloseAsync(); }
    }

    // A head that does not move sees a world that does not move: frame after frame the eyes must be the same picture (flicker is the viewer's, not the world's: nothing here changes it)
    [TestCase("Enter VR")]
    [TestCase("Enter AR")]
    public async Task TheWorldHoldsStill_WhenTheHeadDoes(string button)
    {
        var page = await _rig.Context.NewPageAsync();
        var log = new List<string>();
        page.Console += (_, m) => log.Add(m.Text);
        await page.AddInitScriptAsync(MockXr);
        try
        {
            await page.GotoAsync(_rig.BaseUrl + "/map?hud=0&time=6000&cam=" + (Environment.GetEnvironmentVariable("AUBS_STILL_CAM") ?? "392,88,-1000,-40,0"), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 100, quietSeconds: 5, maxSeconds: 90);
            var enter = page.GetByRole(AriaRole.Button, new() { Name = button });
            await enter.WaitForAsync(new() { Timeout = 30_000 });
            await enter.ClickAsync();
            for (int i = 0; i < 90 && !log.Any(l => l.Contains("[Xr] world started")); i++) await Task.Delay(1000);
            await Task.Delay(15_000);   // everything streamed in and meshed

            var frames = new List<string>();
            for (int i = 0; i < 24; i++)
            {
                var file = Path.Combine(_outDir, $"still-{button.Replace(' ', '-')}-{i:00}.png");
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = file, Clip = new Clip { X = 0, Y = 0, Width = 1024, Height = 512 } });
                frames.Add(file);
            }
            int worst = 0, worstAt = 0; var counts = new List<int>();
            for (int i = 1; i < frames.Count; i++)
            {
                int changed = Changed(frames[i - 1], frames[i]);
                counts.Add(changed);
                if (changed > worst) { worst = changed; worstAt = i; }
            }
            TestContext.Progress.WriteLine($"[xr] {button}: pixels changed between frames: {string.Join(" ", counts)} (worst {worst} at {worstAt})");
            if (worst > 0) MarkChanges(frames[worstAt - 1], frames[worstAt], Path.Combine(_outDir, $"still-{button.Replace(' ', '-')}-diff.png"));
            Assert.That(worst, Is.LessThan(200), "the same picture, frame after frame");
        }
        finally { await page.CloseAsync(); }
    }

    // The sky's clouds drift; the ground (the lower part of the eyes) is what must not move
    private const int GroundFromRow = 330;

    private static int Changed(string a, string b, int fromRow = GroundFromRow, int toRow = int.MaxValue, int toColumn = int.MaxValue)
    {
        using var x = new Bitmap(a); using var y = new Bitmap(b);
        int n = 0;
        for (int j = fromRow; j < Math.Min(x.Height, toRow); j += 1)
            for (int i = 0; i < Math.Min(x.Width, toColumn); i += 1)
            {
                var p = x.GetPixel(i, j); var q = y.GetPixel(i, j);
                if (Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B) > 40) n++;
            }
        return n;
    }

    private static void MarkChanges(string a, string b, string output, int fromRow = GroundFromRow)
    {
        using var x = new Bitmap(a); using var y = new Bitmap(b);
        using var o = new Bitmap(y);
        for (int j = fromRow; j < x.Height; j++)
            for (int i = 0; i < x.Width; i++)
            {
                var p = x.GetPixel(i, j); var q = y.GetPixel(i, j);
                if (Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B) > 40) o.SetPixel(i, j, Color.Magenta);
            }
        o.Save(output);
    }

    // Leave the headset and go back in: the second session shows the world as the first did (it stayed black once)
    [TestCase("Enter VR")]
    [TestCase("Enter AR")]
    public async Task AfterLeaving_TheHeadsetCanBeEnteredAgain(string button)
    {
        var page = await _rig.Context.NewPageAsync();
        var log = new List<string>();
        var errors = new List<string>();
        page.Console += (_, m) => { log.Add(m.Text); if (m.Type == "error") errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);
        await page.AddInitScriptAsync(MockXr);
        var tag = button.Replace(' ', '-');
        try
        {
            await page.GotoAsync(_rig.BaseUrl + "/map?hud=0&time=6000&cam=" + Cam, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 20, quietSeconds: 4, maxSeconds: 60);
            for (int round = 1; round <= 3; round++)
            {
                int startedBefore = log.Count(l => l.Contains("[Xr] world started"));
                var enter = page.GetByRole(AriaRole.Button, new() { Name = button });
                await enter.WaitForAsync(new() { Timeout = 30_000 });
                await enter.ClickAsync();
                for (int i = 0; i < 90 && log.Count(l => l.Contains("[Xr] world started")) <= startedBefore; i++) await Task.Delay(1000);
                Assert.That(log.Count(l => l.Contains("[Xr] world started")), Is.GreaterThan(startedBefore), $"round {round}: the headset world started:\n" + string.Join("\n", log.Where(l => l.Contains("Xr") || l.Contains("rror")).TakeLast(15)));
                await Task.Delay(8000);

                var shot = Path.Combine(_outDir, $"reenter-{tag}-{round}.png");
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = shot, Clip = new Clip { X = 0, Y = 0, Width = 1024, Height = 512 } });
                using (var left = Crop(shot, new Rectangle(0, 0, 512, 512)))
                {
                    double spread = Spread(left);
                    TestContext.Progress.WriteLine($"[xr] {button} round {round}: left eye spread {spread:F1}, frames {await page.EvaluateAsync<double>("window.__xr.frames")}");
                    Assert.That(button == "Enter AR" || spread > 12, Is.True, $"round {round}: the eye shows the world");
                    if (button == "Enter AR") Assert.That(HasOpaquePixels(shot), Is.True, $"round {round}: something is drawn");
                }

                await page.EvaluateAsync("window.__xr.exit = true");
                for (int i = 0; i < 20 && log.Count(l => l.Contains("[Xr] session ended")) < round; i++) await Task.Delay(500);
                await page.EvaluateAsync("window.__xr.exit = false");
                Assert.That(log.Count(l => l.Contains("[Xr] session ended")), Is.GreaterThanOrEqualTo(round), $"round {round}: the session ended");
                await Task.Delay(3000);
                await page.EvaluateAsync("document.getElementById('xr-mirror').style.display = 'none'");   // the mock's mirror covers the page; a headset's does not
            }
            Assert.That(errors.Where(e => !e.Contains("whitelisted")).ToList(), Is.Empty, "The page logged errors:\n" + string.Join("\n", errors));
        }
        finally
        {
            try { File.WriteAllLines(Path.Combine(_outDir, $"console-reenter-{tag}.log"), log); } catch { }
            await page.CloseAsync();
        }
    }

    private static bool HasOpaquePixels(string file)
    {
        using var b = new Bitmap(file);
        for (int y = 0; y < b.Height; y += 8)
            for (int x = 0; x < b.Width / 2; x += 8)
            {
                var p = b.GetPixel(x, y);
                if (Math.Abs(p.R - 0xf0) + Math.Abs(p.G) + Math.Abs(p.B - 0xf0) > 90) return true;   // not the mirror's magenta backdrop
            }
        return false;
    }

    // A forest floor as the plugin sends it: grass with leaf litter (1.21.5 puts it under trees), tall grass, trunks and canopies
    private static byte[] ForestChunk()
    {
        const int top = 20;
        var palette = new[] { "minecraft:air", "minecraft:stone", "minecraft:grass_block[snowy=false]", "minecraft:oak_log[axis=y]", "minecraft:oak_leaves[distance=1,persistent=false]",
            "minecraft:leaf_litter[facing=north,segment_amount=1]", "minecraft:leaf_litter[facing=east,segment_amount=2]", "minecraft:leaf_litter[facing=south,segment_amount=3]", "minecraft:leaf_litter[facing=west,segment_amount=4]", "minecraft:short_grass", "minecraft:fern", "minecraft:sand" };
        using var ms = new MemoryStream();
        void U8(int v) => ms.WriteByte((byte)v);
        void U16(int v) { ms.WriteByte((byte)v); ms.WriteByte((byte)(v >> 8)); }
        U8(1); U8(0); U16(top); U16(palette.Length);
        foreach (var p in palette) { var b = Encoding.UTF8.GetBytes(p); U16(b.Length); ms.Write(b); }
        var trunks = new (int x, int z)[] { (3, 3), (11, 4), (6, 11), (13, 13) };
        for (int y = 0; y < top; y++)
            for (int z = 0; z < 16; z++)
                for (int x = 0; x < 16; x++)
                {
                    int wy = y - 64;
                    int id = wy < -61 ? 1 : wy == -61 ? (x < 8 ? 11 : 2) : 0;   // a sandy edge on one side
                    int h = (x * 7 + z * 13) % 11;
                    if (wy == -60 && id == 0) id = h < 4 ? 5 + h : h < 6 ? 9 : h == 6 ? 10 : 0;
                    foreach (var (tx, tz) in trunks)
                    {
                        if (x == tx && z == tz && wy >= -60 && wy <= -56) id = 3;
                        if (wy >= -57 && wy <= -55 && Math.Abs(x - tx) + Math.Abs(z - tz) <= 3 && id == 0) id = 4;
                    }
                    U16(id);
                }
        for (int i = 0; i < 256 * top; i++) U8(0xF0);
        for (int i = 0; i < 256; i++) U16(4);
        U16(0); U16(0);
        return ms.ToArray();
    }

    // Under trees, in a headset that does not keep perfectly still: the ground must not flicker
    [TestCase("Enter VR")]
    [TestCase("Enter AR")]
    public async Task AForestFloor_DoesNotFlicker_InTheHeadset(string button)
    {
        Assert.That(await _rig.PostLiveChunkAsync(-1, 0, ForestChunk()), Is.EqualTo(204), "the panel took the forest");
        var page = await _rig.Context.NewPageAsync();
        var log = new List<string>();
        page.Console += (_, m) => log.Add(m.Text);
        await page.AddInitScriptAsync(MockXr);
        var tag = button.Replace(' ', '-');
        try
        {
            await page.GotoAsync(_rig.BaseUrl + "/map?hud=0&time=6000&cam=" + (Environment.GetEnvironmentVariable("AUBS_FOREST_CAM") ?? "-8.5,-58.0,8.5,-55,0"), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 120_000 });
            await ViewerShotTests.WaitForSettledAsync(page, minSections: 20, quietSeconds: 4, maxSeconds: 60);
            var enter = page.GetByRole(AriaRole.Button, new() { Name = button });
            await enter.WaitForAsync(new() { Timeout = 30_000 });
            await enter.ClickAsync();
            for (int i = 0; i < 90 && !log.Any(l => l.Contains("[Xr] world started")); i++) await Task.Delay(1000);
            await Task.Delay(10_000);
            await page.EvaluateAsync("window.__xr.jitter = " + (Environment.GetEnvironmentVariable("AUBS_XR_JITTER") ?? "0.01"));
            var frames = new List<string>();
            for (int i = 0; i < 16; i++)
            {
                var file = Path.Combine(_outDir, $"forest-{tag}-{i:00}.png");
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = file, Clip = new Clip { X = 0, Y = 0, Width = 1024, Height = 512 } });
                frames.Add(file);
            }
            var counts = new List<int>(); int worst = 0, at = 1;
            for (int i = 1; i < frames.Count; i++) { int c = Changed(frames[i - 1], frames[i], 0); counts.Add(c); if (c > worst) { worst = c; at = i; } }
            TestContext.Progress.WriteLine($"[xr] forest {button}: pixels changed between frames: {string.Join(" ", counts)} (worst {worst})");
            // The far ground (a band of the left eye) barely moves with a head this still: what changes there is the texture sparkling
            var far = new List<int>();
            for (int i = 1; i < frames.Count; i++) far.Add(Changed(frames[i - 1], frames[i], 255, 290, 512));
            TestContext.Progress.WriteLine($"[xr] forest {button}: far ground changed: {string.Join(" ", far)} (mean {far.Average():F0})");
            if (worst > 0) MarkChanges(frames[at - 1], frames[at], Path.Combine(_outDir, $"forest-{tag}-diff.png"), 0);
            Assert.That(worst, Is.LessThan(200), "the same picture, frame after frame");
        }
        finally { await page.CloseAsync(); }
    }
}
