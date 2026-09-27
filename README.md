# Paper Reader

An app that teaches you research papers by ear. It reads the paper aloud and shows the math on screen while it talks,
and between sections a tutor explains each idea and asks you about it, so you can study a paper on a walk. It reads
other documents just as well: books, web articles, Word files, slides, notes, and photos of pages. It runs on Android,
on the Linux desktop and in the browser (WebAssembly).

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
  coming up, each with *Listen from here*; tap one to see it full screen.
- **Full screen** (tap the equation or figure on screen): it is shown as large as the screen allows. On a phone, a
  wide one is turned to run along the long side, much larger: turn the phone to read it (*Upright* keeps it upright
  from then on; also *Turn wide equations sideways* in Settings). When it is the one being listened to, *Continue*
  goes back to the player and carries on, and *Hear it again* replays it while it stays full screen.
- **Ask** (with a Mistral key): pauses playback and answers questions about the part you are listening to.
  One-tap questions fit the moment: walk me through the equation or figure on screen, I didn't get that,
  give an example, why does it matter, what is *BLEU* (jargon just heard), recap so far. Every answer
  offers three follow-ups, so typing is rarely needed; you can also type, or tap the microphone and ask
  out loud. Answers can be read to you, show the equation or figure they talk about, and are kept per paper.
  *Ask about it* on any item in the Σ list asks about that one. See [How Ask answers](#how-ask-answers).
- **Study** (with a Mistral key; how papers open): the paper is read aloud section by section, and between sections a
  tutor speaks. It gives a short lesson on each idea the section covered, which builds on what you already know, with
  the paper's equation or figure on screen and a worked example. Then it asks a question. A chime, and you answer out
  loud ("option 2", or the answer in your words) or tap it; the tutor says what it heard, why it is right or wrong,
  and goes on by itself.
  Background the next section needs comes before it. An idea you got wrong comes back a few minutes later with a
  different question. At the end of each part you explain it in your own words and hear feedback. What you learn is
  remembered across papers: the next paper skips the ideas you still know and checks the fading ones with one
  question. It is always clear who is talking: the tutor has its own voice, a short rising sound when it comes in and a
  falling one when the paper goes on, a *Tutor* badge and a tinted screen (the paper: a *Paper* badge). The ⋯ menu shows
  the plan and turns the tutor off to just listen (Settings: *Study with a tutor*). See
  [How studying works](#how-studying-works).
- **Learn** (flashcards with spaced repetition): the cards button in the reader opens *Learn*, where one tap makes
  cards about the equation or figure on screen, what you just heard, or the section's main points, or you type what you
  want to remember ("why divide by √d_k"). *Make a deck* writes about one card per page covering the whole paper
  (problem, key idea, method and its equations, results, limitations); *Make more cards* adds ones the deck doesn't
  cover yet. Every Ask answer has *Make a card*, and every item in the Σ list has *Remember*. New cards are listed so
  bad ones can be removed. Reviews (*Learn* in the library, or the banner when something is due) mix the cards with
  the ideas you studied: a card shows the question, then the answer with Again / Hard / Good / Easy and when each
  would bring the card back; an idea asks one of its questions. *Listen to it in the paper* jumps to where it is
  narrated. The Learn screen also lists *What you know*: every idea studied, how well it is remembered, when it comes
  back, and the papers it was met in. See [How cards are made and scheduled](#how-cards-are-made-and-scheduled).
- **Find papers**: search about 270 million works on [OpenAlex](https://openalex.org) (only those with a free
  PDF are listed), or paste an arXiv id or address, a DOI, or a PDF address. *Recommended for you* lists new and
  related papers from [Semantic Scholar](https://www.semanticscholar.org)'s recommender, based on the papers in
  your library (each is identified once by its title on OpenAlex). *Listen* downloads the PDF (arXiv and the
  preprint servers first, since some publishers only serve their PDFs to a browser) and prepares it like an
  opened file; if no copy can be downloaded, *Web page* opens it in the browser. Both services are free and need
  no key; OpenAlex allows about 100 searches a day without one, and a free OpenAlex key (Settings) raises that.
- **The player** is made for listening on the move, the same for listening and studying: a huge Play / Pause /
  Continue button at the bottom, Back 15 s, Ask and Skip 15 s above it, and the equation or figure being discussed as
  large as the screen allows, with the sentence being said under it (*Full screen*, or tap it, for larger). At the top:
  who is talking (*Paper* or *Tutor*), the speed, and ⋯ for everything else: contents, equations and figures,
  flashcards, the study plan, the tutor on or off, *Stop at equations*, *Stop at figures and tables*, and Settings.
  When playback stops at an equation, it is ringed and marked *Paused at Equation (n)*, the last sentence heard stays
  under it, and the buttons become *Hear it again*, *Ask* and *Continue*. With the phone on its side, the equation
  takes the left of the screen and the buttons the right.
- Playback continues in the
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
| `src/PaperReader.Core` | Everything without UI: PDF layout analysis (PdfPig), the other formats (`Formats.fs`, `Markup.fs`, `Packages.fs`, `Blocks.fs`), math verbalisation (including LaTeX), narration, Mistral client, WAV cache, paper search and recommendations (`Discover.fs`), flashcards (`Cards.fs`) and their FSRS scheduler (`Fsrs.fs`), study sessions (`Study.fs`) and what the learner knows across papers (`Knowledge.fs`) |
| `src/PaperReader` | The shared Elmish UI |
| `src/PaperReader.Android` | Android head: audio player, phone TTS, PDF crops, playback service, microphone |
| `src/PaperReader.Desktop` | Linux desktop head: ffplay audio, pdftoppm crops, optional espeak-ng, ffmpeg microphone |
| `src/PaperReader.Browser` | WebAssembly head: HTML audio, pdf.js crops, IndexedDB storage, MediaRecorder |
| `src/PaperReader.Browser.Interop` | The browser head's JavaScript imports (C#, because `[JSImport]` needs its source generator) |
| `tools/ScriptDump` | Desktop tool that analyses a document (a file or a web address) and prints the narration, for tuning the reading rules (`--blocks` prints what a reader found); it also runs Ask, cards and Study prompts against a data folder |
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

Keys: Space play/pause, ← / → 15 seconds, A ask, S turns the tutor on, Esc back. In a review: Space or Enter shows the
answer (then answers Good), 1–4 answer Again / Hard / Good / Easy, or pick an option of an idea's question. When the
tutor speaks: Space or Enter pause and go on, ← / → 15 seconds of what it says, 1–4 pick an option.

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
- **Ask**, **cards** and **Study**: written by GLM 5.3 (`zai-glm-5-3`, hosted by Mistral); questions asked aloud are
  transcribed by Voxtral. *About you* (e.g. "biology PhD student, rusty on linear algebra") sets the level of the answers.

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

## How studying works

Study is built on what learning research finds lasts, rather than what feels productive in the moment:

| Finding | What Study does |
| --- | --- |
| New material is learned best in small segments, prerequisites first ([segmenting and pre-training](https://doi.org/10.1017/CBO9781139547369.016)) | The paper is heard a section at a time (usually one to three minutes), and its ideas are taught right after. Background the paper assumes without explaining is taught before the section that needs it, when you may not know it. |
| Words and a picture together beat words alone ([dual coding, multimedia principle](https://doi.org/10.1017/CBO9781139547369.010)) | A lesson shows the paper's equation, figure or table it explains, when there is one. |
| Worked examples help novices more than solving from scratch ([worked-example effect](https://doi.org/10.3102/00346543070002181)) | Each lesson has a concrete example: a small worked example with numbers, a case from the paper, or an analogy (said to be one). |
| Retrieving from memory strengthens it more than rereading ([testing effect](https://doi.org/10.1111/j.1467-9280.2006.01693.x)) | Every idea is checked with a question after its lesson, and later reviews ask, never show. |
| Three options work as well as four or five ([Rodriguez 2005](https://doi.org/10.1111/j.1745-3992.2005.00006.x)) | Questions have three short options, which you can keep in mind when they are read to you. |
| Feedback helps most when it explains ([elaborated feedback](https://doi.org/10.3102/0034654307313795)) | Every option of a multiple-choice question says why it is right or wrong. The wrong options are plausible misconceptions, not throwaways, and the right one isn't given away by being the longest. |
| A lucky guess looks like knowing, and hides the gap | *I don't know* is always an option, and counts as not knowing. |
| Spaced practice beats massed practice, and mixing topics beats blocking them ([spacing](https://doi.org/10.1037/0033-2909.132.3.354), [interleaving](https://doi.org/10.1007/s11251-007-9015-8)) | An idea answered wrong comes back minutes later, between other ideas. After that it is reviewed on the FSRS schedule, mixed with other papers' ideas and cards. |
| The same question asked again and again can be answered from memory of the answer, without the idea | Each idea keeps a pool of questions (multiple choice and recall, from every paper it was met in). A review asks a different one each time, alternating between the two kinds. |
| Explaining in your own words deepens understanding ([generation, self-explanation](https://doi.org/10.1016/0364-0213%2889%2990002-5)) | At the end of each part you explain its ideas from memory, out loud or typed. The tutor says what you got right, what is missing, and what a good answer covers. |
| People judge poorly what they know ([illusions of competence](https://doi.org/10.1146/annurev-psych-113011-143823)) | Nothing is skipped on your word: skipping a lesson asks its question first, and ideas known from other papers are skipped only while FSRS says they are still well remembered. |

A session:

1. **Plan** (once per paper, about 20–40 s, while the paper's beginning is read to you). GLM 5.3 reads the whole paper
   (the same prefix as Ask, so the API's cache serves both) and lists the ideas to learn in the paper's order, each
   with the section that covers it, a goal, a one-sentence definition, its equations and figures, and the ideas it
   builds on. It is also given the ideas you already know from other papers. An idea that *is* one of them is marked
   `same as known`, and one that only builds on one is marked `uses known`. Each `same as known` claim is checked by a
   second, quick call, and dropped when in doubt. A wrong match would skip something you never learned.
2. **Listen**. The paper's narration plays as usual, up to the end of the section that covers the next ideas. A strip
   under the paper says what the tutor will go over after it. Jumping elsewhere in the paper is fine: the tutor comes
   in at the end of the stretch you're hearing, and still covers what you skipped.
3. **Teach**. The tutor's lesson is spoken (Voxtral, in the tutor's voice: another speaker than the paper's, chosen
   when you first study and changeable in Settings), with its equation or figure on screen and
   the sentence being said under it. It connects to what you know ("You already know that…"), explains what the section
   meant rather than retelling it, and ends with an example. Lessons are written two ahead while you listen, and their
   first words are spoken ahead, so the tutor starts at once. *Know it? Skip to the question* asks the lesson's question
   first and teaches it only if you get it wrong.
4. **Check**. One multiple-choice question, read with its three numbered options. After a chime the microphone
   listens. Say "option 2", "two", "the second one", the answer in your own words, or "I don't know"; it stops
   listening when you stop talking. Or tap an option. Options are numbered, not lettered, because transcription mixes
   up lone letters ("B" came back empty, "Bee" as "a"). The tutor says what it heard ("You said option 2"), whether it
   is right and why, and goes on; *Misheard? Answer again* under the question takes a misheard answer back. Unheard or
   unclear answers get one reminder, then it waits for a tap. A question's options are mixed, but always in the same
   order for the same question, and a session left on a question comes back to that question.
5. **Recap** at the end of each part: explain it back out loud (or type it), and hear *Got it* / *Partly there* /
   *Not yet* with feedback. Saying nothing skips it.
6. **Known ideas**. If you studied an idea in another paper and FSRS says you still know it, it is skipped. If it is
   fading, one quick question checks it, and that counts as its review. If you've forgotten it, it is taught here.

**Ask**, in a session, always goes to the tutor, while the paper is read too. You can type and, with the microphone
button, ask out loud (what you say joins what you typed), or tap a question. The tutor is given the conversation so far
as a conversation, what it said last (the lesson, the question and its options, the feedback on your answer, even when
the session has moved on) and, during the paper, the sentences just heard, so "why is that the answer?" means the last
question, and a follow-up builds on the answer instead of repeating it. Its answer is spoken; one button pauses it or
goes back to where you were (*Continue the paper*, *Back to the question*, *Continue with the tutor*).

The buttons are the same everywhere, and time runs one way through the session: Back 15 s and Skip 15 s move through
what the tutor is saying as through the paper. Back from the start of a question goes to the end of its lesson, and
back from the start of a lesson goes to the end of the paper heard before it (the tutor comes in again after it).
Skip past the end of a lesson goes to its question. Pause stops the tutor, the microphone and the narration; Continue
goes on from the same word. The headset's play/pause button does the same. On Android the phone can be locked in a pocket: once the microphone is allowed, the playback
service also runs as a microphone service, so the tutor hears answers with the screen off. The screen stays on while
the tutor talks, for the equation; lock it if you don't need it. Answering aloud can be turned off (Settings: *Answer out loud*): the tutor then waits for a tap.
What the tutor says is cached per voice in the paper's `study/audio/` folder, so nothing is spoken twice by the API.

Every answer is an FSRS review of the idea (right is Good; wrong or *I don't know* is Again), with the same scheduler
and retention setting as the cards (see above), so what you learn comes back for review just before you'd forget it.
Ideas are kept across papers in `knowledge.json` in the data folder, with up to 12 questions each. A paper's plan, its
lessons and how far you got are in the paper's `study/` folder. *Forget* on the Learn screen removes an idea, so it is
taught again. *This question is wrong* takes an answer back and never asks that question again.

The tutor is told to use only what the paper says, and to say so when it adds outside background. Every question
and answer is written from the paper, so if the paper is wrong, so is the lesson.

## Desktop analysis tool

```bash
dotnet run --project tools/ScriptDump -- paper.pdf --lines            # line classes
dotnet run --project tools/ScriptDump -- paper.pdf --crops /tmp/crops # equation images (needs pdftoppm, ImageMagick)
MISTRAL_API_KEY=... dotnet run --project tools/ScriptDump -- paper.pdf --ocr --crops /tmp/crops --mistral mistral-medium-latest

# Study prompts against a data folder (a copy of ~/.local/share/PaperReader), with the key from the app's settings
dotnet run --project tools/ScriptDump -- --study <data dir> <paper id> plan                # the plan, saved to study/plan.txt
dotnet run --project tools/ScriptDump -- --study <data dir> <paper id> lesson <idea>       # e.g. lesson c3
dotnet run --project tools/ScriptDump -- --study <data dir> <paper id> tutor <idea> "<question>"
dotnet run --project tools/ScriptDump -- --study <data dir> <paper id> recap <part> "<answer>"
dotnet run --project tools/ScriptDump -- --study <data dir> <paper id> match              # re-match with what you know
```

## Limitations

- Scanned (image-only) PDFs are not supported.
- Without a Mistral key there are no figures or tables, and figure labels and table cells sometimes reach
  the offline narration.
- Papers narrated before figure support get their figures on the next open, shown when referenced, but
  their captions are only read aloud after removing and adding the paper again.
- Occasionally a line that isn't an equation (a table header, a sentence full of symbols) is treated as one.
- Changing the narration model does not re-narrate papers already added (remove and add them again).
- Ask and Study need a Mistral key and a connection. Reviews work offline.
- Answering aloud needs a microphone and some quiet: in wind or traffic it may not hear the end of an answer (it stops
  after 12 seconds) or may hear noise as speech (then it asks again). Tapping always works.
- On Android 11 and later, the microphone works with the screen locked only if the session was started or
  resumed with the app on screen: resuming from the headset or the lock screen after a long pause plays the tutor, but
  its questions then wait for a tap.
