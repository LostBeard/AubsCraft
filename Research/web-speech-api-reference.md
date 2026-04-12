# Web Speech API Reference (for AI Villager Voice Chat)

**Date:** 2026-04-12
**Source:** MDN Web Docs (https://developer.mozilla.org/en-US/docs/Web/API/Web_Speech_API)

---

## Overview

Two separate APIs:
1. **SpeechRecognition** - microphone to text (player speaks)
2. **SpeechSynthesis** - text to speech (villager responds)

Both are free, run in the browser, no API keys needed.

## Browser Support

- Chrome: Full support (desktop + Android)
- Edge: Full support
- Safari: SpeechSynthesis yes, SpeechRecognition partial
- Firefox: SpeechSynthesis yes, SpeechRecognition behind flag
- Quest Browser: Based on Chromium - likely full support (NEEDS TESTING)

## Speech Recognition (Player Voice Input)

```javascript
const recognition = new SpeechRecognition();
recognition.continuous = false;  // stop after one phrase
recognition.interimResults = true;  // show partial results
recognition.lang = 'en-US';

recognition.onresult = (event) => {
    const transcript = event.results[0][0].transcript;
    const confidence = event.results[0][0].confidence;
    // Send transcript to Claude API for AI response
};

recognition.onerror = (event) => {
    console.error('Speech error:', event.error);
};

recognition.start();  // requires user gesture (click/tap)
```

### Key constraints
- **Requires user gesture** to start (click a "talk" button)
- **Requires HTTPS** (we have this via HAProxy)
- **Online by default** - sends audio to Google/Apple servers for processing
- **On-device option** in newer Chrome (privacy-friendly, no network)
- **Permission prompt** - browser asks for microphone access

### SpawnDev.BlazorJS wrapping
Check if SpeechRecognition is already wrapped in SpawnDev.BlazorJS.
If not, create wrappers following JSObject pattern:
```csharp
public class SpeechRecognition : EventTarget
{
    public SpeechRecognition() : base(JS.New("SpeechRecognition")) { }
    public bool Continuous { get => JSRef!.Get<bool>("continuous"); set => JSRef!.Set("continuous", value); }
    public string Lang { get => JSRef!.Get<string>("lang"); set => JSRef!.Set("lang", value); }
    public void Start() => JSRef!.CallVoid("start");
    public void Stop() => JSRef!.CallVoid("stop");
    // Events
    public ActionEvent<SpeechRecognitionEvent> OnResult => ...;
    public ActionEvent<Event> OnEnd => ...;
}
```

## Speech Synthesis (Villager Voice Output)

```javascript
const utterance = new SpeechSynthesisUtterance("Hello, traveler!");
utterance.voice = speechSynthesis.getVoices().find(v => v.name === 'Google UK English Male');
utterance.pitch = 1.0;
utterance.rate = 1.0;
utterance.volume = 1.0;

speechSynthesis.speak(utterance);
```

### Voice selection
- Multiple voices available per language (male, female, different accents)
- `speechSynthesis.getVoices()` returns available voices
- Different voices for different villager personalities:
  - Town Crier: loud, dramatic voice
  - Cartographer: calm, precise voice
  - Historian: older, wise-sounding voice
  - Quest Giver: enthusiastic, young voice

### Key constraints
- **Works offline** - synthesis runs locally
- **No user gesture required** for playback (unlike recognition)
- **Quality varies** by browser/OS - Chrome voices are best
- **Voice loading is async** - voices may not be available immediately on page load

## Performance Considerations

- Speech recognition runs on a separate thread (browser handles it)
- No WASM thread blocking for audio processing
- The only .NET involvement: receiving the transcript text and sending to Claude API
- Claude API call is async HTTP - doesn't block rendering
- Speech synthesis is fire-and-forget from .NET's perspective

## Integration Architecture

```
Player clicks "Talk" button on villager
  -> SpeechRecognition.start() [JS, async, off-thread]
  -> Player speaks into mic
  -> SpeechRecognition.onresult -> transcript text
  -> Send transcript to .NET via BlazorJS callback
  -> .NET sends to Claude API (with villager persona + server context)
  -> Claude response text received
  -> SpeechSynthesisUtterance(response) [JS, async]
  -> Villager "speaks" the response
  -> Chat log updated with both player and villager messages
```

Total latency: ~1-3 seconds (mostly Claude API response time).
No GPU or WASM thread involvement for audio.

## VR-Specific Voice Chat

In WebXR VR mode:
- Microphone access works in VR (Quest has built-in mic)
- Speech synthesis plays through Quest speakers/headphones
- The "Talk" button becomes a controller trigger press
- Spatial audio: villager voice could be positioned in 3D space (Web Audio API + XR)
- This creates an incredibly immersive experience - walk up to a villager in VR and talk to them
