/// Minimal WAV handling: normalise engine output to 16-bit PCM, append pauses, read durations.
module PaperReader.Core.Wav

open System
open System.IO

type Format = { Channels: int; SampleRate: int; BitsPerSample: int; Encoding: int }

/// Parses a RIFF/WAVE byte array into its format and PCM payload.
/// Tolerates streaming headers with bogus sizes by reading the data chunk to the end of the buffer.
let parse (bytes: byte[]) : Format * ArraySegment<byte> =
    if bytes.Length < 12 || Text.Encoding.ASCII.GetString(bytes, 0, 4) <> "RIFF" || Text.Encoding.ASCII.GetString(bytes, 8, 4) <> "WAVE" then
        failwith "not a WAV file"
    let mutable pos = 12
    let mutable format = None
    let mutable data = None
    while data.IsNone && pos + 8 <= bytes.Length do
        let id = Text.Encoding.ASCII.GetString(bytes, pos, 4)
        let size = BitConverter.ToUInt32(bytes, pos + 4)
        let body = pos + 8
        match id with
        | "fmt " ->
            let enc = int (BitConverter.ToUInt16(bytes, body))
            let enc =
                // WAVE_FORMAT_EXTENSIBLE: the real format is the first 2 bytes of the sub-format GUID
                if enc = 0xFFFE && size >= 26u then int (BitConverter.ToUInt16(bytes, body + 24)) else enc
            format <-
                Some { Encoding = enc
                       Channels = int (BitConverter.ToUInt16(bytes, body + 2))
                       SampleRate = BitConverter.ToInt32(bytes, body + 4)
                       BitsPerSample = int (BitConverter.ToUInt16(bytes, body + 14)) }
        | "data" ->
            let available = bytes.Length - body
            let len = if size = 0u || size = UInt32.MaxValue || int64 size > int64 available then available else int size
            data <- Some(ArraySegment(bytes, body, len))
        | _ -> ()
        pos <- body + int (min size (uint32 (bytes.Length - body))) + int (size % 2u)
    match format, data with
    | Some f, Some d -> f, d
    | _ -> failwith "WAV file has no fmt or data chunk"

/// Converts float32/24-bit/32-bit/8-bit PCM to 16-bit little-endian PCM.
let private to16Bit (f: Format) (data: ArraySegment<byte>) : byte[] =
    match f.Encoding, f.BitsPerSample with
    | 1, 16 -> data.ToArray()
    | 3, 32 ->
        let n = data.Count / 4
        let out = Array.zeroCreate<byte> (n * 2)
        for i in 0 .. n - 1 do
            let v = BitConverter.ToSingle(data.Array, data.Offset + i * 4)
            let s = int16 (Math.Clamp(float v, -1.0, 1.0) * 32767.0)
            BitConverter.TryWriteBytes(Span<byte>(out, i * 2, 2), s) |> ignore
        out
    | 1, 24 ->
        let n = data.Count / 3
        let out = Array.zeroCreate<byte> (n * 2)
        for i in 0 .. n - 1 do
            out.[i * 2] <- data.Array.[data.Offset + i * 3 + 1]
            out.[i * 2 + 1] <- data.Array.[data.Offset + i * 3 + 2]
        out
    | 1, 32 ->
        let n = data.Count / 4
        let out = Array.zeroCreate<byte> (n * 2)
        for i in 0 .. n - 1 do
            out.[i * 2] <- data.Array.[data.Offset + i * 4 + 2]
            out.[i * 2 + 1] <- data.Array.[data.Offset + i * 4 + 3]
        out
    | 1, 8 ->
        data.ToArray() |> Array.collect (fun b -> BitConverter.GetBytes(int16 ((int b - 128) <<< 8)))
    | e, b -> failwithf "unsupported WAV encoding %d/%d bits" e b

let private header (channels: int) (rate: int) (dataLen: int) =
    use ms = new MemoryStream(44)
    use w = new BinaryWriter(ms)
    w.Write(Text.Encoding.ASCII.GetBytes "RIFF")
    w.Write(36 + dataLen)
    w.Write(Text.Encoding.ASCII.GetBytes "WAVE")
    w.Write(Text.Encoding.ASCII.GetBytes "fmt ")
    w.Write 16
    w.Write 1s
    w.Write(int16 channels)
    w.Write rate
    w.Write(rate * channels * 2)
    w.Write(int16 (channels * 2))
    w.Write 16s
    w.Write(Text.Encoding.ASCII.GetBytes "data")
    w.Write dataLen
    w.Flush()
    ms.ToArray()

/// Writes a clean 16-bit PCM WAV with `pauseMs` of silence appended. Returns the duration in ms.
/// Writes to a temp file first so an interrupted write never leaves a broken cache entry.
let normalizeTo (source: byte[]) (pauseMs: int) (destination: string) : int =
    let f, data = parse source
    let pcm = to16Bit f data
    let frameBytes = 2 * max 1 f.Channels
    let silence = (f.SampleRate * pauseMs / 1000) * frameBytes
    let total = pcm.Length - pcm.Length % frameBytes + silence
    let tmp = destination + ".tmp"
    do
        use out = File.Create tmp
        out.Write(header f.Channels f.SampleRate total)
        out.Write(pcm, 0, pcm.Length - pcm.Length % frameBytes)
        out.Write(Array.zeroCreate<byte> silence)
    File.Move(tmp, destination, true)
    int (int64 total * 1000L / int64 (f.SampleRate * frameBytes))

/// Duration of a WAV file written by `normalizeTo`, in ms.
let durationMs (path: string) : int =
    use fs = File.OpenRead path
    let head = Array.zeroCreate<byte> 44
    fs.ReadExactly(head, 0, 44)
    let rate = BitConverter.ToInt32(head, 24)
    let byteRate = BitConverter.ToInt32(head, 28)
    if rate <= 0 || byteRate <= 0 then 0
    else int ((fs.Length - 44L) * 1000L / int64 byteRate)
