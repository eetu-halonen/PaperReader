// Browser services for Paper Reader, called from .NET (PaperReader.Browser.Interop/Js.cs).

// ---- byte buffers passed between .NET and JavaScript by id

const buffers = new Map();
let nextBuffer = 1;

function keep(bytes) {
    const id = nextBuffer++;
    buffers.set(id, bytes);
    return id;
}

function takeBuffer(id) {
    const bytes = buffers.get(id);
    buffers.delete(id);
    return bytes;
}

export function stage(view) { return keep(view.slice()); }
export function length(id) { return buffers.get(id).length; }
export function take(id, view) { view.set(takeBuffer(id)); }
export function free(id) { buffers.delete(id); }

// ---- IndexedDB: the data folder, kept between visits

let dbPromise = null;

function db() {
    if (!dbPromise) {
        dbPromise = new Promise((resolve, reject) => {
            const open = indexedDB.open("paperreader", 1);
            open.onupgradeneeded = () => open.result.createObjectStore("files");
            open.onsuccess = () => resolve(open.result);
            open.onerror = () => reject(open.error);
        });
    }
    return dbPromise;
}

async function request(mode, run) {
    const d = await db();
    return new Promise((resolve, reject) => {
        const tx = d.transaction("files", mode);
        const req = run(tx.objectStore("files"));
        tx.oncomplete = () => resolve(req ? req.result : undefined);
        tx.onerror = () => reject(tx.error);
        tx.onabort = () => reject(tx.error);
    });
}

export async function storeList() {
    const keys = await request("readonly", s => s.getAllKeys());
    return keys.join("\n");
}

export async function storeGet(path) {
    const value = await request("readonly", s => s.get(path));
    return value === undefined ? -1 : keep(new Uint8Array(value));
}

export async function storePut(path, id) {
    const bytes = takeBuffer(id);
    await request("readwrite", s => s.put(bytes, path));
}

export async function storeDelete(path) {
    await request("readwrite", s => s.delete(path));
}

export async function storeDeletePrefix(prefix) {
    await request("readwrite", s => s.delete(IDBKeyRange.bound(prefix, prefix + "￿")));
}

export function every(ms, handler) { setInterval(() => handler(), ms); }

export function onHidden(handler) {
    document.addEventListener("visibilitychange", () => { if (document.visibilityState === "hidden") handler(); });
    window.addEventListener("pagehide", () => handler());
}

// ---- audio: one WAV clip at a time

let audio = null;
let audioUrl = null;

export function audioStop() {
    if (audio) {
        audio.onended = null;
        audio.onerror = null;
        audio.pause();
        audio = null;
    }
    if (audioUrl) {
        URL.revokeObjectURL(audioUrl);
        audioUrl = null;
    }
}

export function audioPlay(id, startSeconds, rate, ended, failed) {
    audioStop();
    audioUrl = URL.createObjectURL(new Blob([takeBuffer(id)], { type: "audio/wav" }));
    const a = new Audio(audioUrl);
    audio = a;
    a.preservesPitch = true;
    a.playbackRate = rate;
    a.onended = () => { if (audio === a) ended(); };
    a.onerror = () => { if (audio === a) failed(a.error ? a.error.message || "can't play the clip" : "can't play the clip"); };
    const start = () => {
        if (startSeconds > 0) a.currentTime = startSeconds;
        a.play().catch(e => { if (audio === a) failed(String(e && e.message ? e.message : e)); });
    };
    if (startSeconds > 0) a.addEventListener("loadedmetadata", start, { once: true }); else start();
}

export function audioPosition() { return audio ? audio.currentTime : 0; }
export function audioRate(rate) { if (audio) audio.playbackRate = rate; }

// ---- PDF pages with pdf.js

let pdfjs = null;
const docs = new Map();
let nextDoc = 1;

async function loadPdfJs() {
    if (!pdfjs) {
        pdfjs = await import(new URL("./pdfjs/pdf.min.mjs", import.meta.url).href);
        pdfjs.GlobalWorkerOptions.workerSrc = new URL("./pdfjs/pdf.worker.min.mjs", import.meta.url).href;
    }
    return pdfjs;
}

export async function pdfOpen(id) {
    const lib = await loadPdfJs();
    const task = lib.getDocument({ data: takeBuffer(id) });
    const doc = await task.promise;
    const handle = nextDoc++;
    docs.set(handle, { task, doc });
    return handle;
}

export async function pdfRender(handle, pageIndex, x, y, w, h, scale) {
    const { doc } = docs.get(handle);
    const page = await doc.getPage(pageIndex + 1);
    const width = Math.max(1, Math.floor(w * scale));
    const height = Math.max(1, Math.floor(h * scale));
    const canvas = new OffscreenCanvas(width, height);
    const ctx = canvas.getContext("2d");
    ctx.fillStyle = "#ffffff";
    ctx.fillRect(0, 0, width, height);
    // the region's top-left corner becomes the canvas origin
    const viewport = page.getViewport({ scale, offsetX: -x * scale, offsetY: -y * scale });
    await page.render({ canvasContext: ctx, viewport, background: "rgba(255,255,255,1)" }).promise;
    page.cleanup();
    const data = ctx.getImageData(0, 0, width, height).data;
    return keep(new Uint8Array(data.buffer, data.byteOffset, data.byteLength));
}

export function pdfClose(handle) {
    const open = docs.get(handle);
    docs.delete(handle);
    // the loading task owns the document and its worker
    if (open) Promise.resolve(open.task.destroy()).catch(() => { });
}

// ---- microphone

let recorder = null;
let recorded = [];
let recStream = null;

function recordingType() {
    const types = ["audio/webm;codecs=opus", "audio/ogg;codecs=opus", "audio/mp4", "audio/webm"];
    return types.find(t => window.MediaRecorder && MediaRecorder.isTypeSupported(t)) || "";
}

export function recExtension() {
    const t = recorder ? recorder.mimeType : recordingType();
    if (t.startsWith("audio/ogg")) return "ogg";
    if (t.startsWith("audio/mp4")) return "m4a";
    return "webm";
}

function stopStream() {
    if (recStream) recStream.getTracks().forEach(t => t.stop());
    recStream = null;
}

export async function recStart() {
    recCancel();
    recStream = await navigator.mediaDevices.getUserMedia({ audio: true });
    const type = recordingType();
    recorder = new MediaRecorder(recStream, type ? { mimeType: type } : undefined);
    recorded = [];
    recorder.ondataavailable = e => { if (e.data && e.data.size > 0) recorded.push(e.data); };
    recorder.start();
}

export function recStop() {
    return new Promise((resolve, reject) => {
        if (!recorder) { reject(new Error("not recording")); return; }
        const r = recorder;
        r.onstop = async () => {
            stopStream();
            const blob = new Blob(recorded, { type: r.mimeType });
            resolve(keep(new Uint8Array(await blob.arrayBuffer())));
        };
        r.stop();
    });
}

export function recCancel() {
    if (recorder && recorder.state !== "inactive") {
        recorder.onstop = null;
        recorder.stop();
    }
    recorder = null;
    stopStream();
}

// ---- media session (lock screen and headset keys) and keeping the screen on

let remote = null;

export function setRemote(handler) {
    remote = handler;
    if ("mediaSession" in navigator) {
        navigator.mediaSession.setActionHandler("play", () => remote && remote(true));
        navigator.mediaSession.setActionHandler("pause", () => remote && remote(false));
    }
}

export function setPlayback(title, detail, playing) {
    if ("mediaSession" in navigator) {
        navigator.mediaSession.metadata = new MediaMetadata({ title, artist: detail, album: "Paper Reader" });
        navigator.mediaSession.playbackState = playing ? "playing" : "paused";
    }
    document.title = playing ? `▶ ${title}` : "Paper Reader";
}

export function endPlayback() {
    if ("mediaSession" in navigator) {
        navigator.mediaSession.metadata = null;
        navigator.mediaSession.playbackState = "none";
    }
    document.title = "Paper Reader";
}

let wakeLock = null;

export function keepAwake(on) {
    if (on && !wakeLock && navigator.wakeLock) {
        navigator.wakeLock.request("screen").then(l => { wakeLock = l; l.onrelease = () => { wakeLock = null; }; }).catch(() => { });
    } else if (!on && wakeLock) {
        wakeLock.release().catch(() => { });
        wakeLock = null;
    }
}
