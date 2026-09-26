namespace PaperReader.Android

open System
open System.IO
open Android.App
open Android.Content
open Android.Content.PM
open Android.Provider
open Avalonia.Android
open PaperReader

[<Activity(Label = "Paper Reader",
           Theme = "@style/MyTheme.NoActionBar",
           Icon = "@mipmap/ic_launcher",
           MainLauncher = true,
           Exported = true,
           LaunchMode = LaunchMode.SingleTask,
           ConfigurationChanges = (ConfigChanges.Orientation ||| ConfigChanges.ScreenSize ||| ConfigChanges.UiMode ||| ConfigChanges.ScreenLayout ||| ConfigChanges.SmallestScreenSize))>]
// the kinds of document Formats reads (Formats.mimeTypes; attributes need the list written out)
[<IntentFilter([| Intent.ActionView |], Categories = [| Intent.CategoryDefault; Intent.CategoryBrowsable |],
               DataMimeTypes = [| "application/pdf"; "application/epub+zip"
                                  "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                                  "application/vnd.oasis.opendocument.text"
                                  "application/vnd.openxmlformats-officedocument.presentationml.presentation"
                                  "text/html"; "application/xhtml+xml"; "text/markdown"; "text/x-markdown"; "text/plain"
                                  "image/png"; "image/jpeg"; "image/webp" |])>]
// Share: the same files, and shared text or links (a link shared from the browser opens that page or PDF)
[<IntentFilter([| Intent.ActionSend |], Categories = [| Intent.CategoryDefault |],
               DataMimeTypes = [| "application/pdf"; "application/epub+zip"
                                  "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                                  "application/vnd.oasis.opendocument.text"
                                  "application/vnd.openxmlformats-officedocument.presentationml.presentation"
                                  "text/html"; "application/xhtml+xml"; "text/markdown"; "text/x-markdown"; "text/plain"
                                  "image/png"; "image/jpeg"; "image/webp" |])>]
type MainActivity() =
    inherit AvaloniaMainActivity()

    /// Copies a shared/opened document into app storage so it outlives the sender's permission grant. Shared
    /// text (or a link) is kept as a text file: the importer reads the text, or downloads what the link points to.
    member private this.Receive(intent: Intent) =
        if not (isNull intent) then
            let sharedText =
                if intent.Action = Intent.ActionSend && isNull (intent.GetParcelableExtra(Intent.ExtraStream)) then
                    intent.GetStringExtra(Intent.ExtraText) |> Option.ofObj |> Option.filter (fun t -> t.Trim() <> "")
                else None
            match sharedText with
            | Some text ->
                try
                    let incoming = Path.Combine(this.FilesDir.AbsolutePath, "incoming")
                    Directory.CreateDirectory incoming |> ignore
                    let dest = Path.Combine(incoming, Guid.NewGuid().ToString("N") + ".txt")
                    File.WriteAllText(dest, text)
                    let subject = intent.GetStringExtra(Intent.ExtraSubject) |> Option.ofObj |> Option.defaultValue "Shared text"
                    Incoming.deliver (dest, subject + ".txt")
                with e -> Android.Util.Log.Warn("PaperReader", "could not keep shared text: " + e.Message) |> ignore
                intent.SetAction(Intent.ActionMain) |> ignore
            | None -> ()
            let uri =
                if intent.Action = Intent.ActionView then intent.Data
                elif intent.Action = Intent.ActionSend then
                    match intent.GetParcelableExtra(Intent.ExtraStream) with
                    | :? Android.Net.Uri as u -> u
                    | _ -> null
                else null
            if not (isNull uri) then
                let resolver = this.ContentResolver
                let name =
                    try
                        use cursor = resolver.Query(uri, [| OpenableColumns.DisplayName |], null, null, null)
                        if not (isNull cursor) && cursor.MoveToFirst() then cursor.GetString 0 else "document"
                    with _ -> "document"
                // without an extension in the name, the media type gives one (the content decides in the end)
                let name =
                    if Path.HasExtension name then name
                    else
                        let ext = Android.Webkit.MimeTypeMap.Singleton.GetExtensionFromMimeType(resolver.GetType uri)
                        if String.IsNullOrEmpty ext then name else name + "." + ext
                try
                    let incoming = Path.Combine(this.FilesDir.AbsolutePath, "incoming")
                    Directory.CreateDirectory incoming |> ignore
                    let dest = Path.Combine(incoming, Guid.NewGuid().ToString("N") + Path.GetExtension(name).ToLowerInvariant())
                    do
                        use input = resolver.OpenInputStream uri
                        use output = File.Create dest
                        input.CopyTo output
                    Incoming.deliver (dest, name)
                with e -> Android.Util.Log.Warn("PaperReader", "could not read shared document: " + e.Message) |> ignore
                // handled: don't re-import on configuration changes
                intent.SetAction(Intent.ActionMain) |> ignore

    override this.OnCreate(savedInstanceState) =
        AndroidPlatform.Activity <- this
        base.OnCreate savedInstanceState
        // the media notification (pause from the lock screen) needs this from Android 13
        if Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.Tiramisu
           && this.CheckSelfPermission(Android.Manifest.Permission.PostNotifications) <> Permission.Granted then
            this.RequestPermissions([| Android.Manifest.Permission.PostNotifications |], 1)
        this.Receive this.Intent

    override this.OnRequestPermissionsResult(requestCode, permissions, grantResults) =
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults)
        Permissions.answered requestCode (grantResults.Length > 0 && grantResults.[0] = Permission.Granted)

    override this.OnNewIntent(intent) =
        base.OnNewIntent intent
        this.Receive intent

    override this.OnResume() =
        base.OnResume()
        AndroidPlatform.Activity <- this
