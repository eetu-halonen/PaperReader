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
[<IntentFilter([| Intent.ActionView |], Categories = [| Intent.CategoryDefault; Intent.CategoryBrowsable |], DataMimeType = "application/pdf")>]
[<IntentFilter([| Intent.ActionSend |], Categories = [| Intent.CategoryDefault |], DataMimeType = "application/pdf")>]
type MainActivity() =
    inherit AvaloniaMainActivity()

    /// Copies a shared/opened PDF into app storage so it outlives the sender's permission grant.
    member private this.Receive(intent: Intent) =
        if not (isNull intent) then
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
                        if not (isNull cursor) && cursor.MoveToFirst() then cursor.GetString 0 else "paper.pdf"
                    with _ -> "paper.pdf"
                try
                    let incoming = Path.Combine(this.FilesDir.AbsolutePath, "incoming")
                    Directory.CreateDirectory incoming |> ignore
                    let dest = Path.Combine(incoming, Guid.NewGuid().ToString("N") + ".pdf")
                    do
                        use input = resolver.OpenInputStream uri
                        use output = File.Create dest
                        input.CopyTo output
                    Incoming.deliver (dest, name)
                with e -> Android.Util.Log.Warn("PaperReader", "could not read shared PDF: " + e.Message) |> ignore
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

    override this.OnNewIntent(intent) =
        base.OnNewIntent intent
        this.Receive intent

    override this.OnResume() =
        base.OnResume()
        AndroidPlatform.Activity <- this
