# Paper Reader

An app that reads research papers (PDF) aloud and shows the math on screen while it talks.
It runs on Android, on the Linux desktop and in the browser (WebAssembly).

- Open a PDF from the app, or use *Open with* / *Share* from any other app.
- The paper is analysed once: reading order, two-column layout, headings, display equations,
  algorithms and inline math. Citations, headers and footers are skipped.
- Equations are cut out of the page as images and shown while the narration talks about them.
- With a Mistral API key, a Mistral chat model rewrites the paper for listening (it sees the
  equation images) and Voxtral reads it aloud. Without one, an offline narration and the
  phone's own text-to-speech are used.
- Audio is made a few sentences ahead of the listener and cached, so nothing is generated twice.
- Equation images are checked to be whole: a crop grows (up to 8 pt per side) while a glyph touches its
  edge, and with a Mistral key every display equation is read by Mistral OCR, which also corrects crops
  that miss part of an equation. An equation that still can't be cut out whole is typeset from the OCR'd
  LaTeX instead (CSharpMath).
- Figures and tables (with a Mistral key): Mistral OCR finds each figure, chart and table with its caption,
  and it is cut out whole. The caption is read where it sits, followed by a short description of the
  picture, and the image is on screen then and whenever the text refers to it ("see Fig. 3", "Table 2").
- *Stop at equations* / *Stop at figures and tables* (Settings): after one has been read and explained,
  playback pauses with it on screen until you tap Continue (or *Hear it again*).
- The Σ button lists every equation, figure and table: the ones heard so far (latest first) and the ones
  coming up, each with *Listen from here*; tap one to see it full size.
- **Ask** (with a Mistral key): pauses playback and answers questions about the part you are listening to.
  One-tap questions fit the moment: walk me through the equation or figure on screen, I didn't get that,
  give an example, why does it matter, what is *BLEU* (jargon just heard), recap so far. Every answer
  offers three follow-ups, so typing is rarely needed; you can also type, or tap the microphone and ask
  out loud. Answers can be read to you, show the equation or figure they talk about, and are kept per paper.
  *Ask about it* on any item in the Σ list asks about that one. See [How Ask answers](#how-ask-answers).
- **Find papers**: search about 270 million works on [OpenAlex](https://openalex.org) (only those with a free
  PDF are listed), or paste an arXiv id or address, a DOI, or a PDF address. *Recommended for you* lists new and
  related papers from [Semantic Scholar](https://www.semanticscholar.org)'s recommender, based on the papers in
  your library (each is identified once by its title on OpenAlex). *Listen* downloads the PDF (arXiv and the
  preprint servers first, since some publishers only serve their PDFs to a browser) and prepares it like an
  opened file; if no copy can be downloaded, *Web page* opens it in the browser. Both services are free and need
  no key; OpenAlex allows about 100 searches a day without one, and a free OpenAlex key (Settings) raises that.
- Controls: play/pause, back and forward 15 s, speed, contents list. Playback continues in the
  background, with a media notification, lock-screen and headset controls; it pauses for calls
  and when headphones are unplugged.

Written in F# with Avalonia, FuncUI and Elmish, for .NET 10.

## Projects

| Path | What it is |
| --- | --- |
| `src/PaperReader.Core` | Everything without UI: PDF layout analysis (PdfPig), math verbalisation, narration, Mistral client, WAV cache, paper search and recommendations (`Discover.fs`) |
| `src/PaperReader` | The shared Elmish UI |
| `src/PaperReader.Android` | Android head: audio player, phone TTS, PDF crops, playback service, microphone |
| `src/PaperReader.Desktop` | Linux desktop head: ffplay audio, pdftoppm crops, optional espeak-ng, ffmpeg microphone |
| `src/PaperReader.Browser` | WebAssembly head: HTML audio, pdf.js crops, IndexedDB storage, MediaRecorder |
| `src/PaperReader.Browser.Interop` | The browser head's JavaScript imports (C#, because `[JSImport]` needs its source generator) |
| `tools/ScriptDump` | Desktop tool that analyses a PDF and prints the narration, for tuning the layout rules |
| `tests/PaperReader.Core.Tests` | Unit tests |

## Build

Needs the .NET 10 SDK with the `android` workload, the Android SDK and a JDK.

```bash
# unit tests
dotnet test --project tests/PaperReader.Core.Tests

# APK (self-contained, arm64 and x64)
cd src/PaperReader.Android
dotnet build -c Release -p:AndroidSdkDirectory=$HOME/Android/Sdk -p:JavaSdkDirectory=/opt/android-studio/jbr
# -> bin/Release/net10.0-android/app.paperreader-Signed.apk

adb install -r bin/Release/net10.0-android/app.paperreader-Signed.apk
```

The APK is signed with the debug key. To publish it, sign it with your own keystore
(`-p:AndroidKeyStore=true -p:AndroidSigningKeyStore=... -p:AndroidSigningKeyAlias=...`).

## Linux desktop

Needs `pdftoppm` (poppler-utils) and `ffplay` (ffmpeg); `espeak-ng` is used as the offline voice
when there is no Mistral key.

```bash
src/PaperReader.Desktop/install.sh
```

This publishes a self-contained build to `dist/linux-x64/paper-reader` and adds *Paper Reader*
to the application menu and to *Open with* for PDFs. It can also be started directly:
`dist/linux-x64/paper-reader paper.pdf`. Data lives in `~/.local/share/PaperReader`.

Keys: Space play/pause, ← / → 15 seconds, A ask, Esc back.

## Browser (WebAssembly)

Needs the `wasm-tools` workload (`dotnet workload install wasm-tools`) and Python 3 for the small server.

```bash
src/PaperReader.Browser/run.sh        # publishes to dist/web and serves http://localhost:8080
```

`dist/web/wwwroot` is a static site: any web server works if it sends `.wasm` as `application/wasm` and
`.mjs` as `text/javascript`. The microphone needs a secure page (localhost, or HTTPS elsewhere).
`?pdf=<address>` opens a PDF from this site (or any site that allows it) straight away.

Differences from the apps: a Mistral key is needed to read aloud (the browser has no voice that makes
audio files); papers, settings and audio are kept in the browser's IndexedDB, and a paper's audio is
loaded when you open it. PDF pages are drawn by pdf.js (bundled in `wwwroot/pdfjs`).

## Mistral

Open *Settings* and paste an API key from [console.mistral.ai](https://console.mistral.ai).
The key is stored only on the device and sent only to `api.mistral.ai`.

- **Explain with Mistral**: narration by a chat model (default `mistral-medium-latest`).
  Applies to papers added after the change.
- **Mistral voice**: Voxtral text-to-speech; pick a voice with *Load voices*.
  Each voice has its own audio cache.
- **Ask**: answered by GLM 5.3 (`zai-glm-5-3`, hosted by Mistral); questions asked aloud are transcribed by
  Voxtral. *About you* (e.g. "biology PhD student, rusty on linear algebra") sets the level of the answers.

## How Ask answers

GLM 5.3 can't see images and knows nothing of where you are, so every question carries:

- the whole paper as Mistral OCR read it (formulas in LaTeX, tables as tables), saved as `paper.md` at
  import (older papers get it on the first question);
- every equation, figure and table by its id, with its LaTeX or caption, what the narration said about it,
  and for figures a detailed description made once by the vision model (`help/figures.json`);
- where you are: the section, the last sentences you heard as narrated, the one playing, what is on
  screen, and what the question is about (the equation on screen, or the item picked in the Σ list);
- earlier questions about the paper, and *About you*.

The paper and figure part comes first and is the same for every question, so the API can cache it.
The model is told to stay with the paper, say when it adds outside background, keep answers short and
spoken, and not spoil what's ahead in a recap. Quick taps use low reasoning effort (first words in about
1–2 s); walkthroughs and typed or spoken questions use high. The reply ends with the item to show and
three follow-ups, which become the next taps. `ScriptDump --ask <paper id> <segment> <question>` asks
from the command line against the desktop app's cache.

## Desktop analysis tool

```bash
dotnet run --project tools/ScriptDump -- paper.pdf --lines            # line classes
dotnet run --project tools/ScriptDump -- paper.pdf --crops /tmp/crops # equation images (needs pdftoppm, ImageMagick)
MISTRAL_API_KEY=... dotnet run --project tools/ScriptDump -- paper.pdf --ocr --crops /tmp/crops --mistral mistral-medium-latest
```

## Limitations

- Scanned (image-only) PDFs are not supported.
- Without a Mistral key there are no figures or tables, and figure labels and table cells sometimes reach
  the offline narration.
- Papers narrated before figure support get their figures on the next open, shown when referenced, but
  their captions are only read aloud after removing and adding the paper again.
- Occasionally a line that isn't an equation (a table header, a sentence full of symbols) is treated as one.
- Changing the narration model does not re-narrate papers already added (remove and add them again).
- Ask needs a Mistral key and a connection.
