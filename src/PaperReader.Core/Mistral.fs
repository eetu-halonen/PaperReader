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
