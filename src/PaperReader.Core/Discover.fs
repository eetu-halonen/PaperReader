/// Finding papers to listen to: search over OpenAlex (about 270 million works; only those with a free PDF
/// are shown), recommendations from Semantic Scholar based on the papers in the library, and downloading
/// the PDF. Both services are free and need no key; all three allow calls from a web page (CORS).
module PaperReader.Core.Discover

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

let openAlexUrl = "https://api.openalex.org"
let semanticScholarUrl = "https://api.semanticscholar.org"

exception DiscoverError of message: string

/// A paper found by a search or recommended.
type Found =
    { /// Unique within one list: the OpenAlex id ("W…") or "S2:" and the Semantic Scholar id.
      Key: string
      Title: string
      Authors: string list
      Year: int option
      Venue: string option
      Abstract: string option
      Citations: int
      /// Addresses to try for the PDF, most likely to work first.
      Pdfs: string list
      /// The paper's web page, for downloading it by hand when no PDF address works.
      Page: string option
      Doi: string option
      Arxiv: string option
      OpenAlex: string option }

/// One page of search results.
type Results =
    { Items: Found list
      /// How many papers match in all.
      Total: int }

/// Where a library paper came from: known when it was downloaded here, otherwise looked up by its title.
type Source =
    { Doi: string option
      Arxiv: string option
      OpenAlex: string option }

let private http =
    let h = new HttpClient(Timeout = TimeSpan.FromSeconds 90.0)
    h.DefaultRequestHeaders.UserAgent.ParseAdd "PaperReader/1.0 (+https://github.com; paper-reader app)"
    h

// ---------------------------------------------------------------------------------------------
// Identifiers and text
// ---------------------------------------------------------------------------------------------

let private arxivIdRx = Regex(@"(?:arxiv\.org/(?:abs|pdf)/|arxiv[:.]\s*)?(\d{4}\.\d{4,5}|[a-z\-]+(?:\.[A-Z]{2})?/\d{7})(?:v\d+)?(?:\.pdf)?", RegexOptions.IgnoreCase)
let private doiRx = Regex(@"\b(10\.\d{4,9}/[^\s""<>]+)", RegexOptions.IgnoreCase)

/// The arXiv id in an arXiv address or DOI ("https://arxiv.org/abs/2006.11239v2", "10.48550/arXiv.2006.11239").
let arxivOf (text: string) : string option =
    if isNull text then None
    else
        let t = text.Trim()
        let lower = t.ToLowerInvariant()
        if lower.Contains "arxiv" then
            let m = arxivIdRx.Match t
            if m.Success then Some m.Groups.[1].Value else None
        else None

/// The DOI in a DOI address or text, without the "https://doi.org/" part.
let doiOf (text: string) : string option =
    if isNull text then None
    else
        let m = doiRx.Match text
        if m.Success then Some(m.Groups.[1].Value.TrimEnd('.', ',', ';', ')')) else None

/// What a typed query is: an arXiv id or address, a DOI, a web address (of a PDF, a web page or any other
/// document), or words to search for.
[<RequireQualifiedAccess>]
type Query =
    | Arxiv of string
    | Doi of string
    | Url of string
    | Words of string

let parseQuery (text: string) : Query =
    let t = text.Trim()
    let bareArxiv = Regex.IsMatch(t, @"^(arxiv:\s*)?\d{4}\.\d{4,5}(v\d+)?$", RegexOptions.IgnoreCase)
    let isUrl = t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
    match arxivOf t with
    | Some id when bareArxiv || isUrl || t.ToLowerInvariant().StartsWith "arxiv" -> Query.Arxiv id
    | _ when bareArxiv -> Query.Arxiv(Regex.Replace(t, @"^arxiv:\s*|v\d+$", "", RegexOptions.IgnoreCase))
    | _ ->
        match doiOf t with
        | Some d when not (t.Contains ' ') -> Query.Doi d
        | _ when isUrl && not (t.Contains ' ') -> Query.Url t
        | _ -> Query.Words t

/// Lower-case letters and digits only, for comparing titles.
let normalizeTitle (title: string) =
    if isNull title then ""
    else
        let sb = StringBuilder()
        for c in title.Normalize(NormalizationForm.FormD) do
            if Char.IsLetterOrDigit c then sb.Append(Char.ToLowerInvariant c) |> ignore
        sb.ToString()

/// Word overlap between two titles (0 to 1), to accept a looked-up paper only when it is the same one.
let titleSimilarity (a: string) (b: string) =
    let words (s: string) =
        Regex.Split(s.ToLowerInvariant(), @"[^\p{L}\p{N}]+") |> Array.filter (fun w -> w.Length > 1) |> Set.ofArray
    let wa, wb = words a, words b
    if wa.IsEmpty || wb.IsEmpty then 0.0
    else float (Set.intersect wa wb).Count / float (Set.union wa wb).Count

/// OpenAlex keeps abstracts as word -> positions; this puts the words back in order.
let abstractFromIndex (index: JsonElement) : string option =
    if index.ValueKind <> JsonValueKind.Object then None
    else
        let words = Collections.Generic.List<int * string>()
        for p in index.EnumerateObject() do
            if p.Value.ValueKind = JsonValueKind.Array then
                for pos in p.Value.EnumerateArray() do
                    if pos.ValueKind = JsonValueKind.Number then words.Add((pos.GetInt32(), p.Name))
        if words.Count = 0 then None
        else
            let text = words |> Seq.sortBy fst |> Seq.map snd |> String.concat " "
            // many start with the heading itself
            Some(Regex.Replace(text, @"^(abstract|summary)\b[\s.:—-]*", "", RegexOptions.IgnoreCase))

/// PDF addresses in the order to try: arXiv and the preprint servers answer with the file itself, many
/// publishers answer an app with a web page or a refusal, and PubMed Central links are web pages.
let orderPdfs (urls: string list) : string list =
    let rank (u: string) =
        let l = u.ToLowerInvariant()
        if l.Contains "arxiv.org/" then 0
        elif l.Contains "biorxiv.org" || l.Contains "medrxiv.org" || l.Contains "chemrxiv" || l.Contains "ssrn.com" then 1
        elif l.Contains "ncbi.nlm.nih.gov" || l.Contains "europepmc.org" then 4
        elif l.EndsWith ".pdf" || l.Contains "/pdf" || l.Contains "download" then 2
        else 3
    urls
    |> List.filter (fun u -> not (String.IsNullOrWhiteSpace u))
    |> List.map (fun u ->
        // arXiv abstract pages have the PDF next to them
        match arxivOf u with
        | Some id when u.ToLowerInvariant().Contains "arxiv.org/abs/" -> "https://arxiv.org/pdf/" + id
        | _ -> u)
    |> List.distinct
    |> List.sortBy rank

let private host (url: string) =
    match Uri.TryCreate(url, UriKind.Absolute) with
    | true, u -> u.Host.Replace("www.", "")
    | _ -> url

/// "arxiv.org", for telling the listener where the PDF comes from.
let pdfHost (f: Found) = f.Pdfs |> List.tryHead |> Option.map host

// ---------------------------------------------------------------------------------------------
// JSON helpers
// ---------------------------------------------------------------------------------------------

let private prop (e: JsonElement) (name: string) =
    if e.ValueKind <> JsonValueKind.Object then None
    else
        match e.TryGetProperty name with
        | true, v when v.ValueKind <> JsonValueKind.Null && v.ValueKind <> JsonValueKind.Undefined -> Some v
        | _ -> None

let private str (e: JsonElement) (name: string) =
    match prop e name with
    | Some v when v.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(v.GetString())) -> Some(v.GetString())
    | _ -> None

let private int' (e: JsonElement) (name: string) =
    match prop e name with
    | Some v when v.ValueKind = JsonValueKind.Number -> Some(v.GetInt32())
    | _ -> None

let private items (e: JsonElement) (name: string) =
    match prop e name with
    | Some v when v.ValueKind = JsonValueKind.Array -> v.EnumerateArray() |> List.ofSeq
    | _ -> []

let private cleanTitle (t: string) =
    // some titles carry HTML markup (<i>, <sub>) and line breaks
    Regex.Replace(Regex.Replace(t, "<[^>]+>", ""), @"\s+", " ").Trim()

// ---------------------------------------------------------------------------------------------
// OpenAlex
// ---------------------------------------------------------------------------------------------

let private workFields =
    "id,doi,title,publication_year,authorships,primary_location,best_oa_location,locations,cited_by_count,abstract_inverted_index,ids"

/// A work from OpenAlex, or None when it has no free PDF address.
let parseWork (w: JsonElement) : Found option =
    let title = str w "title" |> Option.map cleanTitle
    let doi = str w "doi" |> Option.bind doiOf
    let locations = items w "locations"
    let best = prop w "best_oa_location"
    let pdfUrls =
        [ match best with Some b -> yield! Option.toList (str b "pdf_url") | None -> ()
          for l in locations do
              yield! Option.toList (str l "pdf_url") ]
    let arxiv =
        [ yield! Option.toList doi
          for l in locations do
              yield! Option.toList (str l "landing_page_url")
              yield! Option.toList (str l "pdf_url") ]
        |> List.tryPick arxivOf
    let pdfs = orderPdfs (pdfUrls @ (arxiv |> Option.map (fun id -> "https://arxiv.org/pdf/" + id) |> Option.toList))
    match title with
    | Some title when not pdfs.IsEmpty ->
        let openAlex = str w "id" |> Option.map (fun id -> id.Substring(id.LastIndexOf '/' + 1))
        let venue =
            [ prop w "primary_location"; best ]
            |> List.tryPick (Option.bind (fun l -> prop l "source" |> Option.bind (fun s -> str s "display_name")))
        let page =
            [ yield! Option.toList (doi |> Option.map (fun d -> "https://doi.org/" + d))
              match best with Some b -> yield! Option.toList (str b "landing_page_url") | None -> ()
              match prop w "primary_location" with Some p -> yield! Option.toList (str p "landing_page_url") | None -> () ]
            |> List.tryHead
        Some
            { Key = defaultArg openAlex title
              Title = title
              Authors =
                items w "authorships"
                |> List.choose (fun a -> prop a "author" |> Option.bind (fun a -> str a "display_name"))
              Year = int' w "publication_year"
              Venue = venue
              Abstract = prop w "abstract_inverted_index" |> Option.bind abstractFromIndex
              Citations = defaultArg (int' w "cited_by_count") 0
              Pdfs = pdfs
              Page = page
              Doi = doi
              Arxiv = arxiv
              OpenAlex = openAlex }
    | _ -> None

let private openAlexError (status: HttpStatusCode) (body: string) (hasKey: bool) =
    match int status with
    | 429 when hasKey -> "OpenAlex's daily limit for your key is used up. Try again tomorrow."
    | 429 -> "OpenAlex's free daily search limit is used up (about 100 searches a day without a key). Add a free OpenAlex key in Settings, or try again tomorrow."
    | 401 | 403 -> "OpenAlex rejected the API key. Check it in Settings."
    | s ->
        let detail = if body.Length > 200 then body.Substring(0, 200) else body
        sprintf "OpenAlex error %d: %s" s detail

/// GET from OpenAlex; None for 404.
let private openAlexGet (key: string) (pathAndQuery: string) (ct: CancellationToken) : Task<JsonDocument option> =
    task {
        let sep = if pathAndQuery.Contains '?' then "&" else "?"
        let url = openAlexUrl + pathAndQuery + (if String.IsNullOrWhiteSpace key then "" else sep + "api_key=" + Uri.EscapeDataString(key.Trim()))
        let! resp =
            task {
                try return! http.GetAsync(url, ct)
                with :? HttpRequestException -> return raise (DiscoverError "Couldn't reach OpenAlex. Check the internet connection.")
            }
        use resp = resp
        let! body = resp.Content.ReadAsStringAsync(ct)
        if resp.StatusCode = HttpStatusCode.NotFound then return None
        elif not resp.IsSuccessStatusCode then return raise (DiscoverError(openAlexError resp.StatusCode body (not (String.IsNullOrWhiteSpace key))))
        else return Some(JsonDocument.Parse body)
    }

let pageSize = 20

/// Papers with a free PDF matching the words, most relevant first. `page` starts at 1.
let search (key: string) (words: string) (page: int) (ct: CancellationToken) : Task<Results> =
    task {
        let q =
            sprintf "/works?search=%s&filter=has_pdf_url:true,is_paratext:false&per_page=%d&page=%d&select=%s"
                (Uri.EscapeDataString words) pageSize page workFields
        match! openAlexGet key q ct with
        | None -> return { Items = []; Total = 0 }
        | Some d ->
            use d = d
            let total = prop d.RootElement "meta" |> Option.bind (fun m -> int' m "count") |> Option.defaultValue 0
            return { Items = items d.RootElement "results" |> List.choose parseWork; Total = total }
    }

/// One paper by DOI (an arXiv id is looked up by its arXiv DOI). Singleton lookups are free on OpenAlex.
let private byDoi (key: string) (doi: string) (ct: CancellationToken) : Task<Found option> =
    task {
        match! openAlexGet key (sprintf "/works/doi:%s?select=%s" (Uri.EscapeDataString doi) workFields) ct with
        | Some d ->
            use d = d
            return parseWork d.RootElement
        | None -> return None
    }

/// An arXiv entry (Atom XML) as a found paper.
let parseArxivEntry (id: string) (xml: string) : Found option =
    let atom = Xml.Linq.XNamespace.Get "http://www.w3.org/2005/Atom"
    let doc = Xml.Linq.XDocument.Parse xml
    doc.Root.Elements(atom + "entry")
    |> Seq.tryHead
    |> Option.bind (fun e ->
        let text (name: string) =
            match e.Element(atom + name) with
            | null -> None
            | x when String.IsNullOrWhiteSpace x.Value -> None
            | x -> Some(Regex.Replace(x.Value, @"\s+", " ").Trim())
        text "title"
        |> Option.map (fun title ->
            { Key = "arXiv:" + id
              Title = title
              Authors = e.Elements(atom + "author") |> Seq.choose (fun a -> match a.Element(atom + "name") with null -> None | n -> Some n.Value) |> List.ofSeq
              Year = text "published" |> Option.bind (fun p -> match Int32.TryParse(p.Substring(0, min 4 p.Length)) with | true, y -> Some y | _ -> None)
              Venue = Some "arXiv"
              Abstract = text "summary"
              Citations = 0
              Pdfs = [ "https://arxiv.org/pdf/" + id ]
              Page = Some("https://arxiv.org/abs/" + id)
              Doi = None
              Arxiv = Some id
              OpenAlex = None }))

/// arXiv's own record of a paper (used when OpenAlex doesn't have it; arXiv's API can't be called from a web page).
let private fromArxiv (id: string) (ct: CancellationToken) : Task<Found option> =
    task {
        try
            let! xml = http.GetStringAsync("https://export.arxiv.org/api/query?id_list=" + Uri.EscapeDataString id, ct)
            return parseArxivEntry id xml
        with
        | :? OperationCanceledException when ct.IsCancellationRequested -> return raise (OperationCanceledException())
        | _ -> return None
    }

/// A paper for the exact thing typed: an arXiv id or address, a DOI, or a PDF address.
let lookup (key: string) (query: Query) (ct: CancellationToken) : Task<Found option> =
    task {
        match query with
        | Query.Arxiv id ->
            let! found = byDoi key ("10.48550/arXiv." + id) ct
            let pdf = "https://arxiv.org/pdf/" + id
            match found with
            | Some f -> return Some { f with Pdfs = orderPdfs (pdf :: f.Pdfs); Arxiv = Some id }
            | None ->
                // very new papers (and a few old ones) aren't in OpenAlex under their arXiv DOI
                match! fromArxiv id ct with
                | Some f -> return Some f
                | None ->
                    return
                        Some
                            { Key = "arXiv:" + id; Title = "arXiv:" + id; Authors = []; Year = None; Venue = Some "arXiv"; Abstract = None
                              Citations = 0; Pdfs = [ pdf ]; Page = Some("https://arxiv.org/abs/" + id); Doi = None; Arxiv = Some id; OpenAlex = None }
        | Query.Doi doi -> return! byDoi key doi ct
        | Query.Url url ->
            let name = try Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(Uri(url).AbsolutePath)) with _ -> url
            return
                Some
                    { Key = url; Title = (if String.IsNullOrWhiteSpace name then url else name); Authors = []; Year = None; Venue = Some(host url)
                      Abstract = None; Citations = 0; Pdfs = [ url ]; Page = None; Doi = None; Arxiv = arxivOf url; OpenAlex = None }
        | Query.Words _ -> return None
    }

/// Finds a library paper on OpenAlex by its title; None unless the best match has (nearly) the same title.
let identify (key: string) (title: string) (ct: CancellationToken) : Task<Source option> =
    task {
        // commas separate filters in OpenAlex queries
        let words = Regex.Replace(title, @"[,:;|!?()\[\]{}""]", " ").Trim()
        if words.Length < 8 then return None
        else
            let q = sprintf "/works?filter=title.search:%s&per_page=3&select=id,doi,title,locations" (Uri.EscapeDataString words)
            match! openAlexGet key q ct with
            | None -> return None
            | Some d ->
                use d = d
                return
                    items d.RootElement "results"
                    |> List.tryFind (fun w -> titleSimilarity title (defaultArg (str w "title") "") >= 0.8)
                    |> Option.map (fun w ->
                        let doi = str w "doi" |> Option.bind doiOf
                        let arxiv =
                            [ yield! Option.toList doi
                              for l in items w "locations" do
                                  yield! Option.toList (str l "landing_page_url") ]
                            |> List.tryPick arxivOf
                        { Doi = doi; Arxiv = arxiv; OpenAlex = str w "id" |> Option.map (fun id -> id.Substring(id.LastIndexOf '/' + 1)) })
    }

/// Well-cited recent papers that cite the given OpenAlex works: recommendations when Semantic Scholar is unavailable.
let citing (key: string) (openAlexIds: string list) (ct: CancellationToken) : Task<Found list> =
    task {
        if openAlexIds.IsEmpty then return []
        else
            let since = DateTime.UtcNow.AddYears(-3).ToString("yyyy-MM-dd")
            let q =
                sprintf "/works?filter=cites:%s,has_pdf_url:true,from_publication_date:%s&sort=cited_by_count:desc&per_page=%d&select=%s"
                    (String.Join("|", openAlexIds |> List.truncate 20)) since 30 workFields
            match! openAlexGet key q ct with
            | None -> return []
            | Some d ->
                use d = d
                return items d.RootElement "results" |> List.choose parseWork
    }

// ---------------------------------------------------------------------------------------------
// Semantic Scholar recommendations
// ---------------------------------------------------------------------------------------------

/// A paper from Semantic Scholar, or None when it has no free PDF (no open-access copy and not on arXiv).
let parseS2Paper (p: JsonElement) : Found option =
    let ids = prop p "externalIds"
    let arxiv = ids |> Option.bind (fun i -> str i "ArXiv")
    let doi = ids |> Option.bind (fun i -> str i "DOI")
    let oaPdf =
        prop p "openAccessPdf" |> Option.bind (fun o -> str o "url")
        // "open access" links to doi.org are publisher pages, not files
        |> Option.filter (fun u -> not (u.Contains "doi.org/"))
    let pdfs = orderPdfs (Option.toList oaPdf @ (arxiv |> Option.map (fun id -> "https://arxiv.org/pdf/" + id) |> Option.toList))
    match str p "title", str p "paperId" with
    | Some title, Some id when not pdfs.IsEmpty ->
        Some
            { Key = "S2:" + id
              Title = cleanTitle title
              Authors = items p "authors" |> List.choose (fun a -> str a "name")
              Year = int' p "year"
              Venue = str p "venue" |> Option.orElse (if arxiv.IsSome then Some "arXiv" else None)
              Abstract = str p "abstract"
              Citations = defaultArg (int' p "citationCount") 0
              Pdfs = pdfs
              Page =
                match doi, arxiv with
                | Some d, _ -> Some("https://doi.org/" + d)
                | None, Some a -> Some("https://arxiv.org/abs/" + a)
                | _ -> str p "url"
              Doi = doi
              Arxiv = arxiv
              OpenAlex = None }
    | _ -> None

/// The id Semantic Scholar knows a library paper by.
let s2Id (s: Source) =
    match s.Arxiv, s.Doi with
    | Some a, _ -> Some("ARXIV:" + a)
    | None, Some d -> Some("DOI:" + d)
    | _ -> None

/// Papers like the given ones (Semantic Scholar's recommender, which favours recent papers). The unauthenticated
/// pool is shared by everyone, so busy moments are retried a few times.
let recommend (seeds: string list) (ct: CancellationToken) : Task<Found list> =
    task {
        let body =
            use ms = new MemoryStream()
            do
                use w = new Utf8JsonWriter(ms)
                w.WriteStartObject()
                w.WriteStartArray "positivePaperIds"
                for s in seeds |> List.truncate 50 do
                    w.WriteStringValue s
                w.WriteEndArray()
                w.WriteEndObject()
            Encoding.UTF8.GetString(ms.ToArray())
        let url =
            semanticScholarUrl + "/recommendations/v1/papers?limit=60&fields=title,year,authors,venue,abstract,citationCount,externalIds,openAccessPdf,url"
        let mutable attempt = 0
        let mutable result = None
        while result.IsNone do
            // text/plain keeps it a "simple" request, which browsers send without asking permission first
            use content = new StringContent(body, Encoding.UTF8, "text/plain")
            let! resp =
                task {
                    try return! http.PostAsync(url, content, ct)
                    with :? HttpRequestException -> return raise (DiscoverError "Couldn't reach Semantic Scholar. Check the internet connection.")
                }
            use resp = resp
            let! text = resp.Content.ReadAsStringAsync(ct)
            if resp.IsSuccessStatusCode then
                use d = JsonDocument.Parse text
                result <- Some(items d.RootElement "recommendedPapers" |> List.choose parseS2Paper)
            elif (int resp.StatusCode = 429 || int resp.StatusCode >= 500) && attempt < 3 then
                attempt <- attempt + 1
                do! Task.Delay(2000 * attempt, ct)
            elif int resp.StatusCode = 429 then raise (DiscoverError "Semantic Scholar is busy right now.")
            else raise (DiscoverError(sprintf "Semantic Scholar error %d." (int resp.StatusCode)))
        return result.Value
    }

// ---------------------------------------------------------------------------------------------
// Where library papers came from
// ---------------------------------------------------------------------------------------------

let private sourcePath (p: Store.Paths) (id: string) = Path.Combine(p.Paper id, "source.json")

/// What is known about where a library paper came from: Some None when it was looked up and not found.
let loadSource (p: Store.Paths) (id: string) : Source option option =
    try
        let path = sourcePath p id
        if not (File.Exists path) then None
        else
            use d = JsonDocument.Parse(File.ReadAllText path)
            let e = d.RootElement
            let s = { Doi = str e "doi"; Arxiv = str e "arxiv"; OpenAlex = str e "openAlex" }
            Some(if s.Doi.IsNone && s.Arxiv.IsNone && s.OpenAlex.IsNone then None else Some s)
    with _ -> None

let saveSource (p: Store.Paths) (id: string) (s: Source option) =
    try
        Directory.CreateDirectory(p.Paper id) |> ignore
        use fs = File.Create(sourcePath p id)
        use w = new Utf8JsonWriter(fs)
        w.WriteStartObject()
        match s with
        | Some s ->
            s.Doi |> Option.iter (fun v -> w.WriteString("doi", v))
            s.Arxiv |> Option.iter (fun v -> w.WriteString("arxiv", v))
            s.OpenAlex |> Option.iter (fun v -> w.WriteString("openAlex", v))
        | None -> ()
        w.WriteEndObject()
    with _ -> ()

/// The library papers' sources, looking up (once, and at most `lookups` per call) the ones not known yet.
let librarySources (key: string) (p: Store.Paths) (papers: PaperInfo list) (lookups: int) (progress: string -> unit) (ct: CancellationToken)
    : Task<(PaperInfo * Source) list> =
    task {
        let mutable left = lookups
        let found = Collections.Generic.List<PaperInfo * Source>()
        for paper in papers do
            match loadSource p paper.Id with
            | Some (Some s) -> found.Add((paper, s))
            | Some None -> ()
            | None when left > 0 ->
                left <- left - 1
                progress (sprintf "Looking up \"%s\"" paper.Title)
                try
                    let! s = identify key paper.Title ct
                    saveSource p paper.Id s
                    s |> Option.iter (fun s -> found.Add((paper, s)))
                with
                | :? OperationCanceledException -> raise (OperationCanceledException())
                | _ -> () // tried again next time
            | None -> ()
        return List.ofSeq found
    }

/// True when the found paper is already in the library (same DOI, arXiv id or title).
let inLibrary (known: (string * Source) list) (f: Found) =
    let same (a: string option) (b: string option) =
        match a, b with
        | Some a, Some b -> String.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        | _ -> false
    let title = normalizeTitle f.Title
    known
    |> List.exists (fun (t, s) ->
        same s.Doi f.Doi || same s.Arxiv f.Arxiv || same s.OpenAlex f.OpenAlex || (title.Length > 12 && normalizeTitle t = title))

/// Recommendations for the library: Semantic Scholar's for the papers it can identify, or (if that fails)
/// recent, well-cited papers citing them from OpenAlex. Papers already in the library are left out.
let forLibrary (key: string) (p: Store.Paths) (papers: PaperInfo list) (progress: string -> unit) (ct: CancellationToken) : Task<Found list> =
    task {
        // the most recently added papers say most about what the listener wants now
        let recent = papers |> List.sortByDescending (fun x -> x.AddedUtc) |> List.truncate 15
        let! sources = librarySources key p recent 6 progress ct
        if sources.IsEmpty then return []
        else
            let known = papers |> List.map (fun x -> x.Title, (match loadSource p x.Id with Some (Some s) -> s | _ -> { Doi = None; Arxiv = None; OpenAlex = None }))
            progress "Finding papers like yours"
            let! recs =
                task {
                    let seeds = sources |> List.choose (snd >> s2Id)
                    try
                        if seeds.IsEmpty then return []
                        else return! recommend seeds ct
                    with
                    | :? OperationCanceledException -> return raise (OperationCanceledException())
                    | DiscoverError _ | :? HttpRequestException | :? JsonException -> return []
                }
            let! recs =
                if not recs.IsEmpty then Task.FromResult recs
                else citing key (sources |> List.choose (snd >> fun s -> s.OpenAlex)) ct
            return recs |> List.filter (inLibrary known >> not) |> List.distinctBy (fun f -> normalizeTitle f.Title)
    }

// ---------------------------------------------------------------------------------------------
// Download
// ---------------------------------------------------------------------------------------------

/// True when the bytes are a PDF (servers often answer with a web page instead).
let isPdf (bytes: byte[]) =
    let n = min bytes.Length 1024
    let head = Encoding.ASCII.GetString(bytes, 0, n)
    head.Contains "%PDF-"

let private maxBytes = 150L * 1024L * 1024L

/// Downloads the paper's PDF into `folder`, trying each address in turn (for an address typed in, whatever
/// document is there). Returns the file and a display name.
let download (f: Found) (folder: string) (progress: string -> unit) (ct: CancellationToken) : Task<string * string> =
    task {
        Directory.CreateDirectory folder |> ignore
        let mutable result = None
        let mutable failures = []
        for url in f.Pdfs do
            if result.IsNone then
                ct.ThrowIfCancellationRequested()
                progress (sprintf "Downloading from %s" (host url))
                try
                    use req = new HttpRequestMessage(HttpMethod.Get, url)
                    req.Headers.Accept.ParseAdd "application/pdf,*/*;q=0.8"
                    use! resp = http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    if not resp.IsSuccessStatusCode then
                        failures <- sprintf "%s refused (%d)" (host url) (int resp.StatusCode) :: failures
                    elif resp.Content.Headers.ContentLength.HasValue && resp.Content.Headers.ContentLength.Value > maxBytes then
                        failures <- sprintf "%s: the file is too large" (host url) :: failures
                    else
                        let! bytes = resp.Content.ReadAsByteArrayAsync(ct)
                        if isPdf bytes then
                            let path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".pdf")
                            File.WriteAllBytes(path, bytes)
                            let name =
                                let safe = Regex.Replace(f.Title, @"[^\p{L}\p{N} \-_.,]", "").Trim()
                                (if safe.Length > 80 then safe.Substring(0, 80) else safe) + ".pdf"
                            result <- Some(path, name)
                        elif f.Key = url then
                            // an address typed or pasted (see lookup): any document the app reads, a web page too
                            match Formats.downloaded url bytes with
                            | Some (b, name) ->
                                let path = Path.Combine(folder, Guid.NewGuid().ToString("N") + Path.GetExtension name)
                                File.WriteAllBytes(path, b)
                                result <- Some(path, name)
                            | None -> failures <- sprintf "%s sent something Paper Reader can't read" (host url) :: failures
                        else failures <- sprintf "%s sent a web page, not the PDF" (host url) :: failures
                with
                | :? OperationCanceledException when ct.IsCancellationRequested -> raise (OperationCanceledException())
                | :? TaskCanceledException -> failures <- sprintf "%s took too long" (host url) :: failures
                | :? HttpRequestException -> failures <- sprintf "couldn't connect to %s" (host url) :: failures
        match result with
        | Some r -> return r
        | None ->
            let why = String.Join("; ", List.rev failures)
            return raise (DiscoverError(sprintf "Couldn't download the PDF (%s)." why))
    }

/// After a download, remembers where the paper came from, so it counts as known for recommendations.
let rememberSource (p: Store.Paths) (pdfPath: string) (f: Found) =
    try
        let id = Store.paperId pdfPath
        saveSource p id (Some { Doi = f.Doi; Arxiv = f.Arxiv; OpenAlex = f.OpenAlex })
    with _ -> ()
