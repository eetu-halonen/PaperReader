namespace PaperReader.Android

open Android.App
open Android.Content
open Android.Content.PM
open Android.Media
open Android.Media.Session
open Android.OS

/// What the reader is playing, shared between the app and the playback service.
module Playback =
    /// Asks the app to play (true) or pause (false): notification buttons, headset keys, audio focus.
    let mutable remote: bool -> unit = ignore
    let mutable title = ""
    let mutable subtitle = ""
    let mutable playing = false

type private FocusListener(onChange: AudioFocus -> unit) =
    inherit Java.Lang.Object()
    interface AudioManager.IOnAudioFocusChangeListener with
        member _.OnAudioFocusChange(focus) = onChange focus

type private NoisyReceiver() =
    inherit BroadcastReceiver()
    override _.OnReceive(_, intent) =
        if not (isNull intent) && intent.Action = AudioManager.ActionAudioBecomingNoisy then Playback.remote false

type private SessionCallback() =
    inherit MediaSession.Callback()
    override _.OnPlay() = Playback.remote true
    override _.OnPause() = Playback.remote false
    override _.OnStop() = Playback.remote false

/// Keeps the app alive while it reads in the background, and shows the media notification.
/// Runs in the foreground while audio plays; when paused the notification stays, with a play button.
[<Service(Name = "app.paperreader.PlaybackService", Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)>]
type PlaybackService() =
    inherit Service()

    static let channelId = "playback"
    static let notificationId = 1

    let mutable session: MediaSession = null
    let mutable focus: (AudioFocusRequestClass * NoisyReceiver) option = None
    let mutable resumeOnGain = false

    static member val Current: PlaybackService option = None with get, set

    member private this.Audio = this.GetSystemService(Context.AudioService) :?> AudioManager

    member private this.Action(action: string, icon: int, label: string) =
        let intent = new Intent(this, typeof<PlaybackService>)
        intent.SetAction(action) |> ignore
        let pending = PendingIntent.GetService(this, action.GetHashCode(), intent, PendingIntentFlags.Immutable ||| PendingIntentFlags.UpdateCurrent)
        (new Notification.Action.Builder(Android.Graphics.Drawables.Icon.CreateWithResource(this, icon), label, pending)).Build()

    member private this.Notification() =
        let openApp = this.PackageManager.GetLaunchIntentForPackage(this.PackageName)
        let content = PendingIntent.GetActivity(this, 0, openApp, PendingIntentFlags.Immutable ||| PendingIntentFlags.UpdateCurrent)
        let toggle =
            if Playback.playing then this.Action("pause", Android.Resource.Drawable.IcMediaPause, "Pause")
            else this.Action("play", Android.Resource.Drawable.IcMediaPlay, "Play")
        (new Notification.Builder(this, channelId))
            .SetSmallIcon(Resource.Drawable.ic_launcher_foreground)
            .SetContentTitle(Playback.title)
            .SetContentText(Playback.subtitle)
            .SetContentIntent(content)
            .SetOngoing(Playback.playing)
            .SetShowWhen(false)
            .SetVisibility(NotificationVisibility.Public)
            .AddAction(toggle)
            .SetStyle((new Notification.MediaStyle()).SetMediaSession(session.SessionToken).SetShowActionsInCompactView(0))
            .Build()

    member private this.HoldFocus(hold: bool) =
        let am = this.Audio
        if hold && focus.IsNone then
            let attrs = AudioAttributes.Builder().SetUsage(AudioUsageKind.Media).SetContentType(AudioContentType.Speech).Build()
            let listener =
                new FocusListener(fun change ->
                    match change with
                    | AudioFocus.Gain ->
                        if resumeOnGain then
                            resumeOnGain <- false
                            Playback.remote true
                    | AudioFocus.LossTransient
                    | AudioFocus.LossTransientCanDuck ->
                        // a call or a navigation prompt: speech can't be ducked usefully, so pause and resume after
                        if Playback.playing then
                            resumeOnGain <- true
                            Playback.remote false
                    | AudioFocus.Loss ->
                        resumeOnGain <- false
                        Playback.remote false
                    | _ -> ())
            let request =
                (new AudioFocusRequestClass.Builder(AudioFocus.Gain))
                    .SetAudioAttributes(attrs)
                    .SetWillPauseWhenDucked(true)
                    .SetOnAudioFocusChangeListener(listener)
                    .Build()
            am.RequestAudioFocus request |> ignore
            let noisy = new NoisyReceiver()
            this.RegisterReceiver(noisy, new IntentFilter(AudioManager.ActionAudioBecomingNoisy)) |> ignore
            focus <- Some(request, noisy)
        elif not hold && not resumeOnGain then
            match focus with
            | Some (request, noisy) ->
                am.AbandonAudioFocusRequest request |> ignore
                this.UnregisterReceiver noisy
                focus <- None
            | None -> ()

    /// Brings the notification, session and foreground state in line with `Playback`.
    member this.Refresh() =
        let state =
            (new PlaybackState.Builder())
                .SetActions(PlaybackState.ActionPlay ||| PlaybackState.ActionPause ||| PlaybackState.ActionPlayPause)
                .SetState((if Playback.playing then PlaybackStateCode.Playing else PlaybackStateCode.Paused), PlaybackState.PlaybackPositionUnknown, 1.0f)
                .Build()
        session.SetPlaybackState state
        session.SetMetadata(
            (new MediaMetadata.Builder())
                .PutString(MediaMetadata.MetadataKeyTitle, Playback.title)
                .PutString(MediaMetadata.MetadataKeyArtist, Playback.subtitle)
                .Build())
        this.HoldFocus Playback.playing
        let n = this.Notification()
        if Playback.playing then
            if Build.VERSION.SdkInt >= BuildVersionCodes.Q then this.StartForeground(notificationId, n, ForegroundService.TypeMediaPlayback)
            else this.StartForeground(notificationId, n)
        else
            this.StopForeground(StopForegroundFlags.Detach)
            (this.GetSystemService(Context.NotificationService) :?> NotificationManager).Notify(notificationId, n)

    override _.OnBind(_) = null

    override this.OnCreate() =
        base.OnCreate()
        let nm = this.GetSystemService(Context.NotificationService) :?> NotificationManager
        let channel = new NotificationChannel(channelId, "Playback", NotificationImportance.Low)
        channel.Description <- "Shows the paper being read, with a pause button"
        channel.SetShowBadge false
        nm.CreateNotificationChannel channel
        session <- new MediaSession(this, "PaperReader")
        session.SetCallback(new SessionCallback())
        session.Active <- true
        PlaybackService.Current <- Some this

    override this.OnStartCommand(intent, _, _) =
        match (if isNull intent then null else intent.Action) with
        | "play" -> Playback.remote true
        | "pause" -> Playback.remote false
        | _ -> ()
        // started with startForegroundService: it must go foreground now, even if the app paused meanwhile
        let n = this.Notification()
        if Build.VERSION.SdkInt >= BuildVersionCodes.Q then this.StartForeground(notificationId, n, ForegroundService.TypeMediaPlayback)
        else this.StartForeground(notificationId, n)
        this.Refresh()
        StartCommandResult.NotSticky

    override this.OnDestroy() =
        resumeOnGain <- false
        this.HoldFocus false
        session.Release()
        (this.GetSystemService(Context.NotificationService) :?> NotificationManager).Cancel notificationId
        PlaybackService.Current <- None
        base.OnDestroy()

    /// Updates the service to the app's playback state, starting it when playback starts.
    static member Update(context: Context, title: string, subtitle: string, playing: bool) =
        Playback.title <- title
        Playback.subtitle <- subtitle
        Playback.playing <- playing
        match PlaybackService.Current with
        | None when playing ->
            try context.StartForegroundService(new Intent(context, typeof<PlaybackService>)) |> ignore
            with e -> Android.Util.Log.Warn("PaperReader", "could not start playback service: " + e.Message) |> ignore
        | None -> ()
        | Some s -> s.Refresh()

    /// Removes the notification when the reader closes.
    static member End() =
        Playback.playing <- false
        match PlaybackService.Current with
        | None -> ()
        | Some s ->
            s.StopForeground(StopForegroundFlags.Remove)
            s.StopSelf()
