using System.Numerics;
using AubsCraft.Live.Model.Voice;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace AubsCraft.Admin.Voice;

/// <summary>
/// Voice for the viewer, on the page's own thread (a worker has no microphone and no speakers): one socket to the panel (<c>/api/live/voice/ws</c>), and
/// <list type="bullet">
/// <item>the microphone: captured, cut into 20 ms Opus packets by the browser's own encoder (WebCodecs), each sent with the place the speaker is (<see cref="Where"/>: the camera, or the head in a headset);</item>
/// <item>what comes back (players in the game, other viewers): decoded by the browser, and played through a 3D panner at the place of the speaker, heard from the listener's place (<see cref="SetListener"/>),
/// so a voice is where the person is and fades out over 48 blocks as it does in the game.</item>
/// </list>
/// Nothing is recorded and nothing but the Opus packets leaves the page. The microphone is only open while <see cref="MicOn"/>.
/// </summary>
public sealed class VoiceClient : IDisposable
{
    /// <summary>How far a voice carries in the viewer, in blocks (the game's own voice chat distance).</summary>
    public const double Range = 48;
    private const float SampleRate = 48000f;

    private readonly SpawnJSRuntime _js;
    private WebSocket? _socket;
    private AudioContext? _context;
    private GainNode? _master;
    private MediaStream? _micStream;
    private AudioEncoder? _encoder;
    private ReadableStreamDefaultReader? _reader;
    private long _micRun;
    private bool _disposed;
    private readonly Dictionary<string, Voice> _voices = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private long _timestamp;

    private sealed class Voice
    {
        public AudioDecoder? Decoder;
        public PannerNode? Panner;
        public double NextTime;
        public double LastHeard = double.NegativeInfinity;
        public long Frames;
        public long Timestamp;
    }

    public VoiceClient(SpawnJSRuntime js) => _js = js;

    /// <summary>The place the user speaks from, asked for with every packet of the microphone.</summary>
    public Func<Vector3> Where { get; set; } = () => Vector3.Zero;

    /// <summary>Connected to the panel and ready to play what is said.</summary>
    public bool IsOn => _socket?.ReadyState == 1 && _context != null;

    /// <summary>The microphone is open: what is said is sent.</summary>
    public bool MicOn { get; private set; }

    /// <summary>Whispers: voices from the panel flagged as whispering (not used yet by the page).</summary>
    public long PacketsSent { get; private set; }
    public long PacketsHeard { get; private set; }

    /// <summary>The names of those heard in the last half second.</summary>
    public IReadOnlyList<string> Speaking()
    {
        double now = _clock.Elapsed.TotalSeconds;
        return _voices.Where(v => now - v.Value.LastHeard < 0.5).Select(v => v.Key).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Raised with a short line when something worth saying happens (the microphone was refused, the connection dropped).</summary>
    public event Action<string>? Notice;

    // ---- connecting ----

    /// <summary>Connect to the panel and start hearing. Call it from a click: the audio of a page may only start with one.</summary>
    public async Task<bool> StartAsync(string url)
    {
        if (_disposed) return false;
        if (IsOn) return true;
        try
        {
            _context ??= new AudioContext(new AudioContextOptions { LatencyHint = "interactive", SampleRate = SampleRate });
            _master ??= _context.CreateGain();
            _master.Connect(_context.Destination);
            await _context.Resume();

            var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var socket = new WebSocket(url);
            socket.BinaryType = "arraybuffer";
            _socket = socket;
            socket.OnOpen += _ => opened.TrySetResult(true);
            socket.OnClose += _ => { if (_socket == socket) { opened.TrySetResult(false); OnLost(); } };
            socket.OnError += _ => opened.TrySetResult(false);
            socket.OnMessage += e =>
            {
                try
                {
                    using var buffer = e.GetData<ArrayBuffer>();
                    if (buffer != null) Heard(buffer.ReadBytes());
                }
                catch (Exception ex) { Console.WriteLine("[Voice] a voice packet failed: " + ex.Message); }
                finally { e.Dispose(); }
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var winner = await Task.WhenAny(opened.Task, Task.Delay(Timeout.Infinite, timeout.Token).ContinueWith(_ => false, TaskScheduler.Default));
            if (winner != opened.Task || !await opened.Task)
            {
                Notice?.Invoke("Voice could not connect to the panel");
                CloseSocket();
                return false;
            }
            Console.WriteLine("[Voice] connected");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Voice] could not start: " + ex.Message);
            Notice?.Invoke("Voice could not start: " + ex.Message);
            return false;
        }
    }

    public void Stop()
    {
        SetMicAsync(false).GetAwaiter().GetResult();
        CloseSocket();
        foreach (var v in _voices.Values) CloseVoice(v);
        _voices.Clear();
    }

    private void OnLost()
    {
        _socket = null;
        if (!_disposed) Notice?.Invoke("Voice lost the connection to the panel");
        _ = SetMicAsync(false);
    }

    private void CloseSocket()
    {
        var s = _socket;
        _socket = null;
        if (s == null) return;
        try { s.Close(1000, "bye"); } catch { }
        s.Dispose();
    }

    // ---- what is said to the user ----

    private void Heard(byte[] frame)
    {
        if (VoiceFrames.ReadSpeech(frame) is not { } speech || _context == null || _master == null) return;
        if (!_voices.TryGetValue(speech.Name, out var voice)) _voices[speech.Name] = voice = NewVoice(speech.Name);
        voice.LastHeard = _clock.Elapsed.TotalSeconds;
        if (voice.Panner != null)
        {
            voice.Panner.PositionX.Value = speech.X;
            voice.Panner.PositionY.Value = speech.Y;
            voice.Panner.PositionZ.Value = speech.Z;
        }
        if (voice.Decoder is not { } decoder || decoder.State != "configured") return;
        // Too far behind to catch up (the tab was in the background): drop it rather than play it late
        if (voice.NextTime - _context.CurrentTime > 0.6) { voice.NextTime = 0; }
        using var data = (ArrayBuffer)speech.Opus;
        using var chunk = new OpusChunk(voice.Timestamp, 20000, data);
        voice.Timestamp += 20000;
        decoder.Decode(chunk);
        PacketsHeard++;
    }

    private Voice NewVoice(string name)
    {
        var voice = new Voice();
        var panner = _context!.CreatePanner();
        panner.PanningModel = "HRTF";
        panner.DistanceModel = "linear";
        panner.RefDistance = 2;
        panner.MaxDistance = Range;
        panner.RolloffFactor = 1;
        panner.Connect(_master!);
        voice.Panner = panner;
        var decoder = new AudioDecoder(new AudioDecoderInit
        {
            Output = new ActionCallback<AudioData>(data => Play(voice, data)),
            Error = new ActionCallback<DOMException>(e => Console.WriteLine($"[Voice] decoder for {name}: {e.Message}")),
        });
        decoder.Configure(new AudioDecoderConfig { Codec = "opus", SampleRate = (int)SampleRate, NumberOfChannels = 1 });
        voice.Decoder = decoder;
        Console.WriteLine($"[Voice] hearing {name}");
        return voice;
    }

    // A decoded 20 ms of a voice: into an audio buffer, started when the one before it ends (a little ahead of now, so the network's wobble is not heard)
    private async void Play(Voice voice, AudioData data)
    {
        try
        {
            if (_context == null || voice.Panner == null) return;
            int frames = data.NumberOfFrames;
            using var buffer = _context.CreateBuffer(1, frames, SampleRate);
            using var channel = buffer.GetChannelData(0);
            await data.CopyTo(channel, new AudioDataCopyToOptions { PlaneIndex = 0 });
            using var source = _context.CreateBufferSource();
            source.Buffer = buffer;
            source.Connect(voice.Panner);
            double now = _context.CurrentTime;
            double when = Math.Max(now + 0.06, voice.NextTime);
            source.Start((float)when, 0f);
            voice.NextTime = when + frames / (double)SampleRate;
            voice.Frames++;
        }
        catch (Exception ex) { Console.WriteLine("[Voice] playing failed: " + ex.Message); }
        finally { try { data.Close(); data.Dispose(); } catch { } }
    }

    private static void CloseVoice(Voice voice)
    {
        try { voice.Decoder?.Close(); } catch { }
        voice.Decoder?.Dispose();
        voice.Panner?.Disconnect();
        voice.Panner?.Dispose();
    }

    /// <summary>Where the listener is and which way it faces (world blocks): voices are placed in relation to it.</summary>
    public void SetListener(Vector3 position, Vector3 forward)
    {
        if (_context == null) return;
        try
        {
            var l = _context.Listener;
            l.PositionX.Value = position.X; l.PositionY.Value = position.Y; l.PositionZ.Value = position.Z;
            l.ForwardX.Value = forward.X; l.ForwardY.Value = forward.Y; l.ForwardZ.Value = forward.Z;
            l.UpX.Value = 0f; l.UpY.Value = 1f; l.UpZ.Value = 0f;
        }
        catch { /* the context is closing */ }
    }

    /// <summary>The volume of everything heard, 0 to 1.</summary>
    public void SetVolume(float volume)
    {
        if (_master != null) _master.Gain.Value = Math.Clamp(volume, 0f, 1f);
    }

    // ---- what the user says ----

    /// <summary>Open or close the microphone. Opening asks the browser's permission the first time; a refusal leaves it closed and says so.</summary>
    public async Task<bool> SetMicAsync(bool on)
    {
        if (!on)
        {
            long run = ++_micRun;
            MicOn = false;
            await ReleaseMicAsync();
            return false;
        }
        if (MicOn) return true;
        if (!IsOn) { Notice?.Invoke("Turn the voice on first"); return false; }
        long mine = ++_micRun;
        try
        {
            using var navigator = _js.Get<Navigator>("navigator");
            using var devices = navigator.MediaDevices;
            _micStream = await devices.GetUserMedia(new { video = false, audio = new { echoCancellation = true, noiseSuppression = true, autoGainControl = true, channelCount = 1 } });
            if (_micStream == null) { Notice?.Invoke("No microphone"); return false; }
            if (mine != _micRun) { await ReleaseMicAsync(); return false; }

            _encoder = new AudioEncoder(new AudioEncoderInit
            {
                Output = new ActionCallback<EncodedAudioChunk, EncodedAudioChunkMetadata>((chunk, _) => Sent(chunk)),
                Error = new ActionCallback<DOMException>(e => Console.WriteLine("[Voice] encoder: " + e.Message)),
            });
            _encoder.Configure(new AudioEncoderConfig { Codec = "opus", SampleRate = (int)SampleRate, NumberOfChannels = 1, Bitrate = 32000 });

            using var tracks = _micStream.GetTracks();
            using var track = tracks[0];
            var processor = new MediaStreamTrackProcessor(new MediaStreamTrackProcessorOptions { Track = track });
            _reader = processor.Readable.GetReader();
            MicOn = true;
            Console.WriteLine("[Voice] the microphone is open");
            _ = ReadMicAsync(_reader, _encoder, mine);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Voice] microphone: " + ex.Message);
            Notice?.Invoke("The microphone could not be opened: " + ex.Message);
            await ReleaseMicAsync();
            return false;
        }
    }

    // Audio from the microphone, 10 ms at a time, into the encoder
    private async Task ReadMicAsync(ReadableStreamDefaultReader reader, AudioEncoder encoder, long run)
    {
        try
        {
            while (run == _micRun && MicOn)
            {
                using var result = await reader.Read();
                if (result.Done) break;
                using var data = result.JSRef!.Get<AudioData>("value");
                try { if (encoder.State == "configured") encoder.Encode(data); }
                finally { data.Close(); }
            }
        }
        catch (Exception ex) { if (run == _micRun) Console.WriteLine("[Voice] reading the microphone: " + ex.Message); }
    }

    // A packet of the user's voice, to the panel
    private void Sent(EncodedAudioChunk chunk)
    {
        try
        {
            var socket = _socket;
            if (socket == null || socket.ReadyState != 1 || !MicOn) return;
            using var buffer = new ArrayBuffer((int)chunk.ByteLength);
            chunk.CopyTo(buffer);
            var opus = buffer.ReadBytes();
            var at = Where();
            socket.Send(VoiceFrames.WriteMic(at.X, at.Y, at.Z, 0, opus));
            PacketsSent++;
        }
        catch (Exception ex) { Console.WriteLine("[Voice] sending failed: " + ex.Message); }
        finally { chunk.Dispose(); }
    }

    private async Task ReleaseMicAsync()
    {
        var reader = _reader; _reader = null;
        var encoder = _encoder; _encoder = null;
        var stream = _micStream; _micStream = null;
        try { if (reader != null) { await reader.Cancel(); reader.Dispose(); } } catch { }
        try
        {
            if (stream != null)
            {
                using var tracks = stream.GetTracks();
                for (int i = 0; i < tracks.Length; i++) { using var t = tracks[i]; t.Stop(); }
                stream.Dispose();
            }
        }
        catch { }
        try { encoder?.Close(); encoder?.Dispose(); } catch { }
        MicOn = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Stop(); } catch { }
        try { _context?.Close(); } catch { }
    }
}

/// <summary>
/// An EncodedAudioChunk made from an init whose type is the string the browser wants: SpawnJS writes an enum as its number, and WebCodecs refuses "0" for the chunk's type.
/// </summary>
internal sealed class OpusChunk : EncodedAudioChunk
{
    private sealed class Init
    {
        [System.Text.Json.Serialization.JsonPropertyName("type")] public string Type { get; set; } = "key";
        [System.Text.Json.Serialization.JsonPropertyName("timestamp")] public long Timestamp { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("duration")] public long Duration { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("data")] public ArrayBuffer? Data { get; set; }
    }

    public OpusChunk(long timestamp, long duration, ArrayBuffer data)
        : base(JS.New(nameof(EncodedAudioChunk), new Init { Timestamp = timestamp, Duration = duration, Data = data })) { }
}
