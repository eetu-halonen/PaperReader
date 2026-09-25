namespace PaperReader.Android

open Android.App
open Avalonia
open Avalonia.Android
open PaperReader

[<Application(Label = "Paper Reader", Icon = "@mipmap/ic_launcher")>]
type MainApplication(javaReference: nativeint, transfer: Android.Runtime.JniHandleOwnership) =
    inherit AvaloniaAndroidApplication<App>(javaReference, transfer)

    override this.OnCreate() =
        Services.platform <- Some(AndroidPlatform(this) :> IPlatform)
        base.OnCreate()

    override _.CustomizeAppBuilder(builder) =
        base.CustomizeAppBuilder(builder).WithInterFont()
