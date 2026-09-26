/// Mistral AI HTTP API: Voxtral text-to-speech, preset voices, and chat completions.
module PaperReader.Core.Mistral

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks

let baseUrl = "https://api.mistral.ai"
let ttsModel = "voxtral-mini-tts-latest"

exception MistralError of status: int * message: string

let private http =
    let h = new HttpClient(Timeout = TimeSpan.FromMinutes 4.0)
    h.DefaultRequestHeaders.UserAgent.ParseAdd "PaperReader/1.0"
    h

let private describe (status: HttpStatusCode) (body: string) =
    let detail =
        try
            use d = JsonDocument.Parse body
            let root = d.RootElement
            match root.TryGetProperty "message" with
            | true, m when m.ValueKind = JsonValueKind.String -> m.GetString()
            | _ ->
                match root.TryGetProperty "detail" with
                | true, m -> m.ToString()
                | _ -> body
        with _ -> body
    let detail = if detail.Length > 300 then detail.Substring(0, 300) else detail
    match int status with
    | 401 -> "The Mistral API key was rejected (401). Check it in Settings."
    | 402 -> "Mistral says the account has no credit left (402)."
    | 429 -> "Mistral rate limit reached (429). Try again in a moment."
    | s -> sprintf "Mistral error %d: %s" s detail

/// Sends a request, retrying rate limits and server errors with backoff.
let private send (key: string) (mk: unit -> HttpRequestMessage) (ct: CancellationToken) : Task<string> =
    task {
        let mutable attempt = 0
        let mutable result = None
        while result.IsNone do
            use req = mk ()
            req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", key.Trim())
            let! resp =
                task {
                    try
                        let! r = http.SendAsync(req, ct)
                        return Choice1Of2 r
                    with
                    | :? HttpRequestException as e when attempt < 3 -> return Choice2Of2 e.Message
                    | :? TaskCanceledException when not ct.IsCancellationRequested && attempt < 2 -> return Choice2Of2 "timeout"
                }
            match resp with
            | Choice2Of2 _ ->
                attempt <- attempt + 1
                do! Task.Delay(1500 * attempt, ct)
            | Choice1Of2 resp ->
                use resp = resp
                let! body = resp.Content.ReadAsStringAsync(ct)
                let status = int resp.StatusCode
                if resp.IsSuccessStatusCode then result <- Some body
                elif (status = 429 || status >= 500) && attempt < 5 then
                    attempt <- attempt + 1
                    let wait =
                        match resp.Headers.RetryAfter with
                        | null -> TimeSpan.FromSeconds(2.0 * float attempt)
                        | ra when ra.Delta.HasValue -> ra.Delta.Value
                        | _ -> TimeSpan.FromSeconds(2.0 * float attempt)
                    do! Task.Delay(wait, ct)
                else raise (MistralError(status, describe resp.StatusCode body))
        return result.Value
    }

let private jsonContent (node: JsonNode) =
    new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json")

type Voice = { Id: string; Name: string; Languages: string list }

/// Preset voices, English first.
let listVoices (key: string) (ct: CancellationToken) : Task<Voice list> =
    task {
        let! body = send key (fun () -> new HttpRequestMessage(HttpMethod.Get, baseUrl + "/v1/audio/voices?type=preset&limit=100")) ct
        use d = JsonDocument.Parse body
        let voices =
            [ for v in d.RootElement.GetProperty("items").EnumerateArray() do
                  let str (p: string) =
                      match v.TryGetProperty p with
                      | true, x when x.ValueKind = JsonValueKind.String -> x.GetString()
                      | _ -> ""
                  let langs =
                      match v.TryGetProperty "languages" with
                      | true, x when x.ValueKind = JsonValueKind.Array -> [ for l in x.EnumerateArray() -> l.GetString() ]
                      | _ -> []
                  let slug = str "slug"
                  { Id = (if slug <> "" then slug else str "id"); Name = str "name"; Languages = langs } ]
        let english (v: Voice) = v.Languages |> List.exists (fun l -> l.StartsWith "en")
        return voices |> List.sortBy (fun v -> (if english v then 0 else 1), v.Name)
    }

/// Synthesizes speech with Voxtral; returns WAV bytes.
let speech (key: string) (voiceId: string) (text: string) (ct: CancellationToken) : Task<byte[]> =
    task {
        let payload =
            JsonObject(
                [ Collections.Generic.KeyValuePair("model", JsonValue.Create ttsModel :> JsonNode)
                  Collections.Generic.KeyValuePair("input", JsonValue.Create text :> JsonNode)
                  Collections.Generic.KeyValuePair("voice_id", JsonValue.Create voiceId :> JsonNode)
                  Collections.Generic.KeyValuePair("response_format", JsonValue.Create "wav" :> JsonNode) ])
        let! body =
            send key (fun () -> new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/audio/speech", Content = jsonContent payload)) ct
        use d = JsonDocument.Parse body
        return Convert.FromBase64String(d.RootElement.GetProperty("audio_data").GetString())
    }

/// A part of a chat message: text, or a PNG image.
type Part =
    | Text of string
    | Png of byte[]

/// Chat completion in JSON mode. Returns the assistant message content.
let chatJson (key: string) (model: string) (system: string) (user: Part list) (ct: CancellationToken) : Task<string> =
    task {
        let content = JsonArray()
        for p in user do
            match p with
            | Text t ->
                content.Add(JsonObject([ Collections.Generic.KeyValuePair("type", JsonValue.Create "text" :> JsonNode)
                                         Collections.Generic.KeyValuePair("text", JsonValue.Create t :> JsonNode) ]))
            | Png bytes ->
                let url = "data:image/png;base64," + Convert.ToBase64String bytes
                content.Add(JsonObject([ Collections.Generic.KeyValuePair("type", JsonValue.Create "image_url" :> JsonNode)
                                         Collections.Generic.KeyValuePair("image_url", JsonValue.Create url :> JsonNode) ]))
        let msg (role: string) (c: JsonNode) =
            JsonObject([ Collections.Generic.KeyValuePair("role", JsonValue.Create role :> JsonNode)
                         Collections.Generic.KeyValuePair("content", c) ]) :> JsonNode
        let payload =
            JsonObject(
                [ Collections.Generic.KeyValuePair("model", JsonValue.Create model :> JsonNode)
                  Collections.Generic.KeyValuePair("temperature", JsonValue.Create 0.2 :> JsonNode)
                  Collections.Generic.KeyValuePair("response_format", JsonObject([ Collections.Generic.KeyValuePair("type", JsonValue.Create "json_object" :> JsonNode) ]) :> JsonNode)
                  Collections.Generic.KeyValuePair("messages", JsonArray([| msg "system" (JsonValue.Create system); msg "user" content |]) :> JsonNode) ])
        let! body =
            send key (fun () -> new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/chat/completions", Content = jsonContent payload)) ct
        use d = JsonDocument.Parse body
        let choice = d.RootElement.GetProperty("choices").[0]
        let c = choice.GetProperty("message").GetProperty("content")
        return
            match c.ValueKind with
            | JsonValueKind.String -> c.GetString()
            | JsonValueKind.Array ->
                // some models return content chunks; keep the text ones
                [ for part in c.EnumerateArray() do
                      match part.TryGetProperty "text" with
                      | true, t when t.ValueKind = JsonValueKind.String -> yield t.GetString()
                      | _ -> () ]
                |> String.concat ""
            | _ -> ""
    }

/// Chat completion streamed as it is written: `onText` gets the answer so far after every piece (reasoning is
/// left out). Returns the whole answer. `effort` is the reasoning effort ("low", "high") for models that take one.
let chatStream (key: string) (model: string) (messages: (string * string) list) (effort: string option)
               (onText: string -> unit) (ct: CancellationToken) : Task<string> =
    task {
        let kv (k: string) (v: JsonNode) = Collections.Generic.KeyValuePair(k, v)
        let msgs = JsonArray()
        for role, text in messages do
            msgs.Add(JsonObject([ kv "role" (JsonValue.Create role); kv "content" (JsonValue.Create text) ]))
        let payload =
            JsonObject(
                [ kv "model" (JsonValue.Create model)
                  kv "stream" (JsonValue.Create true)
                  kv "temperature" (JsonValue.Create 0.3)
                  // room for a deck of cards after the reasoning; answers stay short by instruction
                  kv "max_tokens" (JsonValue.Create 12000)
                  kv "messages" msgs ])
        effort |> Option.iter (fun e -> payload.["reasoning_effort"] <- JsonValue.Create e)
        let body = payload.ToJsonString()
        let mutable attempt = 0
        let mutable response: HttpResponseMessage = null
        while isNull response do
            let req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/chat/completions", Content = new StringContent(body, Encoding.UTF8, "application/json"))
            req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", key.Trim())
            // in the browser, responses are buffered whole unless streaming is asked for
            req.Options.Set(HttpRequestOptionsKey<bool>("WebAssemblyEnableStreamingResponse"), true)
            let! r = http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            let status = int r.StatusCode
            if r.IsSuccessStatusCode then response <- r
            elif (status = 429 || status >= 500) && attempt < 4 then
                attempt <- attempt + 1
                r.Dispose()
                do! Task.Delay(TimeSpan.FromSeconds(2.0 * float attempt), ct)
            else
                let! text = r.Content.ReadAsStringAsync(ct)
                r.Dispose()
                raise (MistralError(status, describe r.StatusCode text))
        use response = response
        use! stream = response.Content.ReadAsStreamAsync(ct)
        use reader = new IO.StreamReader(stream)
        let answer = StringBuilder()
        let mutable fin = false
        while not fin do
            let! line = reader.ReadLineAsync(ct).AsTask()
            if isNull line then fin <- true
            elif line.StartsWith "data:" then
                let data = line.Substring(5).Trim()
                if data = "[DONE]" then fin <- true
                elif data <> "" then
                    use d = JsonDocument.Parse data
                    match d.RootElement.TryGetProperty "choices" with
                    | true, choices when choices.GetArrayLength() > 0 ->
                        match choices.[0].TryGetProperty "delta" with
                        | true, delta ->
                            match delta.TryGetProperty "content" with
                            | true, c when c.ValueKind = JsonValueKind.String && c.GetString() <> "" ->
                                answer.Append(c.GetString()) |> ignore
                                onText (answer.ToString())
                            | true, c when c.ValueKind = JsonValueKind.Array ->
                                // chunks: "thinking" (skipped) and "text"
                                let mutable added = false
                                for part in c.EnumerateArray() do
                                    match part.TryGetProperty "type", part.TryGetProperty "text" with
                                    | (true, t), (true, x) when t.GetString() = "text" && x.ValueKind = JsonValueKind.String ->
                                        answer.Append(x.GetString()) |> ignore
                                        added <- true
                                    | _ -> ()
                                if added then onText (answer.ToString())
                            | _ -> ()
                        | _ -> ()
                    | _ -> ()
        return answer.ToString()
    }

/// Speech to text with Voxtral. `fileName`'s extension tells the format (wav, webm, m4a, ogg, mp3).
let transcribe (key: string) (audio: byte[]) (fileName: string) (ct: CancellationToken) : Task<string> =
    task {
        let! body =
            send key (fun () ->
                let form = new MultipartFormDataContent()
                form.Add(new StringContent("voxtral-mini-latest"), "model")
                form.Add(new ByteArrayContent(audio), "file", fileName)
                new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/audio/transcriptions", Content = form)) ct
        use d = JsonDocument.Parse body
        return
            match d.RootElement.TryGetProperty "text" with
            | true, t when t.ValueKind = JsonValueKind.String -> t.GetString().Trim()
            | _ -> ""
    }

/// A region Mistral OCR recognised, in pixels of its page image.
type OcrBlock = { X0: float; Y0: float; X1: float; Y1: float; Kind: string; Content: string }

/// One page of Mistral OCR output. Width and Height are the page image's size in pixels.
type OcrPage = { Index: int; Width: float; Height: float; Markdown: string; Blocks: OcrBlock list }

let ocrModel = "mistral-ocr-latest"

/// Mistral OCR on a PDF ("application/pdf") or an image ("image/png"); returns markdown with LaTeX math.
let ocr (key: string) (mime: string) (bytes: byte[]) (ct: CancellationToken) : Task<OcrPage list> =
    task {
        let uri = sprintf "data:%s;base64,%s" mime (Convert.ToBase64String bytes)
        let kind, field = if mime = "application/pdf" then "document_url", "document_url" else "image_url", "image_url"
        let doc =
            JsonObject([ Collections.Generic.KeyValuePair("type", JsonValue.Create kind :> JsonNode)
                         Collections.Generic.KeyValuePair(field, JsonValue.Create uri :> JsonNode) ])
        let payload =
            JsonObject([ Collections.Generic.KeyValuePair("model", JsonValue.Create ocrModel :> JsonNode)
                         Collections.Generic.KeyValuePair("document", doc :> JsonNode) ])
        let! body =
            send key (fun () -> new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/ocr", Content = jsonContent payload)) ct
        use d = JsonDocument.Parse body
        let num (e: JsonElement) (name: string) =
            match e.TryGetProperty name with
            | true, v when v.ValueKind = JsonValueKind.Number -> v.GetDouble()
            | _ -> 0.0
        let str (e: JsonElement) (name: string) =
            match e.TryGetProperty name with
            | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
            | _ -> ""
        return
            [ for p in d.RootElement.GetProperty("pages").EnumerateArray() do
                  let dims = match p.TryGetProperty "dimensions" with | true, x when x.ValueKind = JsonValueKind.Object -> Some x | _ -> None
                  let blocks =
                      match p.TryGetProperty "blocks" with
                      | true, bs when bs.ValueKind = JsonValueKind.Array ->
                          [ for b in bs.EnumerateArray() ->
                                { X0 = num b "top_left_x"; Y0 = num b "top_left_y"; X1 = num b "bottom_right_x"; Y1 = num b "bottom_right_y"
                                  Kind = str b "type"; Content = str b "content" } ]
                      | _ -> []
                  yield
                      { Index = int (num p "index")
                        Width = dims |> Option.map (fun x -> num x "width") |> Option.defaultValue 0.0
                        Height = dims |> Option.map (fun x -> num x "height") |> Option.defaultValue 0.0
                        Markdown = str p "markdown"
                        Blocks = blocks } ]
    }
