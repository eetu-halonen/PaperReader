# Paper Reader

An app that reads research papers aloud and shows the math on screen while it talks. It reads other
documents just as well: books, web articles, Word files, slides, notes, and photos of pages.
It runs on Android, on the Linux desktop and in the browser (WebAssembly).

- Open a document from the app, or use *Open with* / *Share* from any other app. Sharing a link from the browser
  opens that page (or the PDF behind it). See [Documents it reads](#documents-it-reads).
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
- **Learn** (flashcards with spaced repetition): the cards button in the reader opens *Remember*, where one tap makes
  cards about the equation or figure on screen, what you just heard, or the section's main points, or you type what you
  want to remember ("why divide by √d_k"). *Make a deck* writes about one card per page covering the whole paper
  (problem, key idea, method and its equations, results, limitations); *Make more cards* adds ones the deck doesn't
  cover yet. Every Ask answer has *Make a card*, and every item in the Σ list has *Remember*. New cards are listed so
  bad ones can be removed. Reviews (*Learn* in the library, or the banner when cards are due) show the question, then
  the answer with Again / Hard / Good / Easy and when each would bring the card back; *Listen to it in the paper* jumps
  to where it is narrated. See [How cards are made and scheduled](#how-cards-are-made-and-scheduled).
- **Find papers**: search about 270 million works on [OpenAlex](https://openalex.org) (only those with a free
  PDF are listed), or paste an arXiv id or address, a DOI, or a PDF address. *Recommended for you* lists new and
  related papers from [Semantic Scholar](https://www.semanticscholar.org)'s recommender, based on the papers in
  your library (each is identified once by its title on OpenAlex). *Listen* downloads the PDF (arXiv and the
  preprint servers first, since some publishers only serve their PDFs to a browser) and prepares it like an
  opened file; if no copy can be downloaded, *Web page* opens it in the browser. Both services are free and need
  no key; OpenAlex allows about 100 searches a day without one, and a free OpenAlex key (Settings) raises that.
- **Walking mode** (the walker button in the reader, or W): a simple full-screen player for listening on the move.
  A huge Play / Pause / Continue button at the bottom, Back 15 s, Ask and Skip 15 s above it, speed and Exit at
  the top, and the equation or figure being discussed as large as the screen allows (tap it for full size). When
  playback stops at an equation, the buttons become *Hear it again*, *Ask* and *Continue*. It stays on for the
  next paper until you tap Exit; the back button leaves walking mode rather than closing the paper.
- Controls: play/pause, back and forward 15 s, speed, contents list. Playback continues in the
  background, with a media notification, lock-screen and headset controls; it pauses for calls
  and when headphones are unplugged.

Written in F# with Avalonia, FuncUI and Elmish, for .NET 10.

## Documents it reads

| Kind | How it is read |
| --- | --- |
| PDF | Layout analysis (reading order, columns, equations cut out of the page); with a Mistral key, OCR checks the equations and finds figures and tables. A scanned PDF (pictures of pages) is read with Mistral OCR. |
| Web pages (HTML) | The article is taken out of the page (menus, sidebars, footers, link lists and "See also" left out). Formulas in MathML are read from their LaTeX (Wikipedia, arXiv HTML, most converters); pictures are downloaded; tables, figures with captions and code listings are shown. Paste a web address in *Find papers*, share a link to the app, or pass it on the desktop command line. |
| EPUB | Chapter by chapter in reading order; each chapter is a "page". |
| Word (DOCX), OpenDocument (ODT) | Headings by their style, lists, tables, pictures with their captions, and Word equations (converted to LaTeX). |
| PowerPoint (PPTX) | Slide by slide: the title, the text boxes, pictures and tables, then the speaker notes. |
| Markdown, plain text | Headings, lists, `$…$` / `$$…$$` math, tables, code and embedded pictures; in plain text, chapter titles are recognised and Project Gutenberg's licence is left out. |
| Photos of pages (PNG, JPEG, WebP) | Mistral OCR (needs a key). |

Every format except PDF is read into the same blocks (headings, paragraphs, formulas, pictures, tables, listings,
page breaks; `Blocks.fs`), which become the units the narrator, Ask and Learn work with. Formulas are typeset,
tables and listings drawn, and pictures kept, once at import. Adding a format means writing one reader that
returns blocks (`Markup.fs` for text formats, `Packages.fs` for zip-based ones) and a line in `Formats.detect`.

## Projects

| Path | What it is |
| --- | --- |
| `src/PaperReader.Core` | Everything without UI: PDF layout analysis (PdfPig), the other formats (`Formats.fs`, `Markup.fs`, `Packages.fs`, `Blocks.fs`), math verbalisation (including LaTeX), narration, Mistral client, WAV cache, paper search and recommendations (`Discover.fs`), flashcards (`Cards.fs`) and their FSRS scheduler (`Fsrs.fs`) |
| `src/PaperReader` | The shared Elmish UI |
| `src/PaperReader.Android` | Android head: audio player, phone TTS, PDF crops, playback service, microphone |
| `src/PaperReader.Desktop` | Linux desktop head: ffplay audio, pdftoppm crops, optional espeak-ng, ffmpeg microphone |
| `src/PaperReader.Browser` | WebAssembly head: HTML audio, pdf.js crops, IndexedDB storage, MediaRecorder |
| `src/PaperReader.Browser.Interop` | The browser head's JavaScript imports (C#, because `[JSImport]` needs its source generator) |
| `tools/ScriptDump` | Desktop tool that analyses a document (a file or a web address) and prints the narration, for tuning the reading rules (`--blocks` prints what a reader found) |
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
to the application menu and to *Open with* for the documents it reads. It can also be started directly:
`dist/linux-x64/paper-reader paper.pdf` (or a web address). Data lives in `~/.local/share/PaperReader`.

Keys: Space play/pause, ← / → 15 seconds, A ask, Esc back. In a review: Space or Enter shows the answer (then
answers Good), 1–4 answer Again / Hard / Good / Easy.

## Browser (WebAssembly)

Needs the `wasm-tools` workload (`dotnet workload install wasm-tools`) and Python 3 for the small server.

```bash
src/PaperReader.Browser/run.sh        # publishes to dist/web and serves http://localhost:8080
```

`dist/web/wwwroot` is a static site: any web server works if it sends `.wasm` as `application/wasm` and
`.mjs` as `text/javascript`. The microphone needs a secure page (localhost, or HTTPS elsewhere).
`?url=<address>` (or `?pdf=`) opens a document from this site (or any site that allows it) straight away.

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

## How cards are made and scheduled

Cards are written by the Ask model (GLM 5.3) with the same system prompt as Ask, so it has read the whole paper and
every equation, figure and table, and the API's cache of that long prefix serves both. The request says what to make
cards about (with where you are, as Ask describes it), the cards the paper already has (so none is repeated), and rules
for good cards: one idea each, a short answer, a question that makes sense months later shuffled with other papers
("In the Transformer, …" rather than "this paper"), understanding over wording, the paper's notation and numbers. A
card can show an equation, figure or table on its front or with its answer, and remembers where in the narration its
subject is. Cards are kept per paper in `cards.json`.

Reviews are scheduled with [FSRS-5](https://github.com/open-spaced-repetition/fsrs4anki/wiki/The-Algorithm) (the
scheduler in Anki since 23.10) with its default parameters. Each card has a stability (days until the chance of
recalling it falls to 90%) and a difficulty; each answer updates both from how likely recall was at that moment, and
the card comes back when that chance will have fallen to the retention set in Settings (85, 90 or 95%). For the same
retention this needs roughly 20–30% fewer reviews than SM-2. New and forgotten cards first come back after 1 and
10 minutes, within the session. A session takes the cards in short steps first, then the reviews most likely to be
forgotten, then at most 20 new cards. Intervals of 3 days or more are spread by ±5% so a deck made at once doesn't
keep coming back on the same day. `ScriptDump --cards <paper id> <segment> <moment|section|visual|paper|text>`
makes cards from the command line against the desktop app's cache (printed, not saved).

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
