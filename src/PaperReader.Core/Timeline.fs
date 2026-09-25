/// Moving through a paper whose audio is split into per-segment clips.
module PaperReader.Core.Timeline

/// A position: segment index and offset into its clip.
type Position = { Segment: int; OffsetMs: int }

/// Goes back `amountMs` of audio, crossing into earlier clips. Clips whose duration is unknown
/// (not synthesized yet) stop the walk at their start.
let back (duration: int -> int option) (amountMs: int) (p: Position) : Position =
    let rec walk seg remaining =
        if seg < 0 then { Segment = 0; OffsetMs = 0 }
        else
            match duration seg with
            | Some d when d >= remaining -> { Segment = seg; OffsetMs = d - remaining }
            | Some d -> walk (seg - 1) (remaining - d)
            | None -> { Segment = seg; OffsetMs = 0 }
    if p.OffsetMs >= amountMs then { p with OffsetMs = p.OffsetMs - amountMs }
    else walk (p.Segment - 1) (amountMs - p.OffsetMs)

/// Goes forward `amountMs`, crossing into later clips. Unknown clips are entered at their start.
let forward (duration: int -> int option) (count: int) (amountMs: int) (p: Position) : Position =
    let rec walk seg offset remaining =
        if seg >= count then { Segment = count - 1; OffsetMs = max 0 (defaultArg (duration (count - 1)) 0 - 250) }
        else
            match duration seg with
            | Some d when d - offset > remaining -> { Segment = seg; OffsetMs = offset + remaining }
            | Some d -> walk (seg + 1) 0 (remaining - (d - offset))
            | None -> { Segment = seg; OffsetMs = 0 }
    walk p.Segment p.OffsetMs amountMs

/// Rough speaking time for text that has no audio yet (about 15 characters per second).
let estimateMs (text: string) = 400 + text.Length * 1000 / 15
