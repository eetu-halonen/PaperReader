using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace PaperReader.Browser;

/// <summary>
/// Functions of wwwroot/interop.js. Byte arrays cross in two steps: a promise resolves to a buffer id,
/// then <see cref="Take"/> copies that buffer into .NET memory; the other way, <see cref="Stage"/> copies
/// bytes into a JavaScript buffer and returns its id.
/// </summary>
[SupportedOSPlatform("browser")]
public static partial class Js
{
    public const string Module = "paperreader";

    // ---- buffers

    [JSImport("stage", Module)]
    public static partial int Stage([JSMarshalAs<JSType.MemoryView>] Span<byte> bytes);

    [JSImport("length", Module)]
    public static partial int Length(int id);

    /// <summary>Copies a buffer into <paramref name="dest"/> and frees it.</summary>
    [JSImport("take", Module)]
    public static partial void Take(int id, [JSMarshalAs<JSType.MemoryView>] Span<byte> dest);

    [JSImport("free", Module)]
    public static partial void Free(int id);

    // ---- IndexedDB, holding the data folder between visits

    /// <summary>Every stored path, one per line.</summary>
    [JSImport("storeList", Module)]
    public static partial Task<string> StoreList();

    /// <summary>Resolves to a buffer id with the file's bytes (-1 when missing).</summary>
    [JSImport("storeGet", Module)]
    public static partial Task<int> StoreGet(string path);

    [JSImport("storePut", Module)]
    public static partial Task StorePut(string path, int buffer);

    [JSImport("storeDelete", Module)]
    public static partial Task StoreDelete(string path);

    /// <summary>Deletes every stored path starting with the prefix.</summary>
    [JSImport("storeDeletePrefix", Module)]
    public static partial Task StoreDeletePrefix(string prefix);

    [JSImport("every", Module)]
    public static partial void Every(int milliseconds, [JSMarshalAs<JSType.Function>] Action handler);

    [JSImport("onHidden", Module)]
    public static partial void OnHidden([JSMarshalAs<JSType.Function>] Action handler);

    // ---- audio

    [JSImport("audioPlay", Module)]
    public static partial void AudioPlay(int buffer, double startSeconds, double rate,
        [JSMarshalAs<JSType.Function>] Action ended,
        [JSMarshalAs<JSType.Function<JSType.String>>] Action<string> failed);

    [JSImport("audioStop", Module)]
    public static partial void AudioStop();

    [JSImport("audioPosition", Module)]
    public static partial double AudioPosition();

    [JSImport("audioRate", Module)]
    public static partial void AudioRate(double rate);

    // ---- PDF pages (pdf.js)

    /// <summary>Opens a PDF from a staged buffer; resolves to a document handle.</summary>
    [JSImport("pdfOpen", Module)]
    public static partial Task<int> PdfOpen(int buffer);

    /// <summary>Renders a page region (points) at a scale; resolves to a buffer id of RGBA pixels, width * height * 4.</summary>
    [JSImport("pdfRender", Module)]
    public static partial Task<int> PdfRender(int doc, int page, double x, double y, double w, double h, double scale);

    [JSImport("pdfClose", Module)]
    public static partial void PdfClose(int doc);

    // ---- microphone

    [JSImport("recStart", Module)]
    public static partial Task RecStart();

    /// <summary>Stops recording; resolves to a buffer id with the audio.</summary>
    [JSImport("recStop", Module)]
    public static partial Task<int> RecStop();

    /// <summary>File extension of the recording's format (webm, ogg, m4a).</summary>
    [JSImport("recExtension", Module)]
    public static partial string RecExtension();

    [JSImport("recCancel", Module)]
    public static partial void RecCancel();

    /// <summary>How loud the microphone is now, 0 to 1 (-1 if it can't be measured).</summary>
    [JSImport("recLevel", Module)]
    public static partial double RecLevel();

    // ---- media session and screen

    [JSImport("setPlayback", Module)]
    public static partial void SetPlayback(string title, string detail, bool playing);

    [JSImport("endPlayback", Module)]
    public static partial void EndPlayback();

    [JSImport("setRemote", Module)]
    public static partial void SetRemote([JSMarshalAs<JSType.Function<JSType.Boolean>>] Action<bool> handler);

    [JSImport("keepAwake", Module)]
    public static partial void KeepAwake(bool on);
}
