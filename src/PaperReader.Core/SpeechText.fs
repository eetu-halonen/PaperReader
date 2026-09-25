/// Rewriting extracted paper text so it sounds right when spoken.
module PaperReader.Core.SpeechText

open System
open System.Text.RegularExpressions

let private rx (p: string) = Regex(p, RegexOptions.Compiled ||| RegexOptions.CultureInvariant)

let private numericCitation = rx @"\s?\[\s*\d+[a-z]?(\s*[,–—-]\s*\d+[a-z]?)*\s*\]"
let private yearOnly = rx @"\s?\(\s*(?:19|20)\d{2}[a-z]?(\s*[,;]\s*(?:19|20)\d{2}[a-z]?)*\s*\)"
let private authorYear = rx @"\s?\((?:see |e\.g\.,? |cf\. )?[^()]{0,160}?\b(?:19|20)\d{2}[a-z]?\)"
let private url = rx @"(https?://|www\.)\S+"
let private spacedHyphen = rx @"(\w)- (\w)"

let private abbreviations =
    [ rx @"\be\.\s?g\.,?", "for example,"
      rx @"\bi\.\s?e\.,?", "that is,"
      rx @"\bet al\.", "and colleagues"
      rx @"\bw\.r\.t\.", "with respect to"
      rx @"\bi\.i\.d\.", "I I D"
      rx @"\bresp\.", "respectively"
      rx @"\bcf\.", "compare"
      rx @"\bvs\.", "versus"
      rx @"\betc\.", "et cetera."
      rx @"\bFigs?\.\s*", "Figure "
      rx @"\bEqs?\.\s*", "Equation "
      rx @"\bSecs?\.\s*", "Section "
      rx @"\bTab\.\s*", "Table "
      rx @"\bThm\.\s*", "Theorem "
      rx @"\bDef\.\s*", "Definition "
      rx @"\bApp\.\s*", "Appendix "
      rx @"\bRef\.\s*", "Reference "
      rx @"\bNo\.\s*(\d)", "number $1"
      rx @"\bapprox\.", "approximately" ]

/// Removes citation markers that are noise when listening.
let stripCitations (s: string) =
    let s = numericCitation.Replace(s, "")
    let s = yearOnly.Replace(s, "")
    authorYear.Replace(s, fun m -> if m.Value.Contains "=" then m.Value else "")

/// Everything the offline narrator does to a sentence before speaking it.
let forSpeech (s: string) =
    let mutable t = MathText.compat s
    t <- stripCitations t
    t <- url.Replace(t, "a link")
    for (r, w) in abbreviations do
        t <- r.Replace(t, w)
    t <- spacedHyphen.Replace(t, "$1-$2")
    MathText.tidy t

let private noSplitAfter =
    set [ "e.g."; "i.e."; "al."; "etc."; "vs."; "cf."; "fig."; "figs."; "eq."; "eqs."; "sec."; "secs."
          "tab."; "resp."; "approx."; "no."; "dr."; "prof."; "mr."; "ms."; "thm."; "def."; "ref."
          "w.r.t."; "i.i.d."; "st."; "app."; "vol."; "pp."; "ch." ]

/// True if a sentence ends after `word`, given the word that follows.
let endsSentence (word: string) (next: string) =
    if String.IsNullOrEmpty word || String.IsNullOrEmpty next then false
    else
        let w = word.TrimEnd(')', '"', '”', '’', ']')
        let terminal = w.EndsWith "." || w.EndsWith "?" || w.EndsWith "!"
        if not terminal then false
        elif noSplitAfter.Contains(w.TrimStart('(', '[', '"', '“').ToLowerInvariant()) then false
        // single initials like "J." inside names
        elif w.Length = 2 && Char.IsUpper w.[0] then false
        else
            let n = next.TrimStart('(', '"', '“', '‘', '[')
            n.Length = 0 || Char.IsUpper n.[0] || Char.IsDigit n.[0] || not (Char.IsLetter n.[0])
